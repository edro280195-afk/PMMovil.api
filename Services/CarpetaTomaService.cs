using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// «Tomar carpeta»: el revisador registra que agarró la carpeta física de una entrada SIN abrir la revisión ni arrancar el
/// cronómetro, y el Monitor de revisadores muestra quién la trae y cuánto lleva sin iniciar.
///
/// Se guarda como una auto-asignación en <c>asignaciones_revision</c> (<c>usuario_asigna = usuario_asignado</c>): esa tabla ya
/// alimenta «Mis pendientes» de la tableta y se completa sola cuando termina la revisión. NO se usa la sesión de
/// <c>traftiemposrevision</c> porque esa fila se cierra sola a los 45 minutos sin actividad, al perder conexión la tablet y al
/// salir de la pantalla: una carpeta tomada a las 9:30 y revisada a las 17:00 desaparecía del registro en minutos.
///
/// OJO: en producción <c>fecha_asignacion</c> es TIMESTAMP con ON UPDATE CURRENT_TIMESTAMP, así que cualquier UPDATE de la
/// fila que no la nombre le pone la hora de ahora y «la tomó hace 7 h» pasaría a «hace 2 min». Todo UPDATE de una toma
/// escribe <c>fecha_asignacion = fecha_asignacion</c> para conservarla.
/// </summary>
public sealed class CarpetaTomaService
{
    private const string ClaveAviso = "toma_aviso_min";
    private const string ClaveCritico = "toma_critico_min";

    private readonly BaseRepository _db;
    private readonly IHubContext<WarehouseHub> _hub;
    private readonly MonitorTvRealtimePublisher _tv;
    private readonly RevisionActivityService _activity;
    private readonly ILogger<CarpetaTomaService> _logger;

    public CarpetaTomaService(
        BaseRepository db,
        IHubContext<WarehouseHub> hub,
        MonitorTvRealtimePublisher tv,
        RevisionActivityService activity,
        ILogger<CarpetaTomaService> logger)
    {
        _db = db;
        _hub = hub;
        _tv = tv;
        _activity = activity;
        _logger = logger;
    }

    private sealed class AsignacionActiva
    {
        public int Id { get; set; }
        public string Usuario { get; set; } = string.Empty;
        public string Asigna { get; set; } = string.Empty;
        public DateTime Fecha { get; set; }
    }

    private sealed class TomaFila
    {
        public int IdAsignacion { get; set; }
        public string Cref { get; set; } = string.Empty;
        public string Usuario { get; set; } = string.Empty;
        public DateTime Fecha { get; set; }
        public long Segundos { get; set; }
    }

    public async Task<TomarCarpetaResultado> TomarAsync(string crefAgrupada, string usuario, bool confirmar)
    {
        string cref = (crefAgrupada ?? string.Empty).Trim();
        if (cref.Length == 0 || string.IsNullOrWhiteSpace(usuario))
        {
            return Rechazo("REFERENCIA_VACIA", "Falta la referencia de la entrada.");
        }

        using IDbConnection conn = _db.CreateConnection();
        conn.Open();

        // Dos revisadores que tocan «Tomar» en el mismo instante no deben quedar los dos como dueños.
        string nombreLock = "toma:" + cref;
        bool conLock = await conn.ExecuteScalarAsync<int?>("SELECT GET_LOCK(@nombre, 5)", new { nombre = nombreLock }) == 1;
        if (!conLock)
        {
            return Rechazo("OCUPADO", "Otra persona está tomando esta carpeta en este momento. Intenta de nuevo.");
        }

        try
        {
            dynamic? entrada = await conn.QueryFirstOrDefaultAsync<object>(@"
                SELECT COUNT(*) AS total, MAX(c.cnombre) AS cliente
                FROM fact f
                LEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0
                WHERE f.cref_agrupada = @cref",
                new { cref });
            if (entrada == null || Convert.ToInt32(entrada.total) == 0)
            {
                return Rechazo("NO_EXISTE", $"No encontré la entrada {cref}.");
            }

            string? nombreCliente = (string?)entrada.cliente;

            bool yaRevisada = await EstaRevisadaAsync(conn, cref);
            string? enRevisionUsuario = await conn.ExecuteScalarAsync<string?>(@"
                SELECT usuario
                FROM traftiemposrevision
                WHERE creferencia = @cref
                  AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                  AND finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000
                  AND usuario IS NOT NULL AND usuario <> ''
                ORDER BY idtiemposrevision DESC
                LIMIT 1",
                new { cref });
            bool enRevisionPropia = enRevisionUsuario != null && Mismo(enRevisionUsuario, usuario);
            bool enRevisionPorOtro = enRevisionUsuario != null && !enRevisionPropia;

            List<AsignacionActiva> activas = (await conn.QueryAsync<AsignacionActiva>(@"
                SELECT id_asignacion AS Id, usuario_asignado AS Usuario, usuario_asigna AS Asigna, fecha_asignacion AS Fecha
                FROM asignaciones_revision
                WHERE cref_agrupada = @cref AND estatus IN (0, 1)
                ORDER BY fecha_asignacion",
                new { cref })).ToList();
            AsignacionActiva? mia = activas.FirstOrDefault(a => Mismo(a.Usuario, usuario));
            AsignacionActiva? deOtro = activas.FirstOrDefault(a => !Mismo(a.Usuario, usuario));

            DecisionToma decision = CarpetaTomaRules.Decidir(
                yaRevisada,
                enRevisionPropia,
                enRevisionPorOtro,
                mia != null,
                deOtro != null,
                confirmar);

            switch (decision)
            {
                case DecisionToma.YaRevisada:
                    return Rechazo(CarpetaTomaRules.Codigo(decision), "Esta entrada ya fue revisada.", nombreCliente);

                case DecisionToma.EnRevisionPropia:
                    return Rechazo(CarpetaTomaRules.Codigo(decision), "Ya la estás revisando.", nombreCliente);

                case DecisionToma.EnRevisionPorOtro:
                    return new TomarCarpetaResultado
                    {
                        Success = false,
                        Codigo = CarpetaTomaRules.Codigo(decision),
                        Mensaje = $"{enRevisionUsuario!.Trim()} la está revisando ahora.",
                        TomadaPor = enRevisionUsuario.Trim(),
                        NombreCliente = nombreCliente
                    };

                case DecisionToma.YaEraTuya:
                    return new TomarCarpetaResultado
                    {
                        Success = true,
                        Codigo = CarpetaTomaRules.Codigo(decision),
                        Mensaje = "Ya la tenías tomada.",
                        TomadaPor = usuario,
                        Desde = Iso(mia!.Fecha),
                        IdAsignacion = mia.Id,
                        NombreCliente = nombreCliente
                    };

                case DecisionToma.PedirConfirmacion:
                    return new TomarCarpetaResultado
                    {
                        Success = false,
                        Codigo = CarpetaTomaRules.Codigo(decision),
                        Mensaje = $"{deOtro!.Usuario.Trim()} la tomó a las {deOtro.Fecha:HH:mm}.",
                        TomadaPor = deOtro.Usuario.Trim(),
                        Desde = Iso(deOtro.Fecha),
                        IdAsignacion = deOtro.Id,
                        NombreCliente = nombreCliente
                    };
            }

            // Nueva o Transferir: la carpeta queda a nombre de quien la toca.
            string? anterior = null;
            if (decision == DecisionToma.Transferir)
            {
                foreach (AsignacionActiva otra in activas.Where(a => !Mismo(a.Usuario, usuario)))
                {
                    await conn.ExecuteAsync(
                        "UPDATE asignaciones_revision SET estatus = 3, fecha_asignacion = fecha_asignacion WHERE id_asignacion = @id",
                        new { id = otra.Id });
                    anterior ??= otra.Usuario.Trim();
                    await NotificarDesasignadaAsync(otra, cref, usuario);
                }
            }

            int idNuevo = await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO asignaciones_revision (cref_agrupada, usuario_asignado, usuario_asigna, prioridad, nota, estatus)
                VALUES (@cref, @usuario, @usuario, 0, 'Carpeta tomada', 0);
                SELECT LAST_INSERT_ID();",
                new { cref, usuario });
            DateTime tomadaEn = await conn.ExecuteScalarAsync<DateTime>(
                "SELECT fecha_asignacion FROM asignaciones_revision WHERE id_asignacion = @id",
                new { id = idNuevo });

            await PublicarTomaAsync(cref, usuario, tomadaEn, nombreCliente, anterior);

            return new TomarCarpetaResultado
            {
                Success = true,
                Codigo = CarpetaTomaRules.Codigo(decision),
                Mensaje = anterior == null ? "Carpeta tomada." : $"Carpeta tomada (antes la tenía {anterior}).",
                TomadaPor = usuario,
                Desde = Iso(tomadaEn),
                IdAsignacion = idNuevo,
                NombreCliente = nombreCliente
            };
        }
        finally
        {
            await conn.ExecuteScalarAsync<int?>("SELECT RELEASE_LOCK(@nombre)", new { nombre = nombreLock });
        }
    }

    /// <summary>El revisador devuelve una carpeta que tomó por error. Solo suelta SUS tomas, nunca una asignación del supervisor.</summary>
    public async Task<bool> SoltarAsync(string crefAgrupada, string usuario)
    {
        string cref = (crefAgrupada ?? string.Empty).Trim();
        if (cref.Length == 0 || string.IsNullOrWhiteSpace(usuario))
        {
            return false;
        }

        using IDbConnection conn = _db.CreateConnection();
        int filas = await conn.ExecuteAsync(@"
            UPDATE asignaciones_revision
            SET estatus = 3, fecha_asignacion = fecha_asignacion
            WHERE cref_agrupada = @cref
              AND usuario_asignado = @usuario
              AND usuario_asigna = usuario_asignado
              AND estatus IN (0, 1)",
            new { cref, usuario });
        if (filas == 0)
        {
            return false;
        }

        await _activity.RegistrarAsync(cref, usuario, RevisionActivityTypes.CarpetaSoltada, detalle: "Carpeta devuelta sin revisar");
        await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync("onCarpetaSoltada", new { creferencia = cref, usuario });
        await _tv.CarpetaSoltadaAsync(cref, usuario);
        await _hub.Clients.All.SendAsync("onActividadRecibida", cref, usuario, string.Empty, string.Empty);
        return true;
    }

    /// <summary>Las carpetas tomadas que todavía no inician revisión, de la más antigua a la más reciente.</summary>
    public async Task<CarpetasTomadasResponse> ListarSinIniciarAsync()
    {
        using IDbConnection conn = _db.CreateConnection();
        (int aviso, int critico) = await LeerUmbralesAsync(conn);

        var respuesta = new CarpetasTomadasResponse
        {
            AvisoMin = aviso,
            CriticoMin = critico,
            ServerTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
        };

        List<TomaFila> tomas = (await conn.QueryAsync<TomaFila>(@"
            SELECT id_asignacion AS IdAsignacion,
                   TRIM(cref_agrupada) AS Cref,
                   usuario_asignado AS Usuario,
                   fecha_asignacion AS Fecha,
                   TIMESTAMPDIFF(SECOND, fecha_asignacion, NOW()) AS Segundos
            FROM asignaciones_revision
            WHERE usuario_asigna = usuario_asignado AND estatus IN (0, 1)
            ORDER BY fecha_asignacion")).ToList();
        if (tomas.Count == 0)
        {
            return respuesta;
        }

        List<string> refs = tomas.Select(t => t.Cref).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Una toma deja de estar «sin iniciar» cuando su dueño arranca el cronómetro después de tomarla.
        var iniciadas = (await conn.QueryAsync<(string Cref, string Usuario, DateTime Inicio)>(@"
            SELECT creferencia, usuario, MAX(finiciorevision)
            FROM traftiemposrevision
            WHERE creferencia IN @refs
              AND finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000
            GROUP BY creferencia, usuario",
            new { refs })).ToList();

        HashSet<string> revisadas = (await conn.QueryAsync<string>(
            "SELECT creferencia FROM traf WHERE creferencia IN @refs AND status >= 2",
            new { refs })).Select(r => r.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string> clientes = (await conn.QueryAsync<(string Cref, string? Nombre)>(@"
            SELECT TRIM(f.cref_agrupada), MAX(c.cnombre)
            FROM fact f
            LEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0
            WHERE f.cref_agrupada IN @refs
            GROUP BY TRIM(f.cref_agrupada)",
            new { refs }))
            .ToDictionary(c => c.Cref, c => c.Nombre ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        List<TomaFila> vigentes = tomas
            .Where(t => !revisadas.Contains(t.Cref))
            .Where(t => !iniciadas.Any(i =>
                string.Equals(i.Cref.Trim(), t.Cref, StringComparison.OrdinalIgnoreCase)
                && Mismo(i.Usuario, t.Usuario)
                && i.Inicio >= t.Fecha))
            .ToList();
        if (vigentes.Count == 0)
        {
            return respuesta;
        }

        Dictionary<string, string> nombres = await LeerNombresAsync(vigentes.Select(t => t.Usuario.Trim()).Distinct().ToList());

        foreach (TomaFila toma in vigentes)
        {
            respuesta.Items.Add(new CarpetaTomadaItem
            {
                IdAsignacion = toma.IdAsignacion,
                CrefAgrupada = toma.Cref,
                Usuario = toma.Usuario,
                NombreUsuario = nombres.TryGetValue(toma.Usuario.Trim(), out string? nombre) ? nombre : toma.Usuario.Trim(),
                NombreCliente = clientes.TryGetValue(toma.Cref, out string? cliente) ? cliente : string.Empty,
                TomadaEn = Iso(toma.Fecha),
                SegundosSinIniciar = Math.Max(0, toma.Segundos),
                Nivel = CarpetaTomaRules.Nivel(toma.Segundos, aviso, critico)
            });
        }

        return respuesta;
    }

    /// <summary>
    /// Cierra las tomas de entradas que ya se revisaron (otra persona la terminó, o la terminó el cierre automático): si no, la
    /// toma se queda pegada para siempre. Lo corre RevisionHygieneService cada minuto.
    /// </summary>
    public async Task<int> CerrarTomasDeEntradasRevisadasAsync()
    {
        using IDbConnection conn = _db.CreateConnection();
        List<(int Id, string Cref)> abiertas = (await conn.QueryAsync<(int Id, string Cref)>(@"
            SELECT id_asignacion, TRIM(cref_agrupada)
            FROM asignaciones_revision
            WHERE usuario_asigna = usuario_asignado AND estatus IN (0, 1)")).ToList();
        if (abiertas.Count == 0)
        {
            return 0;
        }

        List<string> refs = abiertas.Select(a => a.Cref).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        HashSet<string> revisadas = (await conn.QueryAsync<string>(
            "SELECT creferencia FROM traf WHERE creferencia IN @refs AND status >= 2",
            new { refs })).Select(r => r.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<int> ids = abiertas.Where(a => revisadas.Contains(a.Cref)).Select(a => a.Id).ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        return await conn.ExecuteAsync(@"
            UPDATE asignaciones_revision
            SET estatus = 2, fecha_completada = NOW(), fecha_asignacion = fecha_asignacion
            WHERE id_asignacion IN @ids AND estatus IN (0, 1)",
            new { ids });
    }

    /// <summary>
    /// Pone «tomada por X desde …» a las entradas que alguien trae: la respuesta a «¿quién trae la 123?» desde la tableta.
    /// </summary>
    public static async Task AdjuntarTomasAsync(IDbConnection conn, IReadOnlyCollection<EntradaPendiente> entradas)
    {
        if (entradas.Count == 0)
        {
            return;
        }

        List<string> refs = entradas.Select(e => (e.CrefAgrupada ?? string.Empty).Trim())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (refs.Count == 0)
        {
            return;
        }

        List<TomaFila> tomas;
        try
        {
            tomas = (await conn.QueryAsync<TomaFila>(@"
                SELECT id_asignacion AS IdAsignacion,
                       TRIM(cref_agrupada) AS Cref,
                       usuario_asignado AS Usuario,
                       fecha_asignacion AS Fecha,
                       TIMESTAMPDIFF(SECOND, fecha_asignacion, NOW()) AS Segundos
                FROM asignaciones_revision
                WHERE cref_agrupada IN @refs
                  AND usuario_asigna = usuario_asignado
                  AND estatus IN (0, 1)
                ORDER BY fecha_asignacion",
                new { refs })).ToList();
        }
        catch
        {
            // Que no se pueda saber quién la trae nunca debe impedir listar las entradas.
            return;
        }

        foreach (EntradaPendiente entrada in entradas)
        {
            TomaFila? toma = tomas.FirstOrDefault(t =>
                string.Equals(t.Cref, (entrada.CrefAgrupada ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
            if (toma == null)
            {
                continue;
            }

            entrada.TomadaPor = toma.Usuario.Trim();
            entrada.TomadaDesde = Iso(toma.Fecha);
            entrada.SegundosDesdeToma = Math.Max(0, toma.Segundos);
        }
    }

    private async Task<bool> EstaRevisadaAsync(IDbConnection conn, string cref)
    {
        int status = await conn.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(status), 0) FROM traf WHERE creferencia = @cref",
            new { cref });
        if (status >= 2)
        {
            return true;
        }

        // Mismas dos reglas con las que TimerController.IniciarTimer se niega a iniciar una revisión terminada.
        int total = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM fact WHERE cref_agrupada = @cref", new { cref });
        int pendientes = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM fact WHERE cref_agrupada = @cref AND (status_rev IS NULL OR status_rev != 1)",
            new { cref });
        if (total > 0 && pendientes == 0)
        {
            return true;
        }

        return await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM (
                SELECT ffinrevision, estatus_final
                FROM traftiemposrevision
                WHERE creferencia = @cref
                ORDER BY idtiemposrevision DESC
                LIMIT 1
            ) ultima
            WHERE ultima.ffinrevision IS NOT NULL AND ultima.estatus_final IN (1, 2)",
            new { cref }) > 0;
    }

    private async Task PublicarTomaAsync(string cref, string usuario, DateTime tomadaEn, string? nombreCliente, string? anterior)
    {
        try
        {
            await _activity.RegistrarAsync(
                cref,
                usuario,
                RevisionActivityTypes.CarpetaTomada,
                detalle: anterior == null ? "Carpeta tomada" : $"Carpeta tomada (antes la tenía {anterior})");
            await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync("onCarpetaTomada", new
            {
                creferencia = cref,
                usuario,
                tomadaEn = Iso(tomadaEn),
                nombreCliente = nombreCliente ?? string.Empty,
                anterior
            });
            await _tv.CarpetaTomadaAsync(cref, usuario, Iso(tomadaEn));
            await _hub.Clients.All.SendAsync("onActividadRecibida", cref, usuario, string.Empty, string.Empty);
        }
        catch (Exception exception)
        {
            // La toma ya quedó guardada; un aviso en vivo que falla no debe deshacerla ni mostrarle un error al revisador.
            _logger.LogWarning(exception, "La toma de {Cref} por {Usuario} se guardó sin aviso en vivo.", cref, usuario);
        }
    }

    private async Task NotificarDesasignadaAsync(AsignacionActiva asignacion, string cref, string quienToma)
    {
        try
        {
            await _hub.Clients.Group("USER_" + asignacion.Usuario).SendAsync("onRevisionDesasignada", new
            {
                idAsignacion = asignacion.Id,
                crefAgrupada = cref,
                supervisor = quienToma
            });
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No se pudo avisar a {Usuario} que su carpeta {Cref} pasó a {Otro}.", asignacion.Usuario, cref, quienToma);
        }
    }

    private static async Task<(int Aviso, int Critico)> LeerUmbralesAsync(IDbConnection conn)
    {
        try
        {
            var filas = (await conn.QueryAsync<(string Clave, string Valor)>(
                "SELECT clave, valor FROM monitor_config WHERE clave IN (@a, @c)",
                new { a = ClaveAviso, c = ClaveCritico })).ToList();
            int? aviso = filas.Where(f => f.Clave == ClaveAviso).Select(f => int.TryParse(f.Valor, out int v) ? v : (int?)null).FirstOrDefault();
            int? critico = filas.Where(f => f.Clave == ClaveCritico).Select(f => int.TryParse(f.Valor, out int v) ? v : (int?)null).FirstOrDefault();
            return CarpetaTomaRules.NormalizarUmbrales(aviso, critico);
        }
        catch
        {
            // La tabla de configuración la crea RevisionHygieneService; si todavía no existe, valen los de siempre.
            return (CarpetaTomaRules.AvisoMinPorOmision, CarpetaTomaRules.CriticoMinPorOmision);
        }
    }

    private async Task<Dictionary<string, string>> LeerNombresAsync(List<string> usuarios)
    {
        try
        {
            // La tabla usuarios con nombre y apellido está en pmmtraf (la misma que usa «revisores-disponibles»).
            using IDbConnection conn = _db.CreateConnection();
            return (await conn.QueryAsync<(string Clave, string Nombre)>(@"
                SELECT TRIM(claveusuario), TRIM(CONCAT_WS(' ', TRIM(nombre), TRIM(apaterno)))
                FROM usuarios
                WHERE TRIM(claveusuario) IN @usuarios",
                new { usuarios }))
                .Where(u => !string.IsNullOrWhiteSpace(u.Nombre))
                .GroupBy(u => u.Clave, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Nombre, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No se pudieron leer los nombres de los revisadores.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static TomarCarpetaResultado Rechazo(string codigo, string mensaje, string? nombreCliente = null) => new()
    {
        Success = false,
        Codigo = codigo,
        Mensaje = mensaje,
        NombreCliente = nombreCliente
    };

    private static bool Mismo(string? a, string? b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Iso(DateTime fecha) => fecha.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
}
