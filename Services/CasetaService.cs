using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Dapper;

using MailKit.Security;

using MimeKit;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public partial class CasetaService
{
    // Correo de entrada de caseta: en Development se redirige a Desarrollo
    // (mismo patrón que EntradaService.SmtpDirectoAsync); en el resto de los
    // entornos va a los destinatarios reales de GetEntradaCasetaRecipients.
    // La copia (calidad + los dos correos de desarrollo) replica la que ya
    // usaba PMM_Caseta (Caseta.aspx.cs -> EnviarCorreo), solo actualizando el
    // dominio de imexfwd.com a grupopmm.com.
    private const string CorreoPruebasDesarrollo = "desarrollo3@grupopmm.com";
    private const string CorreoCalidad = "calidad@grupopmm.com";
    private const string CorreoDesarrolloPrincipal = "desarrollo@grupopmm.com";

    private readonly BaseRepository _db;
    private readonly ISmtpService _smtp;
    private readonly IConfiguration _config;
    private readonly ILogger<CasetaService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CasetaPhotoStorage _photoStorage;
    private readonly ICasetaShareService _casetaShareService;
    private readonly IHubContext<WarehouseHub> _hub;
    private readonly IHostEnvironment _env;
    private readonly ITransporteCorreo _transporte;
    private readonly ICorreoSalienteCola _cola;
    private readonly ICorreoEntradaDiferido _diferido;

    public CasetaService(
       BaseRepository db,
       ISmtpService smtp,
       IConfiguration config,
        ILogger<CasetaService> logger,
        IHttpClientFactory httpClientFactory,
        CasetaPhotoStorage photoStorage,
        ICasetaShareService casetaShareService,
        IHubContext<WarehouseHub> hub,
        IHostEnvironment env,
        ITransporteCorreo transporte,
        ICorreoSalienteCola cola,
        ICorreoEntradaDiferido diferido)
    {
        _db = db;
        _smtp = smtp;
        _config = config;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _photoStorage = photoStorage;
        _casetaShareService = casetaShareService;
        _hub = hub;
        _env = env;
        _transporte = transporte;
        _cola = cola;
        _diferido = diferido;
    }

    /// <summary>
    /// Avisa por SignalR que la garita se movio, para que el panel web refresque
    /// al momento en vez de esperar su vuelta de reloj. Nunca revienta la
    /// operacion: si el hub falla, la entrada o la salida ya quedaron guardadas
    /// y eso es lo que importa.
    /// </summary>
    private async Task NotificarMovimientoCasetaAsync(
        string tipo, string folio, string usuario, CancellationToken ct = default)
    {
        try
        {
            await _hub.Clients.All.SendAsync("onCasetaMovimiento", new
            {
                Tipo = tipo,          // ENTRADA | SALIDA
                Folio = folio,        // casetaid en entrada, relno en salida
                Usuario = usuario,
                Fecha = DateTime.Now.ToString("s")
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "No se pudo notificar por SignalR el movimiento de caseta {Tipo} {Folio}.", tipo, folio);
        }
    }

    private static (string tabla, string sufijo) ResolverTabla(string relno)
    {
        char s = relno.Length > 2 ? relno[2] : 'I';
        string tabla = s == 'C' ? "relacion_imex" : "relacion";
        return (tabla, s == 'C' ? "C" : "I");
    }


    /// <summary>
    /// Cola de relaciones de caseta. Tiene dos modos deliberadamente distintos:
    ///
    /// - <paramref name="fecha"/> null (el caso normal): la cola EN VIVO, o sea
    ///   lo que sigue autorizado y todavia no cruza. No depende de ninguna fecha
    ///   porque por definicion es del presente.
    /// - <paramref name="fecha"/> con valor: la cola HISTORICA de ese dia, o sea
    ///   todo lo que se autorizo esa fecha, sin importar en que estatus quedo.
    ///   Filtrar el modo historico por ESTATUS_CARGA='AUTORIZADA' devolveria casi
    ///   siempre vacio: lo que ya salio quedo en 'SALIDA'.
    ///
    /// El consumidor distingue los dos modos por <c>PendientesListResult.EsHistorico</c>
    /// y cambia el rotulo de la tarjeta; antes el panel mandaba ?fecha= y el API
    /// lo ignoraba, asi que la pantalla decia una fecha y ensenaba otra.
    /// </summary>
    public async Task<List<CasetaPendiente>> GetPendientesAsync(DateTime? fecha = null)
    {
        const string sql = @"
   SELECT relno, caja, usuautoriza, autoriza_carga AS AutorizaCarga,
     cidtransfer AS CidTransfer, num_tractor AS NumTractor, op_transfer AS OpTransfer,
     gafete_op AS GafeteOp, estatus_carga AS Estatus, salida_caseta AS SalidaCaseta,
     TRIM(CONCAT_WS(' ', IFNULL(sellos,''), IFNULL(sellos2,''), IFNULL(sellos3,''), IFNULL(sellos4,''))) AS Sellos,
     IFNULL((SELECT MAX(c.cnombre) FROM traf2 t2
  LEFT JOIN traf t ON t2.creferencia=t.creferencia
  LEFT JOIN clientes c ON t.cclienteidtraf=c.cidcte
  WHERE t2.nrelacion=r.relno), '') AS cliente
   FROM relacion r
   WHERE TIPOP='I'
     AND ((@fecha IS NULL AND ESTATUS_CARGA='AUTORIZADA' AND YEAR(fcruce)=0)
       OR (@fecha IS NOT NULL AND DATE(autoriza_carga) = @fecha))
   UNION
   SELECT relno, caja, usuautoriza, autoriza_carga AS AutorizaCarga,
     cidtransfer AS CidTransfer, num_tractor AS NumTractor, op_transfer AS OpTransfer,
     gafete_op AS GafeteOp, estatus_carga AS Estatus, salida_caseta AS SalidaCaseta,
     TRIM(CONCAT_WS(' ', IFNULL(sellos,''), IFNULL(sellos2,''), IFNULL(sellos3,''), IFNULL(sellos4,''))) AS Sellos,
     IFNULL((SELECT MAX(c.cnombre) FROM traf2 t2
  LEFT JOIN agrupa ag ON t2.creferencia=ag.cref_agrupada
  LEFT JOIN traf t ON ag.cref_agrupada=t.creferencia
  LEFT JOIN clientes c ON t.cclienteidtraf=c.cidcte
  WHERE t2.nrelacion=r.relno), '') AS cliente
   FROM relacion_imex r
   WHERE TIPOP='C'
     AND ((@fecha IS NULL AND ESTATUS_CARGA='AUTORIZADA' AND YEAR(fcruce)=0)
       OR (@fecha IS NOT NULL AND DATE(autoriza_carga) = @fecha))
   ORDER BY AutorizaCarga DESC";

        using IDbConnection conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<CasetaPendiente>(
            sql, new { fecha = fecha?.Date })).ToList();
        return rows;
    }


    public async Task<CasetaResult> GetDetalleAsync(string relnoOrCasetaId)
    {
        var result = new CasetaResult();
        string relno = relnoOrCasetaId?.Trim() ?? "";

        using IDbConnection conn = _db.CreateConnection();

        // Inteligencia de escaneo: Si escanean la etiqueta de caseta (UUID), resolvemos su relno
        if (relno.Length >= 32)
        {
            var resolvedRelno = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT relno FROM caseta WHERE casetaid = @casetaId",
                new { casetaId = relno });

            if (!string.IsNullOrEmpty(resolvedRelno))
            {
                relno = resolvedRelno;
            }
            else
            {
                result.Message = "ETIQUETA DE CASETA SIN RELACION ASIGNADA";
                return result;
            }
        }

        var (tabla, _) = ResolverTabla(relno);

        var row = await conn.QueryFirstOrDefaultAsync<CasetaDetalleDbRow>(
        $@"SELECT r.*, r.estatus_carga AS EstatusCarga, t.cnombre
 FROM {tabla} r
 LEFT JOIN tranfers t ON r.cidtransfer=t.cidtransfer
 WHERE r.relno=@relno",
        new { relno });

        if (row == null)
        {
            result.Message = "RELACION NO ENCONTRADA";
            return result;
        }

        if (!string.IsNullOrEmpty(row.Usuedit))
        {
            result.Message = "RELACION OCUPADA";
            return result;
        }

        if (row.EstatusCarga != "AUTORIZADA" && row.EstatusCarga != "SALIDA")
        {
            result.Message = "RELACION INCOMPLETA";
            return result;
        }

        if (row.EstatusCarga == "SALIDA")
        {
            result.Message = "SALIDA YA AUTORIZADA";
            return result;
        }

        string sellos = JoinSellos(row.Sellos, row.Sellos2, row.Sellos3, row.Sellos4);

        result.Success = true;
        result.Message = "COMPLETA";
        result.Detalle = new CasetaDetalle
        {
            Relno = row.Relno,
            Estatus = row.STATUS,
            EstatusCarga = row.EstatusCarga,
            Transfer = row.Cnombre ?? "",
            Operador = row.Op_transfer ?? "",
            Caja = row.Caja ?? "",
            Sellos = sellos,
            Tractor = row.Num_tractor ?? "",
            Gafete = row.Gafete_op ?? "",
            Cliente = "",
            IdCliente = ""
        };

        return result;
    }

    private static string JoinSellos(string? s1, string? s2, string? s3, string? s4)
    {
        var parts = new[] { s1, s2, s3, s4 };
        return string.Join(" ", parts.Where(s => !string.IsNullOrEmpty(s)));
    }


    /// <summary>Tope de fotos de una salida y de fotos por envío (la app manda lotes de 4, igual que Entrada).</summary>
    private const int MaxFotosSalida = 40;
    private const int MaxFotosPorLoteSalida = 6;

    /// <summary>
    /// Guarda un lote de fotos de salida antes de confirmar la salida. Solo se aceptan mientras la relación sigue
    /// autorizada y sin salida; repetir un lote reemplaza las fotos de esas posiciones, así que reintentar es seguro.
    /// </summary>
    public async Task<ConfirmarResult> SubirFotosSalidaAsync(
        string relno, int indiceInicial, List<string>? fotos, string usuario, CancellationToken ct = default)
    {
        string limpio = (relno ?? string.Empty).Trim();
        if (limpio.Length == 0) return new ConfirmarResult { Success = false, Message = "La relación es obligatoria." };
        if (fotos == null || fotos.Count == 0) return new ConfirmarResult { Success = false, Message = "No llegó ninguna foto." };
        if (fotos.Count > MaxFotosPorLoteSalida)
            return new ConfirmarResult { Success = false, Message = $"Máximo {MaxFotosPorLoteSalida} fotos por envío." };
        if (indiceInicial < 1 || indiceInicial + fotos.Count - 1 > MaxFotosSalida)
            return new ConfirmarResult { Success = false, Message = $"La salida admite hasta {MaxFotosSalida} fotos." };

        // Se decodifica todo antes de abrir la transacción: una foto dañada no debe dejar el lote a medias.
        var decodificadas = new List<(int Indice, byte[] Bytes)>();
        for (int i = 0; i < fotos.Count; i++)
        {
            int indice = indiceInicial + i;
            string f = fotos[i] ?? string.Empty;
            string b64 = f.Contains(',') ? f.Split(',')[1] : f;
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(b64);
            }
            catch (FormatException)
            {
                return new ConfirmarResult { Success = false, Message = $"La foto {indice} no es válida." };
            }

            if (bytes.Length == 0) return new ConfirmarResult { Success = false, Message = $"La foto {indice} llegó vacía." };
            decodificadas.Add((indice, bytes));
        }

        var (tabla, _) = ResolverTabla(limpio);
        using IDbConnection conn = _db.CreateConnection();
        conn.Open();

        var registro = await conn.QueryFirstOrDefaultAsync<RelacionSalidaRow>(new CommandDefinition(
            $@"SELECT estatus_carga AS Estatus,
                      CASE WHEN YEAR(salida_caseta) >= 2000 THEN salida_caseta END AS SalidaCaseta,
                      IFNULL(usumodi, '') AS Usuario
               FROM {tabla}
               WHERE relno = @relno", new { relno = limpio }, cancellationToken: ct));
        if (registro == null) return new ConfirmarResult { Success = false, Message = $"No existe la relación {limpio}." };
        if (!string.Equals(registro.Estatus?.Trim(), "AUTORIZADA", StringComparison.OrdinalIgnoreCase) || registro.SalidaCaseta != null)
            return new ConfirmarResult { Success = false, Message = "La relación ya salió o no tiene carga autorizada. Actualiza la ficha." };

        using var tx = conn.BeginTransaction();
        var staged = new List<CasetaPhotoStage>();
        try
        {
            var existentes = (await conn.QueryAsync<int>(new CommandDefinition(
                "SELECT nlineno FROM fotos WHERE creferencia = @relno AND ntipodocto = 101",
                new { relno = limpio }, tx, cancellationToken: ct))).ToHashSet();

            foreach ((int indice, byte[] bytes) in decodificadas)
            {
                if (existentes.Contains(indice))
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE fotos SET cextension = 'jpg', usucrea = @usuario, fcrea = @ahora WHERE creferencia = @relno AND ntipodocto = 101 AND nlineno = @indice",
                        new { relno = limpio, indice, usuario, ahora = DateTime.Now }, tx, cancellationToken: ct));
                }
                else
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO fotos (nlineno, creferencia, ntipodocto, cextension, usucrea, fcrea) VALUES (@indice, @relno, 101, 'jpg', @usuario, @ahora)",
                        new { relno = limpio, indice, usuario, ahora = DateTime.Now }, tx, cancellationToken: ct));
                    existentes.Add(indice);
                }

                staged.Add(await _photoStorage.StageAsync(bytes, _photoStorage.GetFileName(limpio, indice), ct));
            }

            tx.Commit();
            _photoStorage.ActivateStagedPhotos(staged);
            return new ConfirmarResult { Success = true, Message = $"{decodificadas.Count} foto(s) guardada(s)." };
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            _photoStorage.CleanupStagedPhotos(staged);
            throw;
        }
    }

    public async Task<ConfirmarResult> ConfirmarSalidaAsync(string relno, string usuario, string observaciones = "", List<string>? fotos = null, int? totalFotos = null, CancellationToken ct = default)
    {
        var result = new ConfirmarResult();
        var (tabla, sufijo) = ResolverTabla(relno);
        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using IDbConnection conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        var fotosStaged = new List<CasetaPhotoStage>();
        var fotosSobrantes = new List<int>();
        bool committed = false;

        try
        {
            int changed = await conn.ExecuteAsync(new CommandDefinition(
        $@"UPDATE {tabla}
     SET estatus_carga='SALIDA', usumodi=@usuario, fmodi=@time, salida_caseta=@time
     WHERE relno=@relno AND estatus_carga='AUTORIZADA'
       AND TRIM(COALESCE(usuedit, '')) = ''
       AND (salida_caseta IS NULL OR YEAR(salida_caseta) < 2000)
       AND (fcruce IS NULL OR YEAR(fcruce) < 2000)",
        new { usuario, time = timeStr, relno }, tx, cancellationToken: ct));

            if (changed != 1)
            {
                tx.Rollback();
                return new ConfirmarResult { Success = false, Message = "La relación ya salió, está ocupada o no tiene carga autorizada. Actualiza la ficha." };
            }

            await conn.ExecuteAsync(new CommandDefinition(
        @"UPDATE caseta
    SET fsalida=@time, estatus='SALIDA', ususalida=@usuario, obs_salida=@observaciones
    WHERE relacion=@relno AND (fsalida IS NULL OR YEAR(fsalida) < 2000)",
        new { time = timeStr, usuario, relno, observaciones }, tx, cancellationToken: ct));

            if (fotos != null && fotos.Count > 0)
            {
                await conn.ExecuteAsync(
                    "DELETE FROM fotos WHERE creferencia = @relno AND ntipodocto = 101",
                    new { relno }, tx);

                for (int i = 0; i < fotos.Count; i++)
                {
                    string f = fotos[i];
                    if (string.IsNullOrWhiteSpace(f)) continue;

                    string b64 = f.Contains(',') ? f.Split(',')[1] : f;
                    byte[] bytes = Convert.FromBase64String(b64);
                    int nlineno = i + 1;

                    await conn.ExecuteAsync(@"
INSERT INTO fotos (nlineno, creferencia, ntipodocto, cextension, usucrea, fcrea)
VALUES (@nlineno, @creferencia, 101, 'jpg', @usucrea, @fcrea)",
                    new {
                        nlineno,
                        creferencia = relno,
                        usucrea = usuario,
                        fcrea = DateTime.Now
                    }, tx);

                    string fileName = _photoStorage.GetFileName(relno, nlineno);
                    fotosStaged.Add(await _photoStorage.StageAsync(bytes, fileName, ct));
                }
            }
            else if (totalFotos.HasValue)
            {
                // Las fotos ya se subieron por lotes (SubirFotosSalidaAsync): aqui solo se comprueba que esten todas y se
                // quitan las que sobren de un intento anterior con mas fotos.
                int total = Math.Clamp(totalFotos.Value, 0, MaxFotosSalida);
                int presentes = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(DISTINCT nlineno) FROM fotos WHERE creferencia = @relno AND ntipodocto = 101 AND nlineno BETWEEN 1 AND @total",
                    new { relno, total }, tx);
                if (presentes < total)
                {
                    tx.Rollback();
                    return new ConfirmarResult { Success = false, Message = $"Faltan fotos por subir ({presentes} de {total}). Vuelve a intentar." };
                }

                fotosSobrantes = (await conn.QueryAsync<int>(
                    "SELECT nlineno FROM fotos WHERE creferencia = @relno AND ntipodocto = 101 AND nlineno > @total",
                    new { relno, total }, tx)).ToList();
                if (fotosSobrantes.Count > 0)
                {
                    await conn.ExecuteAsync(
                        "DELETE FROM fotos WHERE creferencia = @relno AND ntipodocto = 101 AND nlineno > @total",
                        new { relno, total }, tx);
                }
            }

            tx.Commit();
            committed = true;
            _photoStorage.ActivateStagedPhotos(fotosStaged);
            foreach (int sobrante in fotosSobrantes)
            {
                _photoStorage.QueueDelete(_photoStorage.GetFileName(relno, sobrante));
            }

            try
            {
                await NotiSalidaCasetaAsync(conn, relno, sufijo, usuario, timeStr, observaciones);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Notificación de salida caseta falló para {Relno}, la salida ya se confirmó", relno);
            }

            await NotificarMovimientoCasetaAsync("SALIDA", relno, usuario, ct);

            result.Success = true;
            result.Message = "SALIDA CONFIRMADA";
        }
        catch
        {
            if (!committed)
            {
                tx.Rollback();
                _photoStorage.CleanupStagedPhotos(fotosStaged);
            }
            throw;
        }

        return result;
    }


    /// <summary>
    /// Correos fijos que reciben toda salida de caseta, igual que el sistema de escritorio (Relacion.NotiSalidaCaseta).
    /// desarrollo3, que alla iba en "Para", aqui llega por la copia oculta automatica de SmtpService.
    /// </summary>
    private static readonly string[] CorreosFijosSalidaCaseta =
    {
        "tatis.guevara@grupopmm.com",
        "despacho.nld@grupopmm.com",
        "notificacionespmmlaredotx@imexnotify.com"
    };

    private const string ClienteSinIdNotificacion = "SIN_CLIENTE";

    /// <summary>
    /// A quien le toca la notificacion de salida de un cliente: sus contactos activos que piden aviso de salida de caja
    /// (`correoscl.not_salida_caja = 1`, con el tipo de operacion vacio o con C) mas los correos fijos. Es la misma regla
    /// de Relacion.GetCorreosPorTrafico del sistema de escritorio.
    /// </summary>
    private async Task<List<string>> ObtenerDestinatariosSalidaAsync(IDbConnection conn, string clienteId)
    {
        var destinatarios = new List<string>();

        void Agregar(string? correo)
        {
            string limpio = (correo ?? string.Empty).Trim();
            if (limpio.Length > 0 && !destinatarios.Contains(limpio, StringComparer.OrdinalIgnoreCase))
                destinatarios.Add(limpio);
        }

        if (!string.IsNullOrWhiteSpace(clienteId) && clienteId != ClienteSinIdNotificacion)
        {
            var contactos = await conn.QueryAsync<string>(
                @"SELECT cemail
                  FROM correoscl
                  WHERE inactivo = 0
                    AND not_salida_caja = 1 AND (tipoop = '' OR tipoop LIKE '%C%')
                    AND cidcte = @clienteId",
                new { clienteId });
            foreach (string contacto in contactos) Agregar(contacto);
        }

        foreach (string fijo in CorreosFijosSalidaCaseta) Agregar(fijo);
        return destinatarios;
    }

    /// <summary>
    /// Manda la notificacion de salida: un correo por cliente de la relacion, con los contactos de ese cliente mas los
    /// fijos, como el sistema de escritorio. Cada cliente recibe solo sus pedimentos. Con <paramref name="destinatariosOverride"/>
    /// el correo va unicamente ahi (prueba), sin las copias ocultas automaticas; los destinatarios reales se calculan
    /// igual y se devuelven para poder validarlos.
    /// </summary>
    private async Task<List<SalidaNotificacionEnviada>> NotiSalidaCasetaAsync(
        IDbConnection conn,
        string relno,
        string sufijo,
        string usuario,
        string timeStr,
        string observaciones,
        IEnumerable<string>? destinatariosOverride = null,
        bool incluirEnlaceFotos = true,
        bool exigirEnlace = false)
    {
        var enviados = new List<SalidaNotificacionEnviada>();
        var rel = await GetDetalleRawAsync(conn, relno, sufijo);
        if (rel == null) return enviados;

        string caja = rel.Caja ?? "";
        string transfer = rel.Cnombre ?? "";
        string operador = rel.Op_transfer ?? "";
        string tractor = rel.Num_tractor ?? "";
        string gafete = rel.Gafete_op ?? "";
        string fecha = DateTime.Parse(timeStr).ToString("dd/MM/yyyy HH:mm:ss");

        var traficos = (await conn.QueryAsync<TraficoNotiRow>(
     @"SELECT traf.creferencia, cclienteidtraf, c.cnombre, cidmanejo, pedimento
  FROM traf
  LEFT JOIN clientes c ON cclienteidtraf=c.cidcte
  LEFT JOIN traf2 ON traf.creferencia=traf2.creferencia
  WHERE nrelacion=@relno",
     new { relno })).ToList();

        if (traficos.Count == 0) return enviados;

        var grupos = traficos
     .Where(t => t.Cidmanejo != "PIPA")
     .GroupBy(t => t.Cclienteidtraf ?? ClienteSinIdNotificacion)
     .ToList();

        if (grupos.Count == 0) return enviados;

        // Mismo asunto que la plantilla SALIDACASETA del sistema de escritorio (reportes.config_correo): "Salida bodega Imex, caja: [caja]".
        string subject = $"Salida bodega Imex, caja: {caja}";

        // El enlace de fotos es uno por relacion: se crea una vez y va en el correo de cada cliente.
        string bloqueFotos = string.Empty;
        if (incluirEnlaceFotos)
        {
            // Si el enlace falla, las notificaciones salen sin el bloque de fotos: el error queda en el log, pero a los
            // contactos del cliente les llega el aviso de la salida.
            try
            {
                var enlaceFotos = await _casetaShareService.GenerarLinkSalidaAsync(relno, usuario);
                if (enlaceFotos.HasValue)
                {
                    string baseUrl = (_config["PublicLinks:ApiBaseUrl"] ?? string.Empty).TrimEnd('/');
                    if (baseUrl.Length == 0)
                    {
                        throw new InvalidOperationException("Falta configurar PublicLinks:ApiBaseUrl para compartir las fotos de salida.");
                    }

                    string url = $"{baseUrl}/api/publico/caseta/{enlaceFotos.Value.Token}";
                    bloqueFotos = CasetaShareHtmlRenderer.RenderEmailLink(url, CasetaShareService.TipoSalida, enlaceFotos.Value.Expira);
                }
            }
            catch (Exception ex)
            {
                // El reenvio manual pide el enlace a proposito: ahi es mejor no mandar nada que mandarlo sin el.
                if (exigirEnlace) throw;
                _logger.LogError(ex, "Notificacion de salida de {Relno}: no se pudo crear el enlace de fotos; los correos salen sin el.", relno);
            }
        }

        foreach (var grupo in grupos)
        {
            string nombreCliente = grupo.First().Cnombre ?? grupo.Key;
            List<string> reales = await ObtenerDestinatariosSalidaAsync(conn, grupo.Key);
            List<string> aQuien = destinatariosOverride?.ToList() ?? reales;
            bool exito = false;

            try
            {
                string body = BuildSalidaCasetaEmailBody(relno, caja, transfer, operador, tractor, gafete, fecha, observaciones,
                    new List<IGrouping<string, TraficoNotiRow>> { grupo });
                if (bloqueFotos.Length > 0) body = CasetaShareHtmlRenderer.InsertarEnCorreo(body, bloqueFotos);

                // preferirCola: el correo se guarda en la cola durable y la confirmacion de la salida no espera al SMTP
                // (con varios clientes eran varios envios seguidos mientras el guardia miraba el spinner). Sin cola
                // activa, como en el reenvio manual de linea de comandos, se manda directo como siempre.
                exito = await _smtp.EnviarAsync(aQuien, subject, body,
                    incluirCopiasOcultasAutomaticas: destinatariosOverride == null,
                    preferirCola: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo enviar la notificacion de salida de {Relno} para el cliente {Cliente}", relno, grupo.Key);
            }

            if (!exito)
                _logger.LogWarning("La notificacion de salida de {Relno} para el cliente {Cliente} no se envio", relno, grupo.Key);

            enviados.Add(new SalidaNotificacionEnviada(nombreCliente, grupo.Key, reales, aQuien, exito));
        }

        return enviados;
    }

    /// <summary>
    /// Vuelve a mandar la notificacion de SALIDA de una relacion que ya salio (caso "no les llego a los contactos").
    /// Con <paramref name="soloValidacion"/> la manda unicamente a desarrollo3, sin copias ocultas, y devuelve a quien le
    /// tocaria; sin ella va a los contactos reales de cada cliente. No cambia ningun dato de la relacion.
    /// </summary>
    public async Task<(bool Success, string Mensaje)> ReenviarNotificacionSalidaAsync(
        string relno, bool soloValidacion, bool incluirEnlaceFotos, CancellationToken ct = default)
    {
        string limpio = (relno ?? string.Empty).Trim();
        if (limpio.Length == 0) return (false, "La relación es obligatoria.");

        var (tabla, sufijo) = ResolverTabla(limpio);
        using IDbConnection conn = _db.CreateConnection();

        var registro = await conn.QueryFirstOrDefaultAsync<RelacionSalidaRow>(new CommandDefinition(
            $@"SELECT estatus_carga AS Estatus,
                      CASE WHEN YEAR(salida_caseta) >= 2000 THEN salida_caseta END AS SalidaCaseta,
                      IFNULL(usumodi, '') AS Usuario
               FROM {tabla}
               WHERE relno = @relno", new { relno = limpio }, cancellationToken: ct));

        if (registro == null) return (false, $"No existe la relación {limpio}.");
        if (!string.Equals(registro.Estatus?.Trim(), "SALIDA", StringComparison.OrdinalIgnoreCase) || registro.SalidaCaseta == null)
            return (false, $"La relación {limpio} no está en SALIDA (estatus '{registro.Estatus}'); no hay salida que notificar.");

        string observaciones = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT obs_salida FROM caseta WHERE relacion = @relno AND TRIM(IFNULL(obs_salida, '')) <> '' LIMIT 1",
            new { relno = limpio }, cancellationToken: ct)) ?? string.Empty;

        var enviados = await NotiSalidaCasetaAsync(
            conn, limpio, sufijo, registro.Usuario, registro.SalidaCaseta.Value.ToString("yyyy-MM-dd HH:mm:ss"),
            observaciones.Trim(), soloValidacion ? new[] { CorreoPruebasDesarrollo } : null, incluirEnlaceFotos,
            exigirEnlace: incluirEnlaceFotos);

        if (enviados.Count == 0)
            return (false, "No hay a quién notificar: la relación no tiene tráficos (o todos son PIPA).");

        string detalle = string.Join(" | ", enviados.Select(e =>
            $"{e.Cliente} ({e.ClienteId}): {(e.Exito ? "enviado" : "FALLÓ")} a {string.Join(", ", e.EnviadoA)}"
            + (soloValidacion ? $" · le tocaría a: {string.Join(", ", e.Reales)}" : string.Empty)));

        return (enviados.All(e => e.Exito), (soloValidacion ? "VALIDACIÓN (solo desarrollo3). " : string.Empty) + detalle);
    }

    private async Task<CasetaDetalleDbRow?> GetDetalleRawAsync(IDbConnection conn, string relno, string sufijo)
    {
        string tabla = sufijo == "C" ? "relacion_imex" : "relacion";
        return await conn.QueryFirstOrDefaultAsync<CasetaDetalleDbRow>(
        $@"SELECT r.*, r.estatus_carga AS EstatusCarga, t.cnombre
 FROM {tabla} r
 LEFT JOIN tranfers t ON r.cidtransfer=t.cidtransfer
 WHERE r.relno=@relno",
        new { relno });
    }

    public async Task<CasetaEntradaCatalogosResult> ObtenerCatalogosEntradaAsync()
    {
        var result = new CasetaEntradaCatalogosResult
        {
            Bodegas = BodegaCatalogo.Todas,
            Paises = ObtenerPaisesEntrada()
        };

        using IDbConnection conn = _db.CreateConnection();

        var lineas = (await conn.QueryAsync<CasetaEntradaCatalogItem>(@"
SELECT cidtranspor AS Id, cnombre AS Nombre
FROM transpor
WHERE Inactivo = 0
ORDER BY cnombre")).ToList();

        result.LineasCamion = lineas.Select(x => new CasetaEntradaCatalogItem { Id = x.Id, Nombre = x.Nombre }).ToList();
        result.LineasCaja = lineas.Select(x => new CasetaEntradaCatalogItem { Id = x.Id, Nombre = x.Nombre }).ToList();
        result.TiposCaja = (await conn.QueryAsync<CasetaEntradaCatalogItem>(@"
SELECT cidcamion AS Id, descripcionCaseta AS Nombre
FROM camiones
WHERE descripcionCaseta != ''
ORDER BY ordenDescripcion ASC")).ToList();
        result.Clientes = (await GetClientesEntradaAsync(conn, null, 0)).ToList();

        return result;
    }

    public async Task<List<CasetaEntradaCatalogItem>> BuscarClientesEntradaAsync(string? filtro, int limite = 50)
    {
        using IDbConnection conn = _db.CreateConnection();
        return (await GetClientesEntradaAsync(conn, filtro, limite)).ToList();
    }

    public async Task<List<CasetaEntradaCatalogItem>> BuscarTransportesEntradaAsync(string? filtro, int limite = 50)
    {
        using IDbConnection conn = _db.CreateConnection();
        string like = "%" + (filtro ?? "").Trim() + "%";
        int take = Math.Clamp(limite, 1, 200);

        return (await conn.QueryAsync<CasetaEntradaCatalogItem>(@"
SELECT cidtranspor AS Id, cnombre AS Nombre
FROM transpor
WHERE Inactivo = 0
  AND (@filtro = '' OR cidtranspor LIKE @like OR cnombre LIKE @like)
ORDER BY cnombre
LIMIT @take", new { filtro = (filtro ?? "").Trim(), like, take })).ToList();
    }

    public async Task<List<CasetaEntradaFrequentItem>> ObtenerFrecuentesEntradaAsync(
   string tipo,
   int limite = 8,
   int dias = 90)
    {
        using IDbConnection conn = _db.CreateConnection();
        int take = Math.Clamp(limite, 1, 20);
        DateTime fromDate = DateTime.Now.AddDays(-Math.Clamp(dias, 1, 365));

        return tipo.Trim().ToLowerInvariant() switch
        {
            "cliente" => (await conn.QueryAsync<CasetaEntradaFrequentItem>(@"
SELECT
    c.cclienteid AS Id,
    COALESCE(NULLIF(cl.cnombre, ''), c.cclienteid) AS Nombre,
    COUNT(*) AS Uso,
    MAX(c.farribo) AS UltimoUso
FROM caseta c
LEFT JOIN clientes cl ON c.cclienteid = cl.cidcte
WHERE c.farribo >= @fromDate
  AND NULLIF(TRIM(c.cclienteid), '') IS NOT NULL
GROUP BY c.cclienteid, cl.cnombre
ORDER BY Uso DESC, UltimoUso DESC
LIMIT @take", new { fromDate, take })).ToList(),

            "linea-camion" => await ObtenerFrecuentesTransporteAsync(conn, "idtransporu", "transporu", fromDate, take),
            "linea-caja" => await ObtenerFrecuentesTransporteAsync(conn, "idtranspore", "transpore", fromDate, take),
            _ => throw new ArgumentException("Tipo de frecuente no valido.")
        };
    }

    public async Task<CasetaEntradaPlateOcrResult> LeerPlacaEntradaAsync(
   CasetaEntradaPlateOcrRequest request,
   CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Base64Image))
        {
            return new CasetaEntradaPlateOcrResult
            {
                Success = false,
                Message = "La imagen es obligatoria."
            };
        }

        string apiKey = _config["GeminiApiKey"]?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new CasetaEntradaPlateOcrResult
            {
                Success = false,
                Message = "OCR no configurado en el servidor."
            };
        }

        var image = NormalizeBase64ImagePayload(request.Base64Image);
        string prompt = """
Eres un OCR de placas vehiculares de caseta.
Devuelve UNICAMENTE JSON valido, sin markdown:
{
  "success": true,
  "placa": "ABC123",
  "confidence": 0.0,
  "candidates": [
    { "value": "ABC123", "confidence": 0.0, "source": "server", "reason": "Placa visible al frente" }
  ],
  "message": "Placa detectada"
}
Reglas:
- candidates debe incluir TODAS las placas visibles razonables, ordenadas de mayor a menor confianza.
- "placa" y "confidence" representan la mejor opcion.
- Usa solo letras A-Z, numeros 0-9 y guion.
- Ignora textos que claramente no son placas (sticker, advertencias, dimensiones, marcas como UTILITY, modelos como 3000R).
- Si no hay placa clara, responde {"success": false, "placa": "", "confidence": 0, "candidates": [], "message": "No se encontro una placa clara."}
""";

        var requestBody = new
        {
            contents = new[]
        {
  new
  {
  parts = new object[]
  {
  new { text = prompt },
  new
  {
  inline_data = new
  {
  mime_type = image.MimeType,
  data = image.Base64
  }
  }
  }
  }
   },
            generationConfig = new
            {
                temperature = 0.1,
                response_mime_type = "application/json"
            }
        };

        string url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=" + WebUtility.UrlEncode(apiKey);
        HttpClient client = _httpClientFactory.CreateClient("GeminiClient");
        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestBody)
        };

        HttpResponseMessage response = await client.SendAsync(reqMsg, ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Gemini OCR placa devolvio {Status}: {Error}", response.StatusCode, error);
            return new CasetaEntradaPlateOcrResult
            {
                Success = false,
                Message = "No se pudo leer la placa en este momento."
            };
        }

        string textResponse = await ExtractGeminiResponseTextAsync(response, ct);

        return ParsePlateOcrResult(textResponse);
    }

    public async Task<CasetaEntradaBoxOcrResult> LeerCajaEntradaAsync(
   CasetaEntradaBoxOcrRequest request,
   CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Base64Image))
        {
            return new CasetaEntradaBoxOcrResult
            {
                Success = false,
                Message = "La imagen es obligatoria."
            };
        }

        string apiKey = _config["GeminiApiKey"]?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new CasetaEntradaBoxOcrResult
            {
                Success = false,
                Message = "OCR no configurado en el servidor."
            };
        }

        var image = NormalizeBase64ImagePayload(request.Base64Image);
        string prompt = """
Eres un OCR de numeros de caja / remolque / contenedor en caseta.
Devuelve UNICAMENTE JSON valido, sin markdown:
{
  "success": true,
  "numCaja": "52094",
  "confidence": 0.0,
  "candidates": [
    { "value": "52094", "confidence": 0.0, "source": "server", "reason": "5 digitos vertical al costado" }
  ],
  "message": "Caja detectada"
}
Reglas:
- candidates debe incluir TODOS los codigos de caja visibles razonables, ordenados de mayor a menor confianza.
- "numCaja" y "confidence" representan la mejor opcion.
- Detecta tanto numeros horizontales (6 digitos) como verticales (5 digitos) y alfanumericos (ej. ASR1072).
- Considera que el texto puede estar rotado 90 o 270 grados.
- Ignora dimensiones como "53'", marcas como UTILITY, modelos como 3000R o C-600, placas chicas, stickers y advertencias (STOP, WARNING, CAUTION).
- Si no hay caja clara, responde {"success": false, "numCaja": "", "confidence": 0, "candidates": [], "message": "No se encontro una caja clara."}
""";

        var requestBody = new
        {
            contents = new[]
        {
  new
  {
  parts = new object[]
  {
  new { text = prompt },
  new
  {
  inline_data = new
  {
  mime_type = image.MimeType,
  data = image.Base64
  }
  }
  }
  }
   },
            generationConfig = new
            {
                temperature = 0.1,
                response_mime_type = "application/json"
            }
        };

        string url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=" + WebUtility.UrlEncode(apiKey);
        HttpClient client = _httpClientFactory.CreateClient("GeminiClient");
        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestBody)
        };

        HttpResponseMessage response = await client.SendAsync(reqMsg, ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Gemini OCR caja devolvio {Status}: {Error}", response.StatusCode, error);
            return new CasetaEntradaBoxOcrResult
            {
                Success = false,
                Message = "No se pudo leer la caja en este momento."
            };
        }

        string textResponse = await ExtractGeminiResponseTextAsync(response, ct);

        return ParseBoxOcrResult(textResponse);
    }

    public async Task<List<CasetaEntradaCatalogItem>> ObtenerTiposCajaEntradaAsync()
    {
        using IDbConnection conn = _db.CreateConnection();
        return (await conn.QueryAsync<CasetaEntradaCatalogItem>(@"
SELECT cidcamion AS Id, descripcionCaseta AS Nombre
FROM camiones
WHERE descripcionCaseta != ''
ORDER BY ordenDescripcion ASC")).ToList();
    }

    public async Task<CasetaEntradaObtenerResult> ObtenerEntradaCasetaAsync(string casetaId, bool incluirFotosBase64 = true)
    {
        if (string.IsNullOrWhiteSpace(casetaId))
        {
            return new CasetaEntradaObtenerResult { Success = false, Message = "CasetaId es obligatorio." };
        }

        using IDbConnection conn = _db.CreateConnection();

        var datos = await conn.QueryFirstOrDefaultAsync<CasetaEntradaDetalle>(@"
SELECT
    idcaseta AS IdCaseta,
    casetaid AS CasetaId,
    idbodega AS IdBodega,
    bodega AS Bodega,
    tipoop AS TipoOp,
    tipomov AS TipoMov,
    tiporegistro AS TipoRegistro,
    iduni AS IdUnidad,
    tipouni AS Tipouni,
    nunidad AS NumUnidad,
    idtransporu AS IdTransporteTractor,
    transporu AS Transpor,
    nequipo AS Nequipo,
    idtranspore AS IdTransporteCaja,
    transpore AS Transporte,
    idpais AS IdPais,
    pais AS Pais,
    placas AS Placas,
    sellos AS Sellos,
    operador AS Operador,
    cclienteid AS IdCliente,
    COALESCE((SELECT CONCAT(cidcte, ' - ', cnombre) FROM clientes WHERE cclienteid = cidcte LIMIT 1), '') AS Cliente,
    COALESCE((SELECT cnombre FROM clientes WHERE cclienteid = cidcte LIMIT 1), '') AS NombreCliente,
    obs_entrada AS Observaciones,
    temperatura AS Temperatura,
    cpedimento AS Pedimento,
    cant_guia AS Guias,
    combustible AS Combustible,
    planta AS Planta
FROM caseta
WHERE casetaId = @casetaId
LIMIT 1", new { casetaId });

        if (datos == null)
        {
            return new CasetaEntradaObtenerResult { Success = false, Message = "Registro de caseta no encontrado." };
        }

        datos.LineaCamion = BuildCatalogText(datos.IdTransporteTractor, datos.Transpor);
        datos.LineaCaja = BuildCatalogText(datos.IdTransporteCaja, datos.Transporte);
        datos.NombreCliente = string.IsNullOrWhiteSpace(datos.NombreCliente) ? datos.Cliente : datos.NombreCliente;
        datos.Actualizado = true;

        var result = new CasetaEntradaObtenerResult
        {
            Success = true,
            Datos = datos
        };

        var fotos = await LeerFotosEntradaCasetaAsync(conn, casetaId, incluirFotosBase64);
        result.Fotos = fotos.Info;
        result.FotosBase64 = fotos.Base64;

        return result;
    }

    public async Task<CasetaEntradaGuardarResult> GuardarEntradaCasetaAsync(
   CasetaEntradaGuardarRequest datosF,
   string usuarioActual,
   CancellationToken ct = default)
    {
        var errores = ValidarEntradaCaseta(datosF);
        if (errores.Count > 0)
        {
            return new CasetaEntradaGuardarResult
            {
                Success = false,
                Message = "Faltan o no son validos: " + string.Join(", ", errores)
            };
        }

        CasetaEntradaRegistro datos;
        try
        {
            datos = ConstruirEntradaCaseta(datosF, usuarioActual);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error construyendo entrada de caseta");
            return new CasetaEntradaGuardarResult { Success = false, Message = "Datos de caseta invalidos." };
        }

        datos.NombreUsuario = await ObtenerNombreUsuarioSeguroAsync(datos.Usucrea, ct);

        var listaFotos = PrepararFotosEntrada(datosF.Fotos);
        if (listaFotos.Count > 15)
        {
            return new CasetaEntradaGuardarResult { Success = false, Message = "Solo se permiten hasta 15 fotos." };
        }

        if (datosF.IndicesFotosEliminar.Any(indice => indice is < 1 or > 15))
        {
            return new CasetaEntradaGuardarResult { Success = false, Message = "El indice de foto a eliminar no es valido." };
        }
        var indicesFotosEliminar = datosF.IndicesFotosEliminar.Distinct().ToList();

        datos.Planta = FirstNonEmpty(await ObtenerPlantaUsuarioSeguroAsync(datos.Usucrea, ct), datos.Planta);
        if (!datos.EsPreRegistro)
        {
            datos.EsPreRegistro = await TienePreregistroVigenteAsync(datosF, ct);
            if (datos.EsPreRegistro)
            {
                // Es la razon mas comun de «el correo no llego»: queda escrita para poder contestar por que.
                _logger.LogWarning(
                    "Entrada de caseta (unidad={Unidad}, placas={Placas}, tipo={Tipo}): coincide con un pre-registro VIGENTE, " +
                    "asi que su correo operativo se desvia a {Destino}.",
                    datosF.NoCamion, datosF.Placas, datosF.TipoRegistro, CorreoPruebasDesarrollo);
            }
        }

        using IDbConnection conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        var idsGenerados = new List<string>();
        var fotosStaged = new List<CasetaPhotoStage>();
        var fotosParaEliminar = new List<string>();
        bool transaccionConfirmada = false;

        try
        {
            bool existe = await ExisteCasetaIdEnBDAsync(conn, tx, datos.CasetaId);
            var resultado = existe
           ? await ActualizarEntradaCasetaYFotosAsync(conn, tx, datos, listaFotos, fotosStaged, fotosParaEliminar, indicesFotosEliminar, ct)
           : await GuardarEntradaCasetaYFotosAsync(conn, tx, datos, listaFotos, fotosStaged, ct);

            if (!resultado.Success)
            {
                tx.Rollback();
                _photoStorage.CleanupStagedPhotos(fotosStaged);
                return resultado;
            }

            idsGenerados.AddRange(resultado.CasetaIds);

            if (datos.RequiereImpo)
            {
                datos.RequiereImpo = false;
                datos.TipoOp = "IMPORTACION";

                var resultadoImpo = await GuardarEntradaCasetaYFotosAsync(conn, tx, datos, listaFotos, fotosStaged, ct);
                if (!resultadoImpo.Success)
                {
                    tx.Rollback();
                    _photoStorage.CleanupStagedPhotos(fotosStaged);
                    return resultadoImpo;
                }

                resultado = resultadoImpo;
                idsGenerados.AddRange(resultadoImpo.CasetaIds);
            }

            tx.Commit();
            transaccionConfirmada = true;

            bool fotosEnCola = true;
            try
            {
                _photoStorage.ActivateStagedPhotos(fotosStaged);
                foreach (string foto in fotosParaEliminar.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    _photoStorage.QueueDelete(foto);
                }
            }
            catch (Exception ex)
            {
                fotosEnCola = false;
                _logger.LogError(ex, "Registro de entrada caseta guardado, pero no se pudo activar la cola local de fotos.");
            }

            // Cuando la app movil usa el flujo diferido, el correo se dispara
            // despues de que el worker confirme las fotos. Los clientes legacy
            // conservan el comportamiento anterior.
            if (!datosF.DiferirCorreo)
            {
                await EncolarCorreoEntradaCasetaAsync(datos, listaFotos);
            }
            else
            {
                // El aviso espera a las fotos, pero ya no depende solo de la tableta: sale cuando lleguen las anunciadas,
                // cuando la tableta mande el ultimo lote, o por respaldo si la sincronizacion se queda a medias.
                _diferido.Registrar(datos.CasetaId, datos.Actualizado, usuarioActual, datosF.IndicesFotosEsperadas);
            }

            await NotificarMovimientoCasetaAsync("ENTRADA", datos.CasetaId, usuarioActual, ct);

            return new CasetaEntradaGuardarResult
            {
                Success = true,
                Message = fotosEnCola
          ? "Registro guardado exitosamente. Fotos y correo en proceso."
          : "Registro guardado, pero las fotos no se pudieron dejar en cola. Revisar logs del API.",
                CasetaId = datos.CasetaId,
                CasetaIds = idsGenerados.Distinct().ToList()
            };
        }
        catch (Exception ex)
        {
            if (!transaccionConfirmada)
            {
                try { tx.Rollback(); } catch { }
                _photoStorage.CleanupStagedPhotos(fotosStaged);
            }
            _logger.LogError(ex, "Error al guardar entrada de caseta");
            return new CasetaEntradaGuardarResult { Success = false, Message = "Error inesperado al guardar." };
        }
    }

    private async Task<string> ObtenerPlantaUsuarioSeguroAsync(string usuario, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(usuario))
            return string.Empty;

        try
        {
            using IDbConnection conn = _db.CreateReportesConnection();
            string? planta = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT IFNULL(planta, '') FROM usuarios WHERE claveusuario = @usuario LIMIT 1",
            new { usuario = usuario.Trim() },
            cancellationToken: ct));

            return planta?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo obtener planta del usuario {Usuario}; se usara la planta enviada por el cliente.", usuario);
            return string.Empty;
        }
    }

    private async Task<string> ObtenerNombreUsuarioSeguroAsync(string usuario, CancellationToken ct)
    {
        string usuarioNormalizado = (usuario ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(usuarioNormalizado))
            return "sistema";

        try
        {
            using IDbConnection conn = _db.CreateConnection();
            string? nombre = await conn.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(
                @"SELECT NULLIF(TRIM(CONCAT_WS(' ',
                    NULLIF(TRIM(nombre), ''),
                    NULLIF(TRIM(apaterno), '')
                )), '')
                FROM usuarios
                WHERE claveusuario = @usuario
                LIMIT 1",
                new { usuario = usuarioNormalizado },
                cancellationToken: ct));

            return FirstNonEmpty(nombre, usuarioNormalizado);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "No se pudo obtener el nombre del usuario autenticado {Usuario}; se usara su clave.",
                usuarioNormalizado);
            return usuarioNormalizado;
        }
    }

    public async Task<CasetaEntradaFotosResult> SubirFotosEntradaCasetaAsync(
        string casetaId,
        CasetaEntradaFotosRequest request,
        string usuarioActual,
        CancellationToken ct = default)
    {
        // Retrofit codifica el folio en la ruta. Los folios de caseta contienen
        // una diagonal (por ejemplo E-017848/26), por lo que aqui puede llegar
        // como E-017848%2F26 dependiendo de como el servidor haya conservado
        // el valor de la ruta. La consulta debe usar siempre el folio real.
        string cleanCasetaId = NormalizeCasetaId(casetaId);
        List<CasetaEntradaFotoUpload> uploads = request?.Fotos ?? [];

        if (string.IsNullOrWhiteSpace(cleanCasetaId))
            return new CasetaEntradaFotosResult { Success = false, Message = "CasetaId es obligatorio." };
        if (uploads.Count == 0)
            return new CasetaEntradaFotosResult { Success = false, Message = "No se recibieron fotos." };
        if (uploads.Count > 4)
            return new CasetaEntradaFotosResult { Success = false, Message = "Solo se permiten 4 fotos por lote." };
        if (uploads.Any(x => x.Indice is < 1 or > 15))
            return new CasetaEntradaFotosResult { Success = false, Message = "El indice de foto debe estar entre 1 y 15." };
        if (uploads.Select(x => x.Indice).Distinct().Count() != uploads.Count)
            return new CasetaEntradaFotosResult { Success = false, Message = "No se permiten indices de foto repetidos." };

        var decoded = new List<(CasetaEntradaFotoUpload Upload, byte[] Bytes)>();
        foreach (CasetaEntradaFotoUpload upload in uploads)
        {
            if (string.IsNullOrWhiteSpace(upload.Base64Image))
                return new CasetaEntradaFotosResult { Success = false, Message = "Una foto no contiene imagen." };

            byte[]? bytes = TryDecodeBase64Image(upload.Base64Image);
            if (bytes == null || bytes.Length == 0)
                return new CasetaEntradaFotosResult { Success = false, Message = $"La foto {upload.Indice} no es valida." };

            decoded.Add((upload, bytes));
        }

        using IDbConnection conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        var staged = new List<CasetaPhotoStage>();

        try
        {
            CasetaEntradaRegistro? caseta = await CargarRegistroEntradaAsync(conn, cleanCasetaId, usuarioActual, tx, ct);

            if (caseta == null)
            {
                _logger.LogWarning(
                    "No existe el registro de caseta al subir fotos. Folio recibido={CasetaIdRaw}, folio normalizado={CasetaId}, indices={Indices}",
                    casetaId,
                    cleanCasetaId,
                    string.Join(",", uploads.Select(x => x.Indice)));
                return new CasetaEntradaFotosResult { Success = false, Message = "El registro de caseta no existe." };
            }

            var existingIndexes = (await conn.QueryAsync<int>(new CommandDefinition(@"
SELECT nlineno FROM fotos
WHERE creferencia = @casetaId AND ntipodocto = 106",
                new { casetaId = cleanCasetaId }, tx, cancellationToken: ct))).ToHashSet();

            foreach ((CasetaEntradaFotoUpload upload, byte[] bytes) in decoded)
            {
                if (existingIndexes.Contains(upload.Indice))
                {
                    await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE fotos
SET cextension = 'jpg', usucrea = @usuario, fcrea = @ahora
WHERE creferencia = @casetaId AND ntipodocto = 106 AND nlineno = @indice",
                        new
                        {
                            casetaId = cleanCasetaId,
                            indice = upload.Indice,
                            usuario = FirstNonEmpty(usuarioActual, caseta.Usucrea),
                            ahora = DateTime.Now
                        }, tx, cancellationToken: ct));
                }
                else
                {
                    await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO fotos (nlineno, creferencia, ntipodocto, cextension, usucrea, fcrea)
VALUES (@indice, @casetaId, 106, 'jpg', @usuario, @ahora)",
                        new
                        {
                            casetaId = cleanCasetaId,
                            indice = upload.Indice,
                            usuario = FirstNonEmpty(usuarioActual, caseta.Usucrea),
                            ahora = DateTime.Now
                        }, tx, cancellationToken: ct));
                    existingIndexes.Add(upload.Indice);
                }

                string fileName = _photoStorage.GetFileName(cleanCasetaId, upload.Indice);
                staged.Add(await _photoStorage.StageAsync(bytes, fileName, ct));
            }

            tx.Commit();
            _photoStorage.ActivateStagedPhotos(staged);

            // La sincronizacion sigue avanzando: el respaldo espera un poco mas antes de avisar sin todas las fotos.
            _diferido.RegistrarActividad(cleanCasetaId);

            PendienteCorreoEntrada? pendiente = _diferido.Leer(cleanCasetaId);
            bool completas = pendiente != null && CorreoEntradaDiferido.Completas(pendiente.IndicesEsperados, existingIndexes);
            MotivoCorreoDiferido motivo = CorreoEntradaDiferido.Decidir(
                hayPendiente: pendiente != null,
                finalizar: request.Finalizar,
                fotosCompletas: completas,
                yaAvisado: _diferido.YaAvisado(cleanCasetaId));

            bool correoEnProceso = false;
            if (motivo != MotivoCorreoDiferido.Ninguno)
            {
                // Solo uno se queda con el derecho de avisar (este lote o el respaldo); el otro no manda nada.
                PendienteCorreoEntrada? tomado = pendiente != null ? _diferido.Reclamar(cleanCasetaId) : null;
                if (pendiente == null || tomado != null)
                {
                    // La verdad de si es una actualizacion la dejo anotada el guardado, no la bandera de este lote.
                    caseta.Actualizado = tomado?.Actualizado ?? request.Actualizado;
                    caseta.CasetaId = cleanCasetaId;
                    var photoInfo = await LeerFotosEntradaCasetaAsync(conn, cleanCasetaId, incluirBase64: false);
                    var emailPhotos = photoInfo.Info
                        .Select(photo => new CasetaEntradaFoto
                        {
                            Indice = photo.Indice,
                            NombreArchivo = photo.NombreArchivo,
                            TipoEtiqueta = "normal"
                        })
                        .ToList();

                    correoEnProceso = await EncolarCorreoEntradaCasetaAsync(caseta, emailPhotos);
                    PendienteCorreoEntrada anotado = tomado ?? new PendienteCorreoEntrada
                    {
                        CasetaId = cleanCasetaId,
                        Actualizado = caseta.Actualizado,
                        Registrado = DateTime.Now
                    };
                    if (correoEnProceso) _diferido.MarcarAvisado(anotado, motivo);
                    else if (tomado != null) _diferido.Devolver(tomado, "el correo no se pudo armar ni encolar al llegar las fotos");
                }
            }

            return new CasetaEntradaFotosResult
            {
                Success = true,
                Message = correoEnProceso
                    ? "Fotos guardadas y correo en proceso."
                    : "Fotos guardadas correctamente.",
                FotosGuardadas = decoded.Count
            };
        }
        catch (OperationCanceledException)
        {
            _photoStorage.CleanupStagedPhotos(staged);
            throw;
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            _photoStorage.CleanupStagedPhotos(staged);
            _logger.LogError(ex, "Error subiendo fotos de entrada caseta {CasetaId}", cleanCasetaId);
            throw;
        }
    }

    // Puede venir doblemente codificado si el valor pasó por dos capas de URL antes de llegar al controlador.
    private static string NormalizeCasetaId(string? casetaId) => CasetaFolio.Normalizar(casetaId);

    /// <summary>
    /// Deja el correo de la entrada guardado en la cola durable ANTES de responder a la tableta: desde ese momento el
    /// envío ya no depende de que el API siga vivo ni de que el SMTP conteste a la primera. Nunca lanza: la entrada ya
    /// está guardada y un fallo del correo no debe deshacerla.
    /// </summary>
    private async Task<bool> EncolarCorreoEntradaCasetaAsync(CasetaEntradaRegistro datos, List<CasetaEntradaFoto> fotos)
    {
        try
        {
            MimeMessage? mensaje = await ConstruirCorreoEntradaCasetaAsync(datos, fotos, CancellationToken.None);
            if (mensaje == null)
            {
                _logger.LogError("Correo de entrada de caseta {CasetaId} NO se armó (sin destinatarios o error); la entrada sí quedó guardada.", datos.CasetaId);
                return false;
            }

            if (_cola.Activa && await _cola.EncolarAsync(mensaje, "entrada-caseta", datos.CasetaId))
            {
                return true;
            }

            // Sin cola (o sin poder escribir en disco) se manda directo en segundo plano, como antes.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _transporte.EnviarAsync(mensaje, CancellationToken.None);
                    _logger.LogInformation("Correo de entrada de caseta {CasetaId} enviado directo.", datos.CasetaId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Registro de entrada caseta {CasetaId} guardado, pero el correo no se pudo enviar ni encolar.", datos.CasetaId);
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error no controlado preparando el correo de entrada caseta {CasetaId}.", datos.CasetaId);
            return false;
        }
    }

    /// <summary>
    /// Lo llama el respaldo cuando una entrada con fotos nuevas no terminó de sincronizar a tiempo: arma el aviso con las
    /// fotos que SÍ llegaron (el enlace las muestra todas, también las que lleguen después) y lo deja en la cola de correo.
    /// </summary>
    public async Task<bool> EnviarCorreoEntradaDiferidoAsync(PendienteCorreoEntrada pendiente, CancellationToken ct)
    {
        using IDbConnection conn = _db.CreateConnection();
        CasetaEntradaRegistro? registro = await CargarRegistroEntradaAsync(conn, pendiente.CasetaId, pendiente.Usuario, null, ct);
        if (registro == null)
        {
            // Se borró el folio antes del aviso: no hay nada que mandar y reintentar no lo va a cambiar.
            _logger.LogWarning("Respaldo de correo: el folio {CasetaId} ya no existe; se descarta el aviso.", pendiente.CasetaId);
            return true;
        }

        var photoInfo = await LeerFotosEntradaCasetaAsync(conn, pendiente.CasetaId, incluirBase64: false);
        var fotos = photoInfo.Info
            .Select(photo => new CasetaEntradaFoto
            {
                Indice = photo.Indice,
                NombreArchivo = photo.NombreArchivo,
                TipoEtiqueta = "normal"
            })
            .ToList();

        registro.CasetaId = pendiente.CasetaId;
        registro.Actualizado = pendiente.Actualizado;
        _logger.LogWarning(
            "Respaldo de correo: la tableta no termino de subir las fotos de {CasetaId} ({Presentes} de {Esperadas} anunciadas); el aviso sale con las que hay.",
            pendiente.CasetaId, fotos.Count, pendiente.IndicesEsperados.Count);
        return await EncolarCorreoEntradaCasetaAsync(registro, fotos);
    }

    /// <summary>El registro de caseta con lo que necesita el correo de entrada. Un solo lugar para las tres vias que lo mandan.</summary>
    private static Task<CasetaEntradaRegistro?> CargarRegistroEntradaAsync(
        IDbConnection conn, string casetaId, string usuario, IDbTransaction? tx, CancellationToken ct) =>
        conn.QueryFirstOrDefaultAsync<CasetaEntradaRegistro>(new CommandDefinition(@"
SELECT
    idcaseta AS IdCaseta,
    casetaid AS CasetaId,
    idbodega AS IdBodega,
    bodega AS Bodega,
    tipoop AS TipoOp,
    tipomov AS TipoMov,
    tiporegistro AS TipoRegistro,
    iduni AS IdUnidad,
    tipouni AS Tipouni,
    nunidad AS NumUnidad,
    idtransporu AS IdTransporteTractor,
    transporu AS Transpor,
    nequipo AS Nequipo,
    idtranspore AS IdTransporteCaja,
    transpore AS Transporte,
    idpais AS IdPais,
    pais AS Pais,
    placas AS Placas,
    sellos AS Sellos,
    operador AS Operador,
    cclienteid AS IdCliente,
    COALESCE((SELECT cnombre FROM clientes WHERE cclienteid = cidcte LIMIT 1), '') AS NombreCliente,
    obs_entrada AS Observaciones,
    temperatura AS Temperatura,
    cpedimento AS Pedimento,
    cant_guia AS Guias,
    combustible AS Combustible,
    planta AS Planta,
    COALESCE(caseta.usumodi, caseta.usucrea, @usuario) AS Usucrea, -- con prefijo: `usuarios` tambien tiene usumodi/usucrea y sin el la subconsulta de abajo compara contra esas
    COALESCE(
        (SELECT CONCAT(TRIM(u.nombre), ' ', TRIM(u.apaterno))
         FROM usuarios u
         WHERE u.claveusuario = COALESCE(caseta.usumodi, caseta.usucrea, @usuario)
         LIMIT 1),
        COALESCE(caseta.usumodi, caseta.usucrea, @usuario)
    ) AS NombreUsuario,
    fcrea AS Fcrea,
    COALESCE(fmodi, fcrea) AS Fmodi
FROM caseta
WHERE casetaid = @casetaId
LIMIT 1", new { casetaId, usuario }, tx, cancellationToken: ct))!;

    public async Task<List<CasetaEntradaBusquedaItem>> BuscarEntradasCasetaAsync(
 string termino,
 string tipo = "general",
 int offset = 0,
 int limit = 20,
 string? tipoMov = null,
 string? cliente = null,
 string? linea = null)
    {
        using IDbConnection conn = _db.CreateConnection();
        int take = Math.Clamp(limit, 1, 100);
        int skip = Math.Max(offset, 0);
        string q = (termino ?? string.Empty).Trim();

        // Filtros de chip del hub de Caseta (cliente / linea / tipo de movimiento).
        // Van SIEMPRE en el SQL con el patron "vacio = sin filtro" que ya usa
        // SalidaCasetaService, en vez de armar el WHERE por concatenacion: asi
        // los parametros existen siempre y no hay ramas de SQL que probar.
        string fTipoMov = (tipoMov ?? string.Empty).Trim();
        string fCliente = (cliente ?? string.Empty).Trim();
        string fLinea = (linea ?? string.Empty).Trim();

        string normalized = q.ToUpperInvariant();
        string tipoNormalizado = (tipo ?? "general").Trim().ToLowerInvariant();

        // tipo="tractocamion" es un FILTRO, no un modo de matching como box/plate:
        // restringe el resultado a registros sin caja (tiporegistro='TRACTOCAMION'),
        // sin cambiar contra qué columnas se busca el termino.
        string tipoRegistroWhere = tipoNormalizado == "tractocamion" ? "AND caseta.tiporegistro = 'TRACTOCAMION'" : "";

        const string filtrosWhere = @"
    AND (@fTipoMov = '' OR caseta.tipomov = @fTipoMov)
    AND (@fCliente = '' OR caseta.cclienteid = @fCliente OR clientes.cnombre = @fCliente)
    AND (@fLinea = '' OR caseta.transporu = @fLinea OR caseta.transpore = @fLinea)";

        if (string.IsNullOrEmpty(q))
        {
            string recentSql = $@"
SELECT
    caseta.casetaid AS Casetaid,
    caseta.nequipo AS Nequipo,
    caseta.placas AS Placas,
    caseta.nunidad AS Nunidad,
    caseta.transporu AS Transporu,
    caseta.transpore AS Transpore,
    clientes.cnombre AS Cliente,
    caseta.cclienteid AS IdCliente,
    caseta.tipoop AS TipoOp,
    caseta.tipomov AS TipoMov,
    caseta.tiporegistro AS TipoRegistro,
    NULLIF(CAST(caseta.farribo AS CHAR), '0000-00-00 00:00:00') AS Farribo,
    COUNT(fotos.creferencia) AS Fotos,
    'reciente' AS MatchField,
    0 AS MatchScore,
    'Ultimos movimientos' AS MatchReason
FROM caseta
LEFT JOIN clientes ON caseta.cclienteid = clientes.cidcte
LEFT JOIN fotos ON fotos.creferencia = caseta.casetaid AND fotos.ntipodocto = 106
WHERE 1=1 {tipoRegistroWhere}{filtrosWhere}
GROUP BY
    caseta.casetaid,
    caseta.nequipo,
    caseta.placas,
    caseta.nunidad,
    caseta.transporu,
    caseta.transpore,
    clientes.cidcte,
    clientes.cnombre,
    caseta.cclienteid,
    caseta.tipoop,
    caseta.tipomov,
    caseta.tiporegistro,
    caseta.farribo
ORDER BY COALESCE(NULLIF(CAST(caseta.farribo AS CHAR), '0000-00-00 00:00:00'), NULLIF(CAST(MAX(caseta.fcrea) AS CHAR), '0000-00-00 00:00:00'), NULLIF(CAST(MAX(caseta.fmodi) AS CHAR), '0000-00-00 00:00:00')) DESC, MAX(caseta.idcaseta) DESC
LIMIT @take OFFSET @skip";

            return (await conn.QueryAsync<CasetaEntradaBusquedaItem>(recentSql, new { take, skip, fTipoMov, fCliente, fLinea })).ToList();
        }

        string where = tipoNormalizado switch
        {
            "box" => "(caseta.nequipo = @exact OR caseta.nequipo LIKE CONCAT('%', @q, '%'))",
            "plate" => "(caseta.placas = @exact OR caseta.placas LIKE CONCAT('%', @q, '%'))",
            _ => BuildGeneralWhere()
        };

        if (tipoNormalizado == "tractocamion")
        {
            where = $"({where}) {tipoRegistroWhere}";
        }

        where = $"({where}){filtrosWhere}";

        string scoreExpr = tipoNormalizado switch
        {
            "box" => BuildBoxScoreExpr(),
            "plate" => BuildPlateScoreExpr(),
            _ => BuildGeneralScoreExpr()
        };

        string fieldExpr = tipoNormalizado switch
        {
            "box" => @"
CASE
 WHEN caseta.nequipo = @exact THEN 'caja'
 WHEN caseta.nequipo LIKE CONCAT(@q, '%') THEN 'caja'
 WHEN caseta.nequipo LIKE CONCAT('%', @q, '%') THEN 'caja'
 ELSE 'general'
END",
            "plate" => @"
CASE
 WHEN caseta.placas = @exact THEN 'placa'
 WHEN caseta.placas LIKE CONCAT(@q, '%') THEN 'placa'
 WHEN caseta.placas LIKE CONCAT('%', @q, '%') THEN 'placa'
 ELSE 'general'
END",
            _ => @"
CASE
 WHEN caseta.casetaid = @exact THEN 'casetaid'
 WHEN caseta.nequipo = @exact THEN 'caja'
 WHEN caseta.placas = @exact THEN 'placa'
 WHEN caseta.nunidad = @exact THEN 'unidad'
 WHEN caseta.transporu = @exact THEN 'transporte-camion'
 WHEN caseta.transpore = @exact THEN 'transporte-caja'
 WHEN clientes.cidcte = @exact THEN 'cliente'
 ELSE 'general'
END"
        };

        string reasonExpr = tipoNormalizado switch
        {
            "box" => @"'Coincidencia por numero de caja'",
            "plate" => @"'Coincidencia por placas'",
            _ => @"
CASE
 WHEN caseta.casetaid = @exact THEN 'CasetaId exacto'
 WHEN caseta.nequipo = @exact THEN 'Numero de caja exacto'
 WHEN caseta.placas = @exact THEN 'Placas exactas'
 WHEN caseta.nunidad = @exact THEN 'Numero de unidad exacto'
 WHEN caseta.transporu = @exact THEN 'Linea de camion exacta'
 WHEN caseta.transpore = @exact THEN 'Linea de caja exacta'
 WHEN clientes.cidcte = @exact THEN 'Cliente exacto'
 WHEN caseta.nequipo LIKE CONCAT(@q, '%') THEN 'Numero de caja inicia con'
 WHEN caseta.placas LIKE CONCAT(@q, '%') THEN 'Placas inician con'
 WHEN caseta.nunidad LIKE CONCAT(@q, '%') THEN 'Numero de unidad inicia con'
 ELSE 'Coincidencia parcial'
END"
        };

        string sql = $@"
SELECT
    caseta.casetaid AS Casetaid,
    caseta.nequipo AS Nequipo,
    caseta.placas AS Placas,
    caseta.nunidad AS Nunidad,
    caseta.transporu AS Transporu,
    caseta.transpore AS Transpore,
    clientes.cnombre AS Cliente,
    caseta.cclienteid AS IdCliente,
    caseta.tipoop AS TipoOp,
    caseta.tipomov AS TipoMov,
    caseta.tiporegistro AS TipoRegistro,
    NULLIF(CAST(caseta.farribo AS CHAR), '0000-00-00 00:00:00') AS Farribo,
    COUNT(fotos.creferencia) AS Fotos,
    {fieldExpr} AS MatchField,
    {scoreExpr} AS MatchScore,
    {reasonExpr} AS MatchReason
FROM caseta
LEFT JOIN clientes ON caseta.cclienteid = clientes.cidcte
LEFT JOIN fotos ON fotos.creferencia = caseta.casetaid AND fotos.ntipodocto = 106
WHERE {where}
GROUP BY
    caseta.casetaid,
    caseta.nequipo,
    caseta.placas,
    caseta.nunidad,
    caseta.transporu,
    caseta.transpore,
    clientes.cidcte,
    clientes.cnombre,
    caseta.cclienteid,
    caseta.tipoop,
    caseta.tipomov,
    caseta.tiporegistro,
    caseta.farribo
ORDER BY MatchScore DESC, caseta.casetaid DESC
LIMIT @take OFFSET @skip";

        var rows = (await conn.QueryAsync<CasetaEntradaBusquedaItem>(sql, new { q, exact = normalized, take, skip, fTipoMov, fCliente, fLinea })).ToList();
        return rows;
    }

    private static string BuildGeneralWhere() => @"
    caseta.casetaid LIKE CONCAT('%', @q, '%')
    OR caseta.nequipo LIKE CONCAT('%', @q, '%')
    OR caseta.placas LIKE CONCAT('%', @q, '%')
    OR caseta.nunidad LIKE CONCAT('%', @q, '%')
    OR caseta.transporu LIKE CONCAT('%', @q, '%')
    OR caseta.transpore LIKE CONCAT('%', @q, '%')
    OR clientes.cidcte = @exact
    OR clientes.cnombre LIKE CONCAT('%', @q, '%')";

    private static string BuildGeneralScoreExpr() => @"
CASE
 WHEN caseta.casetaid = @exact THEN 100
 WHEN caseta.nequipo = @exact THEN 95
 WHEN caseta.placas = @exact THEN 95
 WHEN caseta.nunidad = @exact THEN 90
 WHEN caseta.transporu = @exact THEN 85
 WHEN caseta.transpore = @exact THEN 85
 WHEN clientes.cidcte = @exact THEN 80
 WHEN caseta.nequipo LIKE CONCAT(@q, '%') THEN 70
 WHEN caseta.placas LIKE CONCAT(@q, '%') THEN 70
 WHEN caseta.nunidad LIKE CONCAT(@q, '%') THEN 65
 WHEN caseta.transporu LIKE CONCAT(@q, '%') THEN 60
 WHEN caseta.transpore LIKE CONCAT(@q, '%') THEN 60
 WHEN clientes.cnombre LIKE CONCAT(@q, '%') THEN 50
 ELSE 30
END";

    private static string BuildBoxScoreExpr() => @"
CASE
 WHEN caseta.nequipo = @exact THEN 100
 WHEN caseta.nequipo LIKE CONCAT(@q, '%') THEN 80
 WHEN caseta.nequipo LIKE CONCAT('%', @q, '%') THEN 60
 ELSE 30
END";

    private static string BuildPlateScoreExpr() => @"
CASE
 WHEN caseta.placas = @exact THEN 100
 WHEN caseta.placas LIKE CONCAT(@q, '%') THEN 80
 WHEN caseta.placas LIKE CONCAT('%', @q, '%') THEN 60
 ELSE 30
END";

    /// <summary>
    /// Conteos de entradas y salidas del dia. Con <paramref name="fecha"/> se
    /// consulta ese dia exacto; sin ella se mantiene el comportamiento de
    /// siempre (hoy, y si hoy no hay nada, el ultimo dia con movimientos).
    /// </summary>
    public async Task<object> ObtenerConteosCasetaHoyAsync(DateTime? fecha = null)
    {
        using IDbConnection conn = _db.CreateConnection();
        return await conn.QueryFirstAsync<object>(@"
SELECT
    COALESCE(SUM(CASE WHEN farribo >= CONCAT(ultima_fecha, ' 00:00:00')
                       AND farribo  < CONCAT(ultima_fecha, ' 23:59:59') THEN 1 ELSE 0 END), 0) AS entradas,
    COALESCE(SUM(CASE WHEN fsalida >= CONCAT(ultima_fecha, ' 00:00:00')
                       AND fsalida  < CONCAT(ultima_fecha, ' 23:59:59') THEN 1 ELSE 0 END), 0) AS salidas,
    -- MAX() y no la columna pelona: el SELECT ya es agregado y sin GROUP BY,
    -- asi que con sql_mode=only_full_group_by (default desde MySQL 5.7) la
    -- consulta reventaba y el hub mostraba 0 entradas / 0 salidas siempre.
    -- ultima_fecha viene de un CROSS JOIN de una sola fila: MAX() no la cambia.
    -- CAST a CHAR: sin esto MAX() devuelve un DATE que el conector serializa
    -- como objeto JSON, y el cliente Android espera un string en fechaReferencia.
    CAST(MAX(ultima_fecha) AS CHAR) AS fechaReferencia
FROM caseta
CROSS JOIN (
    SELECT COALESCE(
        -- Si viene fecha explicita manda ella y no hay respaldo: el usuario
        -- pidio ESE dia, y un dia sin movimientos debe salir en cero, no
        -- rebotar al ultimo dia con datos (eso era justo la mentira anterior).
        DATE(@fecha),
        (SELECT MAX(DATE(farribo)) FROM caseta
         WHERE DATE(farribo) = CURDATE()),
        (SELECT MAX(DATE(farribo)) FROM caseta
         WHERE farribo IS NOT NULL AND YEAR(farribo) >= 2000)
    ) AS ultima_fecha
) ref
WHERE farribo >= CONCAT(ultima_fecha, ' 00:00:00')
   OR fsalida >= CONCAT(ultima_fecha, ' 00:00:00')",
            new { fecha = fecha?.Date });
    }

    /// <summary>
    /// Entradas y salidas agrupadas por hora del dia indicado (hoy si va null).
    ///
    /// Se reescribio con UNION ALL a proposito: la version anterior agrupaba
    /// TODO por <c>HOUR(farribo)</c>, asi que una caja que llego el martes y
    /// salio el miercoles sumaba su salida en la hora de su ARRIBO. Las barras
    /// de salida estaban en la columna equivocada. Ahora cada evento se agrupa
    /// por su propia hora.
    /// </summary>
    public async Task<List<object>> ObtenerVolumenPorHoraAsync(DateTime? fecha = null)
    {
        using IDbConnection conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<object>(@"
SELECT hora, SUM(entradas) AS entradas, SUM(salidas) AS salidas
FROM (
    SELECT HOUR(farribo) AS hora, 1 AS entradas, 0 AS salidas
    FROM caseta
    WHERE DATE(farribo) = COALESCE(DATE(@fecha), CURDATE())
    UNION ALL
    SELECT HOUR(fsalida) AS hora, 0 AS entradas, 1 AS salidas
    FROM caseta
    WHERE DATE(fsalida) = COALESCE(DATE(@fecha), CURDATE())
) eventos
GROUP BY hora
ORDER BY hora ASC",
            new { fecha = fecha?.Date });

        return rows.ToList();
    }

    private static async Task<List<CasetaEntradaFrequentItem>> ObtenerFrecuentesTransporteAsync(
   IDbConnection conn,
   string idColumn,
   string nameColumn,
   DateTime fromDate,
   int take)
    {
        string sql = $@"
SELECT
    Id,
    MAX(Nombre) AS Nombre,
    COUNT(*) AS Uso,
    MAX(Farribo) AS UltimoUso
FROM (
    SELECT
  NULLIF(TRIM(c.{idColumn}), '') AS Id,
  COALESCE(NULLIF(TRIM(c.{nameColumn}), ''), NULLIF(TRIM(t.cnombre), ''), c.{idColumn}) AS Nombre,
  c.farribo AS Farribo
    FROM caseta c
    LEFT JOIN transpor t ON c.{idColumn} = t.cidtranspor
    WHERE c.farribo >= @fromDate
) frecuentes
WHERE Id IS NOT NULL
GROUP BY Id
ORDER BY Uso DESC, UltimoUso DESC
LIMIT @take";

        return (await conn.QueryAsync<CasetaEntradaFrequentItem>(sql, new { fromDate, take })).ToList();
    }

    private static (string Base64, string MimeType) NormalizeBase64ImagePayload(string value)
    {
        string text = (value ?? "").Trim();
        if (!text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return (text, "image/jpeg");
        }

        int commaIndex = text.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex < 0)
        {
            return (text, "image/jpeg");
        }

        string header = text[..commaIndex];
        string payload = text[(commaIndex + 1)..].Trim();
        string mimeType = "image/jpeg";
        int mimeStart = "data:".Length;
        int mimeEnd = header.IndexOf(';', mimeStart);
        if (mimeEnd > mimeStart)
        {
            mimeType = header[mimeStart..mimeEnd];
        }

        return (payload, mimeType);
    }

    private static async Task<string> ExtractGeminiResponseTextAsync(
   HttpResponseMessage response,
   CancellationToken ct)
    {
        string payload = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);

            if (TryExtractGeminiText(doc.RootElement, out string text))
            {
                return text;
            }

            return doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return payload;
        }
    }

    private static bool TryExtractGeminiText(JsonElement root, out string text)
    {
        text = "";

        if (root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("text", out JsonElement directText) &&
        directText.ValueKind == JsonValueKind.String)
        {
            text = directText.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(text);
        }

        if (root.ValueKind != JsonValueKind.Object ||
        !root.TryGetProperty("candidates", out JsonElement candidates) ||
        candidates.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out JsonElement content) ||
            !content.TryGetProperty("parts", out JsonElement parts) ||
            parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out JsonElement partText) &&
                partText.ValueKind == JsonValueKind.String)
                {
                    string value = partText.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        text = value;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static string ExtractJsonObjectPayload(string raw)
    {
        string cleaned = (raw ?? "")
        .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
        .Replace("```", "")
        .Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "";
        }

        int start = cleaned.IndexOf('{');
        int end = cleaned.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return cleaned[start..(end + 1)].Trim();
        }

        return cleaned;
    }

    private static CasetaEntradaPlateOcrResult ParsePlateOcrResult(string raw)
    {
        string json = ExtractJsonObjectPayload(raw);

        if (string.IsNullOrWhiteSpace(json))
        {
            return new CasetaEntradaPlateOcrResult
            {
                Success = false,
                Message = "OCR no devolvio contenido."
            };
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string plate = TryGetString(root, "placa");
            double? confidence = TryGetDouble(root, "confidence") ?? TryGetDouble(root, "confianza");
            bool geminiSuccess = !root.TryGetProperty("success", out JsonElement successElement) ||
           successElement.ValueKind != JsonValueKind.False;

            string normalized = NormalizePlate(plate);
            var candidates = ParseCandidates(root, "candidates", NormalizePlate);

            if (!geminiSuccess || string.IsNullOrWhiteSpace(normalized))
            {
                string message = TryGetString(root, "message");
                return new CasetaEntradaPlateOcrResult
                {
                    Success = false,
                    Placa = "",
                    Confidence = confidence,
                    Message = string.IsNullOrWhiteSpace(message) ? "No se encontro una placa clara." : message,
                    Candidates = candidates
                };
            }

            if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(normalized))
            {
                candidates.Add(new CasetaEntradaOcrCandidate
                {
                    Value = normalized,
                    Confidence = confidence,
                    Source = "server",
                    Reason = "Gemini"
                });
            }

            return new CasetaEntradaPlateOcrResult
            {
                Success = true,
                Placa = normalized,
                Confidence = confidence,
                Message = "Placa detectada.",
                Candidates = candidates
            };
        }
        catch (JsonException)
        {
            return new CasetaEntradaPlateOcrResult
            {
                Success = false,
                Message = "OCR devolvio una respuesta no valida."
            };
        }
    }

    private static CasetaEntradaBoxOcrResult ParseBoxOcrResult(string raw)
    {
        string json = ExtractJsonObjectPayload(raw);

        if (string.IsNullOrWhiteSpace(json))
        {
            return new CasetaEntradaBoxOcrResult
            {
                Success = false,
                Message = "OCR no devolvio contenido."
            };
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string box = FirstNonEmpty(
            TryGetString(root, "numCaja"),
            TryGetString(root, "caja"),
            TryGetString(root, "boxNumber"));
            double? confidence = TryGetDouble(root, "confidence") ?? TryGetDouble(root, "confianza");
            bool geminiSuccess = !root.TryGetProperty("success", out JsonElement successElement) ||
           successElement.ValueKind != JsonValueKind.False;

            string normalized = NormalizeBoxNumber(box);
            var candidates = ParseCandidates(root, "candidates", NormalizeBoxNumber);

            if (!geminiSuccess || string.IsNullOrWhiteSpace(normalized))
            {
                string message = TryGetString(root, "message");
                return new CasetaEntradaBoxOcrResult
                {
                    Success = false,
                    NumCaja = "",
                    Confidence = confidence,
                    Message = string.IsNullOrWhiteSpace(message) ? "No se encontro una caja clara." : message,
                    Candidates = candidates
                };
            }

            if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(normalized))
            {
                candidates.Add(new CasetaEntradaOcrCandidate
                {
                    Value = normalized,
                    Confidence = confidence,
                    Source = "server",
                    Reason = "Gemini"
                });
            }

            return new CasetaEntradaBoxOcrResult
            {
                Success = true,
                NumCaja = normalized,
                Confidence = confidence,
                Message = "Caja detectada.",
                Candidates = candidates
            };
        }
        catch (JsonException)
        {
            return new CasetaEntradaBoxOcrResult
            {
                Success = false,
                Message = "OCR devolvio una respuesta no valida."
            };
        }
    }

    private static List<CasetaEntradaOcrCandidate> ParseCandidates(
 JsonElement root,
 string propertyName,
 Func<string, string> normalizer)
    {
        var result = new List<CasetaEntradaOcrCandidate>();
        if (!root.TryGetProperty(propertyName, out JsonElement arrayElement) || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string value = FirstNonEmpty(
            TryGetString(item, "value"),
            TryGetString(item, "placa"),
            TryGetString(item, "numCaja"));
            string normalized = normalizer(value);
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            result.Add(new CasetaEntradaOcrCandidate
            {
                Value = normalized,
                Confidence = TryGetDouble(item, "confidence"),
                Source = string.IsNullOrWhiteSpace(TryGetString(item, "source")) ? "server" : TryGetString(item, "source"),
                Reason = TryGetString(item, "reason")
            });
        }

        return result
        .GroupBy(c => c.Value)
        .Select(g => new CasetaEntradaOcrCandidate
        {
            Value = g.Key,
            Confidence = g.Max(x => x.Confidence ?? 0),
            Source = string.Join("+", g.Select(x => x.Source).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()),
            Reason = g.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Reason))?.Reason ?? string.Empty
        })
        .OrderByDescending(c => c.Confidence ?? 0)
        .ToList();
    }

    private static string NormalizePlate(string value)
    {
        string compact = Regex.Replace((value ?? "").ToUpperInvariant(), @"\s+", "");
        compact = Regex.Replace(compact, @"[^A-Z0-9-]", "");
        return Regex.IsMatch(compact, @"^[A-Z0-9-]{4,15}$") ? compact : "";
    }

    private static string NormalizeBoxNumber(string value)
    {
        string compact = Regex.Replace((value ?? "").ToUpperInvariant(), @"\s+", "");
        compact = Regex.Replace(compact, @"[^A-Z0-9-]", "");
        return Regex.IsMatch(compact, @"^[A-Z0-9-]{3,20}$") ? compact : "";
    }

    private static string TryGetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            return "";
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    private static double? TryGetDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out double value) => value,
            JsonValueKind.String when double.TryParse(element.GetString(), out double value) => value,
            _ => null
        };
    }

    private async Task<IEnumerable<CasetaEntradaCatalogItem>> GetClientesEntradaAsync(
   IDbConnection conn,
   string? filtro,
   int limite)
    {
        string filtroLimpio = (filtro ?? "").Trim();
        string like = "%" + filtroLimpio + "%";
        string limitSql = limite > 0 ? "LIMIT @limite" : "";

        return await conn.QueryAsync<CasetaEntradaCatalogItem>($@"
SELECT Id, Nombre
FROM (
    SELECT codigo AS Id, Nombre AS Nombre
    FROM view_catalogo2
    WHERE catalogo = 'CLIENTES' AND inactivo = '0'
    UNION
    SELECT cidcte AS Id, cnombre AS Nombre
    FROM clientes
    WHERE inactivo = '0' AND tipomv = 'OPERACION'
) clientes_caseta
WHERE (@filtro = '' OR Id LIKE @like OR Nombre LIKE @like)
ORDER BY Nombre
{limitSql}", new { filtro = filtroLimpio, like, limite });
    }

    internal static List<CasetaEntradaCatalogItem> ObtenerPaisesEntrada()
    {
        return
        [
        new() { Id = "FRA", Nombre = "FRANCIA (REPUBLICA FRANCESA)" },
   new() { Id = "JPN", Nombre = "JAPON" },
   new() { Id = "KOR", Nombre = "COREA (REPUBLICA DE) (COREA DEL SUR)" },
   new() { Id = "MEX", Nombre = "MEXICO (ESTADOS UNIDOS MEXICANOS)" },
   new() { Id = "USA", Nombre = "ESTADOS UNIDOS DE AMERICA" }
        ];
    }

    private static List<string> ValidarEntradaCaseta(CasetaEntradaGuardarRequest datos)
    {
        var errores = new List<string>();

        if (string.IsNullOrWhiteSpace(datos.Operacion))
            errores.Add("Operacion");

        // Un tractocamion sin caja (TipoRegistro=TRACTOCAMION, venga de un QR de
        // pre-registro o capturado a mano en garita) no tiene cliente, caja ni
        // linea de caja: son datos que dependen de una carga que ese camion no
        // trae. OrigenPreRegistro ya NO decide esto -- es solo dato de
        // procedencia, ver su comentario en CasetaModels.cs.
        if (!string.Equals(datos.TipoRegistro, "TRACTOCAMION", StringComparison.OrdinalIgnoreCase))
        {
            if (IsEmptySelection(datos.Cliente) && string.IsNullOrWhiteSpace(datos.IdCliente))
                errores.Add("Cliente");

            if (IsEmptySelection(datos.LineaCaja) && string.IsNullOrWhiteSpace(datos.IdLineaCaja))
                errores.Add("Linea Caja");

            if (string.IsNullOrWhiteSpace(datos.NumCaja))
                errores.Add("Numero de Caja");
        }

        if (IsEmptySelection(datos.LineaCamion) && string.IsNullOrWhiteSpace(datos.IdLineaCamion))
            errores.Add("Linea Camion");

        if (IsEmptySelection(datos.Movimiento))
            errores.Add("Movimiento");

        if (datos.Movimiento is "COLOCACION/VACIO" or "DIRECTO")
        {
            if (string.IsNullOrWhiteSpace(datos.Placas))
                errores.Add("Placas");

            if (IsEmptySelection(datos.IdPais))
                errores.Add("Pais");
        }

        if (!string.IsNullOrWhiteSpace(datos.Guia) && !int.TryParse(datos.Guia, out _))
            errores.Add("Guia numerica");

        if (!string.IsNullOrWhiteSpace(datos.Pedimento) && !Regex.IsMatch(datos.Pedimento.Trim(), @"^\d{7}$"))
            errores.Add("Pedimento de 7 digitos");

        if (IsCajaRefrigerada(datos) &&
        (string.IsNullOrWhiteSpace(datos.TemperaturaOculta) || string.IsNullOrWhiteSpace(datos.Combustible)))
        {
            errores.Add("Temperatura y combustible de caja refrigerada");
        }

        if (datos.Fotos.Count > 15)
            errores.Add("Maximo 15 fotos");

        return errores;
    }

    private static bool IsEmptySelection(string? value)
    {
        string text = (value ?? "").Trim();
        return string.IsNullOrWhiteSpace(text)
       || text == "0"
       || text.Equals("Vacio", StringComparison.OrdinalIgnoreCase)
       || text.StartsWith("-- Selecciona", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCajaRefrigerada(CasetaEntradaGuardarRequest datos)
    {
        string tipo = FirstNonEmpty(datos.DescTipoCaja, datos.TipoCaja);
        return tipo.Equals("CAJA REFRIGERADA 53'", StringComparison.OrdinalIgnoreCase);
    }

    private static CasetaEntradaRegistro ConstruirEntradaCaseta(CasetaEntradaGuardarRequest datosF, string usuarioActual)
    {
        var lineaCamion = ResolveCatalogPair(datosF.IdLineaCamion, datosF.NombreLineaCamion, datosF.LineaCamion);
        var lineaCaja = ResolveCatalogPair(datosF.IdLineaCaja, datosF.NombreLineaCaja, datosF.LineaCaja);
        var cliente = ResolveCatalogPair(datosF.IdCliente, datosF.NombreCliente, datosF.Cliente);

        string idTipoCaja = datosF.IdTipoCaja == "0" ? "" : datosF.IdTipoCaja.Trim();
        string descTipoCaja = datosF.IdTipoCaja == "0"
       ? ""
       : FirstNonEmpty(datosF.DescTipoCaja, datosF.TipoCaja);

        string casetaId = FirstNonEmpty(datosF.Casetaid, "N/A");
        // La identidad del movimiento siempre sale del JWT; los campos de
        // usuario del cuerpo se conservan por compatibilidad con clientes.
        string usuario = FirstNonEmpty(usuarioActual, "sistema");

        return new CasetaEntradaRegistro
        {
            CasetaId = casetaId,
            IdBodega = datosF.IdBodega.Trim(),
            Bodega = datosF.Bodega.Trim(),
            TipoOp = datosF.Operacion.Trim(),
            TipoMov = datosF.Movimiento.Trim(),
            TipoRegistro = string.Equals(datosF.TipoRegistro, "TRACTOCAMION", StringComparison.OrdinalIgnoreCase)
                ? "TRACTOCAMION"
                : "CAJA",
            IdUnidad = idTipoCaja,
            Tipouni = descTipoCaja,
            NumUnidad = datosF.NoCamion.Trim(),
            IdTransporteTractor = lineaCamion.Id,
            Transpor = lineaCamion.Nombre,
            Nequipo = datosF.NumCaja.Trim(),
            IdTransporteCaja = lineaCaja.Id,
            Transporte = lineaCaja.Nombre,
            Placas = datosF.Placas.Trim(),
            Sellos = datosF.Sellos.Trim(),
            Farribo = DateTime.Now,
            Usucrea = usuario,
            NombreUsuario = usuario,
            Fcrea = DateTime.Now,
            Usumodi = usuario,
            Fmodi = DateTime.Now,
            IdCliente = cliente.Id,
            NombreCliente = cliente.Nombre,
            Observaciones = datosF.Observaciones.Trim(),
            Pedimento = datosF.Pedimento.Trim(),
            Temperatura = datosF.TemperaturaOculta.Trim(),
            Guias = string.IsNullOrWhiteSpace(datosF.Guia) ? 0 : Convert.ToInt32(datosF.Guia),
            IdPais = datosF.IdPais == "0" ? "" : datosF.IdPais.Trim(),
            Pais = datosF.IdPais == "0" ? "" : datosF.Pais.Trim(),
            Operador = datosF.Operador.Trim(),
            Combustible = datosF.Combustible.Trim(),
            Planta = datosF.Planta.Trim(),
            RequiereImpo = datosF.RequiereImpo,
            Actualizado = datosF.Actualizado,
            EsPreRegistro = datosF.OrigenPreRegistro || !string.IsNullOrWhiteSpace(datosF.PreregistroFolio)
        };
    }

    private static (string Id, string Nombre) ResolveCatalogPair(string id, string nombre, string etiqueta)
    {
        string idValue = (id ?? "").Trim();
        string nameValue = (nombre ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(idValue) || !string.IsNullOrWhiteSpace(nameValue))
            return (idValue, nameValue);

        string label = (etiqueta ?? "").Trim();
        if (IsEmptySelection(label))
            return ("", "");

        int separator = label.IndexOf(" - ", StringComparison.Ordinal);
        if (separator < 0)
            separator = label.IndexOf('-', StringComparison.Ordinal);

        if (separator <= 0)
            return ("", label);

        string left = label[..separator].Trim();
        string right = label[(separator + (label.Substring(separator).StartsWith(" - ", StringComparison.Ordinal) ? 3 : 1))..].Trim();
        return (left, right);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private static string BuildCatalogText(string id, string nombre)
    {
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(nombre))
            return string.Empty;

        if (string.IsNullOrWhiteSpace(id))
            return nombre.Trim();

        if (string.IsNullOrWhiteSpace(nombre))
            return id.Trim();

        return $"{id.Trim()} - {nombre.Trim()}";
    }

    /// <summary>
    /// Parsea la lista de fotos del request distinguiendo entre:
    /// - Marcadores "keep:N" → foto que ya existe en servidor con índice N, no se re-sube
    /// - Base64 real → foto nueva que se debe subir
    /// Asigna índices estables: los keep conservan su índice original,
    /// las nuevas reciben índices que no colisionen con los keep.
    /// </summary>
    private static List<CasetaEntradaFoto> PrepararFotosEntrada(List<string> fotos)
    {
        var result = new List<CasetaEntradaFoto>();

        // Primera pasada: recoger todos los índices keep para evitar colisiones
        var keepIndices = new HashSet<int>();
        foreach (string foto in fotos ?? [])
        {
            if (TryParseKeepMarker(foto, out int keepIdx))
                keepIndices.Add(keepIdx);
        }

        // Segunda pasada: armar la lista final con índices estables
        int nextNewIndex = 1;
        foreach (string foto in fotos ?? [])
        {
            if (string.IsNullOrWhiteSpace(foto))
                continue;

            // Marcador keep:N → foto ya existe, conservar índice original
            if (TryParseKeepMarker(foto, out int keepIndex))
            {
                result.Add(new CasetaEntradaFoto
                {
                    BytesFoto = [],
                    Base64 = "",
                    TipoEtiqueta = "keep",
                    Indice = keepIndex
                });
                continue;
            }

            // Base64 real → foto nueva
            byte[]? bytes = TryDecodeBase64Image(foto);
            if (bytes == null)
                continue;

            // Buscar el próximo índice libre que no colisione con ningún keep
            while (keepIndices.Contains(nextNewIndex))
                nextNewIndex++;

            result.Add(new CasetaEntradaFoto
            {
                BytesFoto = bytes,
                Base64 = foto,
                TipoEtiqueta = "normal",
                Indice = nextNewIndex
            });

            keepIndices.Add(nextNewIndex); // marcar como usado
            nextNewIndex++;
        }

        return result;
    }

    /// <summary>Parsea un marcador "keep:N" y devuelve el índice N. Retorna false si no es un marcador.</summary>
    private static bool TryParseKeepMarker(string value, out int index)
    {
        index = 0;
        string trimmed = (value ?? "").Trim();
        if (!trimmed.StartsWith("keep:", StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(trimmed[5..], out index) && index > 0;
    }

    private static byte[]? TryDecodeBase64Image(string image)
    {
        string payload = image.Trim();
        int commaIndex = payload.IndexOf(',');
        if (commaIndex >= 0)
            payload = payload[(commaIndex + 1)..];

        payload = Regex.Replace(payload, @"\s+", "");

        try
        {
            return Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task<bool> ExisteCasetaIdEnBDAsync(IDbConnection conn, IDbTransaction tx, string casetaId)
    {
        if (IsEmptySelection(casetaId) || casetaId.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            return false;

        int count = await conn.ExecuteScalarAsync<int>(
        "SELECT COUNT(*) FROM caseta WHERE casetaId = @casetaId",
        new { casetaId },
        tx);

        return count > 0;
    }

    private async Task<CasetaEntradaGuardarResult> ActualizarEntradaCasetaYFotosAsync(
   IDbConnection conn,
   IDbTransaction tx,
   CasetaEntradaRegistro caseta,
   List<CasetaEntradaFoto> listaFotos,
   List<CasetaPhotoStage> fotosStaged,
   List<string> fotosParaEliminar,
   IReadOnlyCollection<int> indicesFotosEliminar,
   CancellationToken ct)
    {
        int rows = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE caseta SET
    idbodega = @IdBodega,
    bodega = @Bodega,
    tipoop = @TipoOp,
    tipomov = @TipoMov,
    tiporegistro = @TipoRegistro,
    iduni = @IdUnidad,
    tipouni = @Tipouni,
    nunidad = @NumUnidad,
    idtransporu = @IdTransporteTractor,
    transporu = @Transpor,
    nequipo = @Nequipo,
    idtranspore = @IdTransporteCaja,
    transpore = @Transporte,
    placas = @Placas,
    sellos = @Sellos,
    usumodi = @Usumodi,
    fmodi = @Fmodi,
    cclienteid = @IdCliente,
    obs_entrada = @Observaciones,
    temperatura = @Temperatura,
    cpedimento = @Pedimento,
    cant_guia = @Guias,
    idpais = @IdPais,
    pais = @Pais,
    operador = @Operador,
    combustible = @Combustible,
    planta = @Planta
WHERE casetaid = @CasetaId",
        caseta,
        tx,
        cancellationToken: ct));

        if (rows <= 0)
        {
            return new CasetaEntradaGuardarResult { Success = false, Message = "No se actualizo ningun registro." };
        }

        var fotosResult = await GuardarFotosEntradaCasetaAsync(
            conn, tx, caseta, listaFotos, fotosStaged, fotosParaEliminar, indicesFotosEliminar, ct);
        if (!fotosResult.Success)
            return fotosResult;

        fotosResult.CasetaId = caseta.CasetaId;
        fotosResult.CasetaIds = [caseta.CasetaId];
        return fotosResult;
    }

    private async Task<CasetaEntradaGuardarResult> GuardarEntradaCasetaYFotosAsync(
   IDbConnection conn,
   IDbTransaction tx,
   CasetaEntradaRegistro caseta,
   List<CasetaEntradaFoto> listaFotos,
   List<CasetaPhotoStage> fotosStaged,
   CancellationToken ct)
    {
        string? casetaId = await conn.ExecuteScalarAsync<string>(new CommandDefinition(@"
SELECT CONCAT(prefijo,'-0',numero,'/',RIGHT(YEAR(CURDATE()), 2))
FROM consecutivo
WHERE formulario = 'CASETAE'
FOR UPDATE", transaction: tx, cancellationToken: ct));

        if (string.IsNullOrWhiteSpace(casetaId))
            return new CasetaEntradaGuardarResult { Success = false, Message = "No se pudo obtener el CasetaID." };

        int idCaseta = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT IFNULL(MAX(idcaseta), 0) + 1 FROM caseta",
        transaction: tx,
        cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
        "UPDATE consecutivo SET numero = numero + 1 WHERE formulario = 'CASETAE'",
        transaction: tx,
        cancellationToken: ct));

        caseta.IdCaseta = idCaseta;
        caseta.CasetaId = casetaId;

        int rows = await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO caseta (
    idcaseta, casetaid, idbodega, bodega, tipoop, tipomov, tiporegistro, iduni, tipouni, nunidad,
    idtransporu, transporu, nequipo, idtranspore, transpore, placas, sellos, usucrea,
    fcrea, usumodi, fmodi, estatus, cclienteid, obs_entrada, temperatura, cpedimento,
    cant_guia, idpais, pais, operador, combustible, planta, farribo)
VALUES (
    @IdCaseta, @CasetaId, @IdBodega, @Bodega, @TipoOp, @TipoMov, @TipoRegistro, @IdUnidad, @Tipouni, @NumUnidad,
    @IdTransporteTractor, @Transpor, @Nequipo, @IdTransporteCaja, @Transporte, @Placas, @Sellos, @Usucrea,
    @Fcrea, @Usumodi, @Fmodi, 'DISPONIBLE', @IdCliente, @Observaciones, @Temperatura, @Pedimento,
    @Guias, @IdPais, @Pais, @Operador, @Combustible, @Planta, @Farribo)",
        caseta,
        tx,
        cancellationToken: ct));

        if (rows <= 0)
            return new CasetaEntradaGuardarResult { Success = false, Message = "No se inserto ningun registro." };

        var fotosResult = await GuardarFotosEntradaCasetaAsync(
            conn, tx, caseta, listaFotos, fotosStaged, new List<string>(), Array.Empty<int>(), ct);
        if (!fotosResult.Success)
            return fotosResult;

        fotosResult.CasetaId = caseta.CasetaId;
        fotosResult.CasetaIds = [caseta.CasetaId];
        return fotosResult;
    }

    private async Task<CasetaEntradaGuardarResult> GuardarFotosEntradaCasetaAsync(
   IDbConnection conn,
   IDbTransaction tx,
   CasetaEntradaRegistro caseta,
   List<CasetaEntradaFoto> listaFotos,
   List<CasetaPhotoStage> fotosStaged,
   List<string> fotosParaEliminar,
   IReadOnlyCollection<int> indicesFotosEliminar,
   CancellationToken ct)
    {
        try
        {
            // BUG FIX: filtrar SOLO fotos de caseta (ntipodocto=106) para no
            // interferir con fotos de revisión (107), entrada (100), etc.
            var existentes = (await conn.QueryAsync<int>(
           "SELECT nlineno FROM fotos WHERE creferencia = @creferencia AND ntipodocto = 106",
           new { creferencia = caseta.CasetaId },
           tx)).ToList();

            // Separar entre fotos que se conservan (keep) y fotos nuevas a subir
            var fotosKeep = listaFotos.Where(f => f.TipoEtiqueta == "keep").ToList();
            var fotosNuevas = listaFotos.Where(f => f.TipoEtiqueta != "keep").ToList();
            var indicesFotosNuevas = fotosNuevas.Select(f => f.Indice).ToHashSet();

            // Solo borrar fotos que el usuario quitó explícitamente. No inferimos una baja porque un índice falte
            // de la lista: una lectura parcial de la unidad de red no debe borrar evidencia de la base ni del disco.
            foreach (int indice in existentes.Intersect(indicesFotosEliminar).ToList())
            {
                await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM fotos WHERE nlineno = @nlineno AND creferencia = @creferencia AND ntipodocto = 106",
                new { nlineno = indice, creferencia = caseta.CasetaId },
                tx,
                cancellationToken: ct));

                existentes.Remove(indice);
                if (!indicesFotosNuevas.Contains(indice))
                {
                    string archivo = _photoStorage.GetFileName(caseta.CasetaId, indice);
                    fotosParaEliminar.Add(archivo);
                }
            }

            // Las fotos keep ya existen en BD y en disco → no hacemos nada con ellas
            // (el registro en BD se preserva porque no lo eliminamos arriba)

            // Solo procesar las fotos nuevas (base64 real)
            foreach (CasetaEntradaFoto foto in fotosNuevas)
            {
                if (existentes.Contains(foto.Indice))
                {
                    // El índice ya existe → actualizar registro y reescribir archivo
                    await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE fotos
SET cextension = 'jpg', usucrea = @usucrea, fcrea = @fcrea
WHERE nlineno = @nlineno AND creferencia = @creferencia AND ntipodocto = 106",
                    new
                    {
                        nlineno = foto.Indice,
                        creferencia = caseta.CasetaId,
                        usucrea = caseta.Usucrea,
                        fcrea = DateTime.Now
                    },
                    tx,
                    cancellationToken: ct));
                }
                else
                {
                    // Índice nuevo → insertar registro
                    await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO fotos (nlineno, creferencia, ntipodocto, cextension, usucrea, fcrea)
VALUES (@nlineno, @creferencia, 106, 'jpg', @usucrea, @fcrea)",
                    new
                    {
                        nlineno = foto.Indice,
                        creferencia = caseta.CasetaId,
                        usucrea = caseta.Usucrea,
                        fcrea = DateTime.Now
                    },
                    tx,
                    cancellationToken: ct));
                }

                foto.NombreArchivo = _photoStorage.GetFileName(caseta.CasetaId, foto.Indice);
                fotosStaged.Add(await _photoStorage.StageAsync(foto.BytesFoto, foto.NombreArchivo, ct));
            }

            return new CasetaEntradaGuardarResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error guardando fotos de caseta {CasetaId}", caseta.CasetaId);
            return new CasetaEntradaGuardarResult { Success = false, Message = "Error al guardar fotos de caseta." };
        }
    }

    private async Task<(List<CasetaEntradaFotoInfo> Info, List<string> Base64)> LeerFotosEntradaCasetaAsync(IDbConnection conn, string casetaId, bool incluirBase64)
    {
        var info = new List<CasetaEntradaFotoInfo>();
        var base64 = new List<string>();
        var byIndex = new SortedDictionary<int, (CasetaEntradaFotoInfo Info, string Extension)>();

        foreach (dynamic row in await conn.QueryAsync(@"
SELECT
    nlineno AS Indice,
    COALESCE(NULLIF(cextension, ''), 'jpg') AS Extension,
    idfotos AS IdFotos,
    CAST(IFNULL(UNIX_TIMESTAMP(fcrea), 0) AS SIGNED) AS FcreaUnix
FROM fotos
WHERE creferencia = @casetaId AND ntipodocto = 106
ORDER BY nlineno, idfotos", new { casetaId }))
        {
            int indice = Convert.ToInt32(row.Indice ?? 0);
            if (indice <= 0)
                continue;

            string extension = Convert.ToString(row.Extension ?? "jpg")?.Trim().TrimStart('.') ?? "jpg";
            string nombre = _photoStorage.GetCandidateFileNames(casetaId, indice, extension).FirstOrDefault()
           ?? _photoStorage.GetFileName(casetaId, indice);
            // La misma versión que usa Foto/caseta/ver: con ella la tableta guarda la foto y no la vuelve a pedir.
            string version = CasetaFotoCache.Version(Convert.ToInt64(row.IdFotos ?? 0), Convert.ToInt64(row.FcreaUnix ?? 0));
            byIndex[indice] = (new CasetaEntradaFotoInfo { Indice = indice, NombreArchivo = nombre, Version = version }, extension);
        }

        // El detalle movil pide solo metadatos. No recorrer la unidad de red en ese caso:
        // los registros de fotos ya están en BD y la app descarga las imagenes despues.
        if (incluirBase64)
        {
            foreach (string archivo in _photoStorage.EnumerateCasetaPhotos(casetaId))
            {
                string nombre = Path.GetFileName(archivo);
                int indice = TryExtractPhotoIndex(nombre);
                if (indice <= 0)
                    continue;

                string extension = Path.GetExtension(nombre).TrimStart('.');
                if (string.IsNullOrWhiteSpace(extension))
                    extension = "jpg";
                byIndex.TryAdd(indice, (new CasetaEntradaFotoInfo { Indice = indice, NombreArchivo = nombre }, extension));
            }
        }

        foreach (var entry in byIndex.Values)
        {
            info.Add(entry.Info);

            if (!incluirBase64)
                continue;

            try
            {
                string? path = _photoStorage.FindReadablePhotoPath(casetaId, entry.Info.Indice, entry.Extension);
                if (path == null)
                {
                    _logger.LogWarning("Foto de caseta registrada en BD pero no encontrada en disco: {CasetaId}/{Indice}", casetaId, entry.Info.Indice);
                    // Mantener base64 e info alineados por posición. El cliente usa esta posición para conocer el
                    // índice original; omitir el elemento desplazaría todas las fotos siguientes.
                    base64.Add(string.Empty);
                    continue;
                }

                byte[] bytes = await File.ReadAllBytesAsync(path);
                string mime = entry.Extension.ToLowerInvariant() switch
                {
                    "png" => "image/png",
                    "webp" => "image/webp",
                    _ => "image/jpeg"
                };
                base64.Add($"data:{mime};base64," + Convert.ToBase64String(bytes));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo leer foto de caseta {CasetaId}/{Indice}", casetaId, entry.Info.Indice);
                base64.Add(string.Empty);
            }
        }

        return (info, base64);
    }

    private static int TryExtractPhotoIndex(string fileName)
    {
        Match match = Regex.Match(fileName, @"-(\d{4})\.[a-z0-9]+$", RegexOptions.IgnoreCase);
        if (!match.Success)
            match = Regex.Match(fileName, @"-106(\d{2})\.[a-z0-9]+$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : 0;
    }

    /// <summary>
    /// Envío DIRECTO del correo de entrada, sin cola: lo usan el reenvío manual y los modos admin de línea de comandos,
    /// donde quien lo pide necesita ver en pantalla si salió o no.
    /// </summary>
    private async Task<bool> EnviarCorreoEntradaCasetaAsync(
   CasetaEntradaRegistro datos,
   List<CasetaEntradaFoto> fotos,
   CancellationToken ct,
   IEnumerable<string>? destinatariosOverride = null,
   bool incluirCopias = true,
   bool exigirEnlace = false)
    {
        try
        {
            MimeMessage? msg = await ConstruirCorreoEntradaCasetaAsync(datos, fotos, ct, destinatariosOverride, incluirCopias, exigirEnlace);
            if (msg == null) return false;

            await _transporte.EnviarAsync(msg, ct);
            _logger.LogInformation(
                "Correo de entrada de caseta enviado. CasetaId={CasetaId}, Para={To}, Cc={Cc}, Fotos={Fotos}",
                datos.CasetaId,
                string.Join(", ", msg.To.Mailboxes.Select(x => x.Address)),
                string.Join(", ", msg.Cc.Mailboxes.Select(x => x.Address)),
                fotos.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando correo de entrada caseta {CasetaId}", datos.CasetaId);
            return false;
        }
    }

    /// <summary>
    /// Arma el mensaje completo del correo de entrada (destinatarios, asunto, cuerpo y enlace de fotos) sin mandarlo.
    /// Devuelve null cuando no hay a quién mandárselo. [incluirCopias]=false deja fuera a Calidad y a Desarrollo: es lo
    /// que usa la validación, que no debe copiar a nadie más que a la casilla de pruebas.
    /// </summary>
    private async Task<MimeMessage?> ConstruirCorreoEntradaCasetaAsync(
   CasetaEntradaRegistro datos,
   List<CasetaEntradaFoto> fotos,
   CancellationToken ct,
   IEnumerable<string>? destinatariosOverride = null,
   bool incluirCopias = true,
   bool exigirEnlace = false)
    {
        {
            var msg = new MimeMessage();
            string from = _config["CasetaEntrada:SmtpFrom"] ?? _config["Smtp:User"] ?? "postmaster@mg.imexnotify.com";

            string fromName = _config["CasetaEntrada:SmtpFromName"] ?? "Notificaciones PMM";

            msg.From.Add(new MailboxAddress(fromName, from));

            // El enlace puede ejecutarse despues de que el correo se encola
            // (por ejemplo, en el controlador que consume el QR). Confirmamos
            // aqui tambien contra la tabla de pre-registro para que una version
            // vieja de la app nunca mande el correo operativo de un arribo QR.
            if (!datos.EsPreRegistro)
                datos.EsPreRegistro = await EsEntradaDePreregistroAsync(datos.CasetaId, ct);

            // destinatariosOverride es para el reenvío manual (ReenviarCorreoEntradaAsync):
            // ahí la intención ya es explícita (validar con desarrollo3 o mandar a los
            // reales) y no debe pasar por la protección de entorno, que es solo para el
            // envío automático de EncolarCorreoEntradaCaseta al guardar una entrada nueva.
            IEnumerable<string> destinatarios;
            if (datos.EsPreRegistro)
            {
                destinatarios = new[] { CorreoPruebasDesarrollo };
                _logger.LogInformation(
                    "Entrada de caseta {CasetaId} originada en pre-registro: correo redirigido únicamente a {Destino}.",
                    datos.CasetaId,
                    CorreoPruebasDesarrollo);
            }
            else if (destinatariosOverride != null)
            {
                destinatarios = destinatariosOverride;
            }
            else
            {
                destinatarios = GetEntradaCasetaRecipients(datos);
                if (_env.IsDevelopment())
                {
                    _logger.LogInformation(
                        "CasetaService: entorno Development, se redirige el correo de entrada de caseta (destinatarios originales: {Original}) a {Destino}.",
                        string.Join(", ", destinatarios),
                        CorreoPruebasDesarrollo);
                    destinatarios = new[] { CorreoPruebasDesarrollo };
                }
            }

            foreach (string destinatario in destinatarios)
                AddMailbox(msg.To, destinatario);

            if (!datos.EsPreRegistro && incluirCopias)
            {
                AddMailbox(msg.Cc, CorreoCalidad);
                AddMailbox(msg.Cc, CorreoPruebasDesarrollo);
                AddMailbox(msg.Cc, CorreoDesarrolloPrincipal);
            }

            if (msg.To.Count == 0)
            {
                _logger.LogWarning("Entrada caseta sin destinatarios");
                return null;
            }

            string prefix = datos.Actualizado ? "[ACTUALIZACIÓN] " : "";
            string bodegaMail = datos.Planta == "TRADE CTR" ? $"B4 {datos.Planta}" : $"B1 {datos.Planta}";
            bool esTractocamion = string.Equals(datos.TipoRegistro, "TRACTOCAMION", StringComparison.OrdinalIgnoreCase);

            msg.Subject = esTractocamion
                ? $"{prefix}Entrada Caseta: {bodegaMail} | Tractocamión: {datos.NumUnidad} | {datos.TipoOp} | {datos.TipoMov} | Operador: {datos.Operador} "
                : $"{prefix}Entrada Caseta: {bodegaMail} | Caja: {datos.Nequipo} | {datos.TipoOp} | {datos.TipoMov} | {datos.NombreCliente} ";

            string htmlBody = esTractocamion
                ? BuildEntradaCasetaTractocamionEmailBody(datos, bodegaMail)
                : BuildEntradaCasetaEmailBody(datos, bodegaMail);
            // Un fallo con el enlace de fotos (tabla, URL base, etc.) NO puede tirar el aviso entero: el correo sale sin
            // el bloque de fotos y el error queda en el log. Antes la excepción se tragaba la notificación completa.
            try
            {
                var enlaceFotos = await _casetaShareService.GenerarLinkEntradaAsync(datos.CasetaId, datos.Usucrea);
                if (enlaceFotos.HasValue)
                {
                    string baseUrl = (_config["PublicLinks:ApiBaseUrl"] ?? string.Empty).TrimEnd('/');
                    if (baseUrl.Length == 0)
                    {
                        throw new InvalidOperationException("Falta configurar PublicLinks:ApiBaseUrl para compartir las fotos de entrada.");
                    }

                    string url = $"{baseUrl}/api/publico/caseta/{enlaceFotos.Value.Token}";
                    string bloqueFotos = CasetaShareHtmlRenderer.RenderEmailLink(url, CasetaShareService.TipoEntrada, enlaceFotos.Value.Expira);
                    htmlBody = CasetaShareHtmlRenderer.InsertarEnCorreo(htmlBody, bloqueFotos);
                }
                else if (fotos.Count > 0)
                {
                    throw new InvalidOperationException("No se pudo crear el enlace de las fotos de entrada.");
                }
            }
            catch (Exception ex)
            {
                // El reenvío manual pide el enlace a propósito: ahí es mejor no mandar nada que mandarlo sin él.
                if (exigirEnlace) throw;
                _logger.LogError(ex, "Correo de entrada de caseta {CasetaId}: no se pudo crear el enlace de fotos; el aviso sale sin él.", datos.CasetaId);
            }

            var builder = new BodyBuilder { HtmlBody = htmlBody };

            msg.Body = builder.ToMessageBody();
            return msg;
        }
    }

    private async Task<bool> EsEntradaDePreregistroAsync(string casetaId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(casetaId))
            return false;

        try
        {
            using IDbConnection conn = _db.CreatePmmutileriaConnection();
            int count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
                SELECT COUNT(*)
                FROM caseta_preregistro
                WHERE casetaid = @casetaId",
                new { casetaId }, cancellationToken: ct));
            return count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "No se pudo verificar si la entrada de caseta {CasetaId} proviene de un pre-registro.",
                casetaId);
            return false;
        }
    }

    private async Task<bool> TienePreregistroVigenteAsync(
        CasetaEntradaGuardarRequest entrada,
        CancellationToken ct)
    {
        // Una entrada con caja nunca es el arribo de un pre-registro (que es de tractocamion sin caja): ver ArriboPreregistro.
        if (!ArriboPreregistro.PuedeCoincidirPorUnidad(entrada.TipoRegistro))
            return false;

        string nunidad = (entrada.NoCamion ?? string.Empty).Trim();
        string placas = (entrada.Placas ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(nunidad) && string.IsNullOrWhiteSpace(placas))
            return false;

        try
        {
            using IDbConnection conn = _db.CreatePmmutileriaConnection();
            int count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
                SELECT COUNT(*)
                FROM caseta_preregistro
                WHERE estatus = @vigente
                  AND fexpira >= NOW()
                  AND (@nunidad = '' OR UPPER(TRIM(nunidad)) = UPPER(@nunidad))
                  AND (@placas = '' OR UPPER(TRIM(placas)) = UPPER(@placas))
                  AND (@idBodega = '' OR idbodega = @idBodega)
                  AND (@idLinea = '' OR idtransporu = @idLinea
                       OR UPPER(TRIM(transporu)) = UPPER(@nombreLinea))",
                new
                {
                    vigente = "VIGENTE",
                    nunidad,
                    placas,
                    idBodega = (entrada.IdBodega ?? string.Empty).Trim(),
                    idLinea = (entrada.IdLineaCamion ?? string.Empty).Trim(),
                    nombreLinea = (entrada.NombreLineaCamion ?? entrada.LineaCamion ?? string.Empty).Trim()
                }, cancellationToken: ct));
            return count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "No se pudo detectar si la entrada de caseta corresponde a un pre-registro vigente.");
            return false;
        }
    }

    /// <summary>
    /// Busca entradas de caseta por número de caja (columna <c>nequipo</c>), más
    /// recientes primero. Un mismo número de caja se reutiliza con el tiempo, por
    /// eso puede haber más de un registro -- se limita a 5 para poder confirmar
    /// cuál es antes de reenviar un correo.
    /// </summary>
    public async Task<List<CasetaEntradaBusquedaPorCajaItem>> BuscarEntradasPorCajaAsync(string nequipo, CancellationToken ct)
    {
        string clean = (nequipo ?? "").Trim();
        if (string.IsNullOrWhiteSpace(clean))
            return [];

        using IDbConnection conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<CasetaEntradaBusquedaPorCajaItem>(new CommandDefinition(@"
SELECT
    casetaid AS CasetaId,
    nequipo AS Nequipo,
    planta AS Planta,
    tipomov AS TipoMov,
    tipoop AS TipoOp,
    cclienteid AS IdCliente,
    COALESCE((SELECT cnombre FROM clientes WHERE cclienteid = cidcte LIMIT 1), '') AS NombreCliente,
    farribo AS Farribo
FROM caseta
WHERE nequipo = @nequipo
ORDER BY farribo DESC
LIMIT 5", new { nequipo = clean }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>
    /// Reenvía el correo de una entrada de caseta ya guardada -- para el caso de
    /// "hice la entrada pero no me llegó el correo". Reconstruye el mismo registro
    /// y las mismas fotos que se habrían usado al guardar (misma consulta que usa
    /// la edición en <see cref="SubirFotosEntradaCasetaAsync"/>) y llama al mismo
    /// método de envío que el flujo normal.
    ///
    /// <paramref name="soloValidacion"/>=true manda el correo únicamente a
    /// <see cref="CorreoPruebasDesarrollo"/> para revisar cómo se ve antes de
    /// reenviarlo a los destinatarios reales; en false va a los destinatarios
    /// reales de <see cref="GetEntradaCasetaRecipients"/> sin importar el entorno
    /// (a diferencia del envío automático, aquí la intención ya es explícita).
    /// </summary>
    public async Task<(bool Success, string Mensaje)> ReenviarCorreoEntradaAsync(
        string casetaId, bool soloValidacion, string usuarioActual, CancellationToken ct)
    {
        // Por la ruta llega como "E-020485%2F26": se normaliza igual que en las fotos y la actualización.
        string cleanCasetaId = NormalizeCasetaId(casetaId);
        if (string.IsNullOrWhiteSpace(cleanCasetaId))
            return (false, "CasetaId es obligatorio.");

        using IDbConnection conn = _db.CreateConnection();

        CasetaEntradaRegistro? registro = await CargarRegistroEntradaAsync(conn, cleanCasetaId, usuarioActual, null, ct);

        if (registro == null)
            return (false, $"No existe ningún registro de caseta con folio '{cleanCasetaId}'.");

        var photoInfo = await LeerFotosEntradaCasetaAsync(conn, cleanCasetaId, incluirBase64: false);
        var fotos = photoInfo.Info
            .Select(photo => new CasetaEntradaFoto
            {
                Indice = photo.Indice,
                NombreArchivo = photo.NombreArchivo,
                TipoEtiqueta = "normal"
            })
            .ToList();

        IEnumerable<string> destinatarios = soloValidacion
            ? new[] { CorreoPruebasDesarrollo }
            : GetEntradaCasetaRecipients(registro);

        // La validación va solo a la casilla de pruebas: sin la copia a Calidad y Desarrollo que lleva el envío real.
        bool enviado = await EnviarCorreoEntradaCasetaAsync(registro, fotos, ct, destinatarios, incluirCopias: !soloValidacion, exigirEnlace: true);

        string resumenDestinatarios = string.Join(", ", destinatarios);
        return enviado
            ? (true, $"Correo reenviado a: {resumenDestinatarios}")
            : (false, "No se pudo reenviar el correo (revisa los logs del API).");
    }

    private static IEnumerable<string> GetEntradaCasetaRecipients(CasetaEntradaRegistro datos)
    {
        if (datos.Planta == "TRADE CTR")
        {
            return
            [
            "Notificaciones PMM <notificacionespmm@imexnotify.com>",
            "nldoperacion@grupopmm.com",
            "roberto.garza@grupopmm.com",
            "humberto.sanchez@grupopmm.com",
            "elancon@grupopmm.com",
            "guillermo.sauceda@grupopmm.com",
            "sergio.miranda@grupopmm.com",
            "manuel.ortegon@grupopmm.com"
            ];
        }

        var recipients = new List<string>();
        if (datos.TipoMov != "CONSOLIDADO/MENSAJERIA")
            recipients.Add("nldoperacion@grupopmm.com");

        recipients.Add("imexoperacion@grupopmm.com");
        return recipients;
    }

    private static void AddMailbox(InternetAddressList list, string email)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(email))
                list.Add(MailboxAddress.Parse(email));
        }
        catch
        {
        }
    }

    private static string BuildEntradaCasetaEmailBody(CasetaEntradaRegistro datos, string bodegaMail)
    {
        string aviso = datos.Actualizado
            ? $@"
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0'>
  <tr>
    <td style='background-color:#fffbe6;color:#d46b08;padding:12px 20px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;text-align:center;border-bottom:1px solid #ffe58f;'>
      ✏️ Este registro fue modificado por {H(datos.NombreUsuario)} el {datos.Fmodi:dd/MM/yyyy HH:mm}
    </td>
  </tr>
</table>"
            : "";

        string hsello;
        string dsello;
        if (datos.TipoMov == "CONSOLIDADO/MENSAJERIA")
        {
            hsello = "Guias";
            dsello = datos.Guias.ToString();
        }
        else if (datos.TipoMov is "COLOCACION/VACIO" or "DIRECTO")
        {
            hsello = "Pais Origen";
            dsello = $"{H(datos.IdPais)} - {H(datos.Pais)}";
        }
        else
        {
            hsello = "Sello";
            dsello = H(datos.Sellos);
        }

        string extraRefrigerada = "";
        if (!string.IsNullOrWhiteSpace(datos.Temperatura) || !string.IsNullOrWhiteSpace(datos.Combustible))
        {
            extraRefrigerada = $@"
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:20px;'>
  <tr>
    <td width='48%' style='background-color:#f0f9ff;border:1px solid #bae6fd;padding:12px 16px;vertical-align:top;'>
      <div style='font-family:Arial,sans-serif;font-size:11px;font-weight:bold;color:#0284c7;text-transform:uppercase;'>❄️ Temp. Refrigeración</div>
      <div style='font-family:Arial,sans-serif;font-size:15px;font-weight:bold;color:#0369a1;margin-top:4px;'>{H(datos.Temperatura)}</div>
    </td>
    <td width='4%'>&nbsp;</td>
    <td width='48%' style='background-color:#fffbe6;border:1px solid #ffe58f;padding:12px 16px;vertical-align:top;'>
      <div style='font-family:Arial,sans-serif;font-size:11px;font-weight:bold;color:#d46b08;text-transform:uppercase;'>⛽ Nivel Combustible</div>
      <div style='font-family:Arial,sans-serif;font-size:15px;font-weight:bold;color:#873800;margin-top:4px;'>{H(datos.Combustible)}</div>
    </td>
  </tr>
</table>";
        }

        string observacionesBox = string.IsNullOrWhiteSpace(datos.Observaciones)
            ? ""
            : $@"
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:20px;background-color:#f8fafc;border-left:5px solid #003d8c;border-top:1px solid #e2e8f0;border-right:1px solid #e2e8f0;border-bottom:1px solid #e2e8f0;'>
  <tr>
    <td style='padding:14px 18px;'>
      <div style='font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;color:#003d8c;margin-bottom:6px;letter-spacing:0.5px;'>Observaciones de Entrada</div>
      <div style='font-family:Arial,sans-serif;font-size:13px;color:#334155;line-height:1.5;'>{H(datos.Observaciones)}</div>
    </td>
  </tr>
</table>";

        return $@"<!DOCTYPE html PUBLIC '-//W3C//DTD XHTML 1.0 Transitional//EN' 'http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd'>
<html xmlns='http://www.w3.org/1999/xhtml'>
<head>
  <meta http-equiv='Content-Type' content='text/html; charset=utf-8' />
  <meta name='viewport' content='width=device-width, initial-scale=1.0' />
  <title>Entrada de Caseta</title>
</head>
<body style='margin:0;padding:0;background-color:#f4f6f9;font-family:Arial,Helvetica,sans-serif;'>
  <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='background-color:#f4f6f9;table-layout:fixed;'>
    <tr>
      <td align='center' style='padding:20px 10px;'>
        <!--[if mso]>
        <table align='center' width='650' cellpadding='0' cellspacing='0' border='0'>
        <tr>
        <td>
        <![endif]-->
        <table role='presentation' align='center' width='650' cellpadding='0' cellspacing='0' border='0' style='width:100%;max-width:650px;background-color:#ffffff;border:1px solid #dcdfe6;'>
          
          <!-- Top Accent Line (Lime Green PMM) -->
          <tr>
            <td style='background-color:#C4D600;height:6px;font-size:1px;line-height:1px;'>&nbsp;</td>
          </tr>
          
          <!-- Header Banner (Deep Blue PMM) -->
          <tr>
            <td style='background-color:#003d8c;padding:25px 30px;text-align:center;'>
              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0'>
                <tr>
                  <td align='center'>
                    <div style='font-family:Arial,sans-serif;font-size:11px;text-transform:uppercase;letter-spacing:2px;color:#C4D600;font-weight:bold;margin-bottom:6px;'>
                      PEDRO MARTÍNEZ MÉNDEZ / IMEX FORWARDING
                    </div>
                    <h1 style='font-family:Arial,sans-serif;font-size:22px;font-weight:bold;color:#ffffff;margin:0;padding:0;'>
                      ENTRADA DE CASETA
                    </h1>
                  </td>
                </tr>
              </table>
            </td>
          </tr>
          
          {aviso}

          <!-- Body Content -->
          <tr>
            <td style='padding:25px 30px;background-color:#ffffff;'>
              
              <!-- General Info Table -->
              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='border-collapse:collapse;margin-bottom:20px;'>
                <tr>
                  <td width='140' style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;border-top:1px solid #e2e8f0;'>Bodega</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;border-bottom:1px solid #e2e8f0;border-top:1px solid #e2e8f0;'>
                    <span style='background-color:#003d8c;color:#ffffff;font-size:12px;font-weight:bold;padding:4px 10px;display:inline-block;'>{H(bodegaMail)}</span>
                  </td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Fecha y Hora</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{datos.Fcrea:dd/MM/yyyy HH:mm}</td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Cliente</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'><strong style='color:#003d8c;'>{H(datos.IdCliente)} - {H(datos.NombreCliente)}</strong></td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Tipo Operación</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{H(datos.TipoOp)}</td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Recibido por</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{H(datos.NombreUsuario)}</td>
                </tr>
              </table>

              <!-- Section Title Bar -->
              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:25px;margin-bottom:10px;'>
                <tr>
                  <td style='background-color:#003d8c;padding:8px 14px;color:#ffffff;font-family:Arial,sans-serif;font-size:12px;font-weight:bold;text-transform:uppercase;letter-spacing:0.5px;border-left:5px solid #C4D600;'>
                    Detalles del Equipo y Movimiento
                  </td>
                </tr>
              </table>

              <!-- Movement Table -->
              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='border-collapse:collapse;border:1px solid #cbd5e1;'>
                <thead>
                  <tr style='background-color:#003d8c;color:#ffffff;'>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;border-right:1px solid #002d68;'>Caja / Equipo</th>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;border-right:1px solid #002d68;'>Tipo</th>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;border-right:1px solid #002d68;'>Placas</th>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;border-right:1px solid #002d68;'>{H(hsello)}</th>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;border-right:1px solid #002d68;'>Movimiento</th>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;'>Línea Transportista</th>
                  </tr>
                </thead>
                <tbody>
                  <tr style='background-color:#fcfcfc;'>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#003d8c;font-weight:bold;border-bottom:1px solid #e2e8f0;border-right:1px solid #e2e8f0;background-color:#f0f4f9;'>{H(datos.Nequipo)}</td>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;border-right:1px solid #e2e8f0;'>{H(datos.Tipouni)}</td>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;border-right:1px solid #e2e8f0;'>{H(datos.Placas)}</td>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;border-right:1px solid #e2e8f0;'>{dsello}</td>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;border-right:1px solid #e2e8f0;'>{H(datos.TipoMov)}</td>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{H(datos.Transporte)}</td>
                  </tr>
                </tbody>
              </table>

              {extraRefrigerada}
              {observacionesBox}

            </td>
          </tr>

          <!-- Footer (Deep Blue with Lime accent) -->
          <tr>
            <td style='background-color:#003d8c;padding:18px 30px;text-align:center;border-top:3px solid #C4D600;'>
              <p style='font-family:Arial,sans-serif;font-size:11px;color:#ffffff;line-height:1.5;margin:0;'>
                Este registro fue generado automáticamente desde la aplicación <strong>PMMóvil Caseta</strong>.<br />
                <span style='color:#C4D600;'>Pedro Martínez Méndez y Cía S.C. & Imex Forwarding Agency Inc.</span>
              </p>
            </td>
          </tr>

        </table>
        <!--[if mso]>
        </td>
        </tr>
        </table>
        <![endif]-->
      </td>
    </tr>
  </table>
</body>
</html>";
    }

    /// <summary>
    /// Plantilla de correo para un registro de Caseta con
    /// <c>TipoRegistro = TRACTOCAMION</c> (camión sin caja, ver
    /// [[registro-tractocamiones-tiporegistro]]). Reemplaza la sección
    /// "Caja/Cliente" -- que no aplica-- por los datos propios del
    /// tractocamión (unidad, línea de camión, operador, placas), en vez de
    /// reusar <see cref="BuildEntradaCasetaEmailBody"/> con campos vacíos.
    /// </summary>
    private static string BuildEntradaCasetaTractocamionEmailBody(CasetaEntradaRegistro datos, string bodegaMail)
    {
        string aviso = datos.Actualizado
            ? $@"
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0'>
  <tr>
    <td style='background-color:#fffbe6;color:#d46b08;padding:12px 20px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;text-align:center;border-bottom:1px solid #ffe58f;'>
      ✏️ Este registro fue modificado por {H(datos.NombreUsuario)} el {datos.Fmodi:dd/MM/yyyy HH:mm}
    </td>
  </tr>
</table>"
            : $@"
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0'>
  <tr>
    <td style='background-color:#eff6ff;color:#1d4ed8;padding:12px 20px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;text-align:center;border-bottom:1px solid #bfdbfe;'>
      🚚 Registro de tractocamión sin caja — llega solo a recoger una caja ya dejada
    </td>
  </tr>
</table>";

        string observacionesBox = string.IsNullOrWhiteSpace(datos.Observaciones)
            ? ""
            : $@"
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:20px;background-color:#f8fafc;border-left:5px solid #003d8c;border-top:1px solid #e2e8f0;border-right:1px solid #e2e8f0;border-bottom:1px solid #e2e8f0;'>
  <tr>
    <td style='padding:14px 18px;'>
      <div style='font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;color:#003d8c;margin-bottom:6px;letter-spacing:0.5px;'>Observaciones de Entrada</div>
      <div style='font-family:Arial,sans-serif;font-size:13px;color:#334155;line-height:1.5;'>{H(datos.Observaciones)}</div>
    </td>
  </tr>
</table>";

        return $@"<!DOCTYPE html PUBLIC '-//W3C//DTD XHTML 1.0 Transitional//EN' 'http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd'>
<html xmlns='http://www.w3.org/1999/xhtml'>
<head>
  <meta http-equiv='Content-Type' content='text/html; charset=utf-8' />
  <meta name='viewport' content='width=device-width, initial-scale=1.0' />
  <title>Entrada de Caseta - Tractocamión</title>
</head>
<body style='margin:0;padding:0;background-color:#f4f6f9;font-family:Arial,Helvetica,sans-serif;'>
  <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='background-color:#f4f6f9;table-layout:fixed;'>
    <tr>
      <td align='center' style='padding:20px 10px;'>
        <!--[if mso]>
        <table align='center' width='650' cellpadding='0' cellspacing='0' border='0'>
        <tr>
        <td>
        <![endif]-->
        <table role='presentation' align='center' width='650' cellpadding='0' cellspacing='0' border='0' style='width:100%;max-width:650px;background-color:#ffffff;border:1px solid #dcdfe6;'>

          <!-- Top Accent Line (Lime Green PMM) -->
          <tr>
            <td style='background-color:#C4D600;height:6px;font-size:1px;line-height:1px;'>&nbsp;</td>
          </tr>

          <!-- Header Banner (Deep Blue PMM) -->
          <tr>
            <td style='background-color:#003d8c;padding:25px 30px;text-align:center;'>
              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0'>
                <tr>
                  <td align='center'>
                    <div style='font-family:Arial,sans-serif;font-size:11px;text-transform:uppercase;letter-spacing:2px;color:#C4D600;font-weight:bold;margin-bottom:6px;'>
                      PEDRO MARTÍNEZ MÉNDEZ / IMEX FORWARDING
                    </div>
                    <h1 style='font-family:Arial,sans-serif;font-size:22px;font-weight:bold;color:#ffffff;margin:0;padding:0;'>
                      ENTRADA DE CASETA
                    </h1>
                    <div style='display:inline-block;margin-top:10px;background-color:rgba(255,255,255,0.14);border:1px solid #93c5fd;color:#dbeafe;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;letter-spacing:0.5px;text-transform:uppercase;padding:5px 14px;border-radius:20px;'>
                      🚚 Tractocamión &middot; Sin caja
                    </div>
                  </td>
                </tr>
              </table>
            </td>
          </tr>

          {aviso}

          <!-- Body Content -->
          <tr>
            <td style='padding:25px 30px;background-color:#ffffff;'>

              <!-- General Info Table (sin fila de Cliente: no aplica a un tractocamión) -->
              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='border-collapse:collapse;margin-bottom:20px;'>
                <tr>
                  <td width='140' style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;border-top:1px solid #e2e8f0;'>Bodega</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;border-bottom:1px solid #e2e8f0;border-top:1px solid #e2e8f0;'>
                    <span style='background-color:#003d8c;color:#ffffff;font-size:12px;font-weight:bold;padding:4px 10px;display:inline-block;'>{H(bodegaMail)}</span>
                  </td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Fecha y Hora</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{datos.Fcrea:dd/MM/yyyy HH:mm}</td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Tipo Operación</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{H(datos.TipoOp)}</td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Recibido por</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{H(datos.NombreUsuario)}</td>
                </tr>
              </table>

              <!-- Section Title Bar -->
              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:25px;margin-bottom:10px;'>
                <tr>
                  <td style='background-color:#003d8c;padding:8px 14px;color:#ffffff;font-family:Arial,sans-serif;font-size:12px;font-weight:bold;text-transform:uppercase;letter-spacing:0.5px;border-left:5px solid #C4D600;'>
                    Detalles del Tractocamión
                  </td>
                </tr>
              </table>

              <!-- Movement Table (sin columnas de Tipo de caja ni Sello: no aplica) -->
              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='border-collapse:collapse;border:1px solid #cbd5e1;'>
                <thead>
                  <tr style='background-color:#003d8c;color:#ffffff;'>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;border-right:1px solid #002d68;'>No. Tractocamión</th>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;border-right:1px solid #002d68;'>Placas</th>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;border-right:1px solid #002d68;'>Operador</th>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;border-right:1px solid #002d68;'>Movimiento</th>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;'>Línea de Camión</th>
                  </tr>
                </thead>
                <tbody>
                  <tr style='background-color:#fcfcfc;'>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#1d4ed8;font-weight:bold;border-bottom:1px solid #e2e8f0;border-right:1px solid #e2e8f0;background-color:#eff6ff;'>{H(datos.NumUnidad)}</td>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;border-right:1px solid #e2e8f0;'>{H(datos.Placas)}</td>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;border-right:1px solid #e2e8f0;'>{H(datos.Operador)}</td>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;border-right:1px solid #e2e8f0;'>{H(datos.TipoMov)}</td>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{H(datos.Transpor)}</td>
                  </tr>
                </tbody>
              </table>

              {observacionesBox}

            </td>
          </tr>

          <!-- Footer (Deep Blue with Lime accent) -->
          <tr>
            <td style='background-color:#003d8c;padding:18px 30px;text-align:center;border-top:3px solid #C4D600;'>
              <p style='font-family:Arial,sans-serif;font-size:11px;color:#ffffff;line-height:1.5;margin:0;'>
                Este registro fue generado automáticamente desde la aplicación <strong>PMMóvil Caseta</strong>.<br />
                <span style='color:#C4D600;'>Pedro Martínez Méndez y Cía S.C. & Imex Forwarding Agency Inc.</span>
              </p>
            </td>
          </tr>

        </table>
        <!--[if mso]>
        </td>
        </tr>
        </table>
        <![endif]-->
      </td>
    </tr>
  </table>
</body>
</html>";
    }

    private static string BuildSalidaCasetaEmailBody(
        string relno,
        string caja,
        string transfer,
        string operador,
        string tractor,
        string gafete,
        string fecha,
        string observaciones,
        List<IGrouping<string, TraficoNotiRow>> grupos)
    {
        var sbGrupos = new StringBuilder();
        foreach (var grupo in grupos)
        {
            string nombreCliente = grupo.First().Cnombre ?? grupo.Key;
            sbGrupos.Append($@"
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:15px;border-collapse:collapse;border:1px solid #cbd5e1;'>
  <thead>
    <tr style='background-color:#003d8c;color:#ffffff;'>
      <th colspan='2' style='padding:8px 12px;font-family:Arial,sans-serif;font-size:12px;font-weight:bold;text-align:left;color:#C4D600;border-bottom:2px solid #C4D600;'>
        {H(nombreCliente)}
      </th>
    </tr>
    <tr style='background-color:#f0f4f9;color:#003d8c;'>
      <th style='padding:8px 12px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;border-bottom:1px solid #cbd5e1;border-right:1px solid #cbd5e1;width:50%;'>Referencia</th>
      <th style='padding:8px 12px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;border-bottom:1px solid #cbd5e1;width:50%;'>Pedimento</th>
    </tr>
  </thead>
  <tbody>");

            foreach (var t in grupo)
            {
                sbGrupos.Append($@"
    <tr>
      <td style='padding:8px 12px;font-family:Arial,sans-serif;font-size:13px;color:#003d8c;border-bottom:1px solid #e2e8f0;border-right:1px solid #e2e8f0;font-weight:bold;'>{H(t.Creferencia)}</td>
      <td style='padding:8px 12px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{H(t.Pedimento)}</td>
    </tr>");
            }

            sbGrupos.Append(@"
  </tbody>
</table>");
        }

        string observacionesBox = string.IsNullOrWhiteSpace(observaciones)
            ? ""
            : $@"
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:20px;background-color:#f8fafc;border-left:5px solid #003d8c;border-top:1px solid #e2e8f0;border-right:1px solid #e2e8f0;border-bottom:1px solid #e2e8f0;'>
  <tr>
    <td style='padding:14px 18px;'>
      <div style='font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;color:#003d8c;margin-bottom:6px;letter-spacing:0.5px;'>Observaciones de Salida</div>
      <div style='font-family:Arial,sans-serif;font-size:13px;color:#334155;line-height:1.5;'>{H(observaciones)}</div>
    </td>
  </tr>
</table>";

        return $@"<!DOCTYPE html PUBLIC '-//W3C//DTD XHTML 1.0 Transitional//EN' 'http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd'>
<html xmlns='http://www.w3.org/1999/xhtml'>
<head>
  <meta http-equiv='Content-Type' content='text/html; charset=utf-8' />
  <meta name='viewport' content='width=device-width, initial-scale=1.0' />
  <title>Salida de Caseta</title>
</head>
<body style='margin:0;padding:0;background-color:#f4f6f9;font-family:Arial,Helvetica,sans-serif;'>
  <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='background-color:#f4f6f9;table-layout:fixed;'>
    <tr>
      <td align='center' style='padding:20px 10px;'>
        <!--[if mso]>
        <table align='center' width='650' cellpadding='0' cellspacing='0' border='0'>
        <tr>
        <td>
        <![endif]-->
        <table role='presentation' align='center' width='650' cellpadding='0' cellspacing='0' border='0' style='width:100%;max-width:650px;background-color:#ffffff;border:1px solid #dcdfe6;'>
          
          <!-- Top Accent Line (Lime Green PMM) -->
          <tr>
            <td style='background-color:#C4D600;height:6px;font-size:1px;line-height:1px;'>&nbsp;</td>
          </tr>
          
          <!-- Header Banner (Deep Blue PMM) -->
          <tr>
            <td style='background-color:#003d8c;padding:25px 30px;text-align:center;'>
              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0'>
                <tr>
                  <td align='center'>
                    <div style='font-family:Arial,sans-serif;font-size:11px;text-transform:uppercase;letter-spacing:2px;color:#C4D600;font-weight:bold;margin-bottom:6px;'>
                      PEDRO MARTÍNEZ MÉNDEZ / IMEX FORWARDING
                    </div>
                    <h1 style='font-family:Arial,sans-serif;font-size:22px;font-weight:bold;color:#ffffff;margin:0;padding:0;'>
                      NOTIFICACIÓN DE SALIDA DE CASETA
                    </h1>
                  </td>
                </tr>
              </table>
            </td>
          </tr>

          <!-- Body Content -->
          <tr>
            <td style='padding:25px 30px;background-color:#ffffff;'>
              
              <!-- General Info Table -->
              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='border-collapse:collapse;margin-bottom:20px;'>
                <tr>
                  <td width='140' style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;border-top:1px solid #e2e8f0;'>Relación</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;border-bottom:1px solid #e2e8f0;border-top:1px solid #e2e8f0;'>
                    <span style='background-color:#003d8c;color:#ffffff;font-size:12px;font-weight:bold;padding:4px 10px;display:inline-block;'>{H(relno)}</span>
                  </td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Caja / Equipo</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'><strong style='color:#003d8c;'>{H(caja)}</strong></td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Transfer</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{H(transfer)}</td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Operador</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{H(operador)} (Gafete: {H(gafete)})</td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Tractor</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{H(tractor)}</td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Fecha y Hora</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{H(fecha)}</td>
                </tr>
              </table>

              <!-- Section Title Bar -->
              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:25px;margin-bottom:10px;'>
                <tr>
                  <td style='background-color:#003d8c;padding:8px 14px;color:#ffffff;font-family:Arial,sans-serif;font-size:12px;font-weight:bold;text-transform:uppercase;letter-spacing:0.5px;border-left:5px solid #C4D600;'>
                    Clientes y Referencias de la Salida
                  </td>
                </tr>
              </table>

              {sbGrupos}
              {observacionesBox}

            </td>
          </tr>

          <!-- Footer (Deep Blue with Lime accent) -->
          <tr>
            <td style='background-color:#003d8c;padding:18px 30px;text-align:center;border-top:3px solid #C4D600;'>
              <p style='font-family:Arial,sans-serif;font-size:11px;color:#ffffff;line-height:1.5;margin:0;'>
                Este registro fue generado automáticamente desde la aplicación <strong>PMMóvil Caseta</strong>.<br />
                <span style='color:#C4D600;'>Pedro Martínez Méndez y Cía S.C. & Imex Forwarding Agency Inc.</span>
              </p>
            </td>
          </tr>

        </table>
        <!--[if mso]>
        </td>
        </tr>
        </table>
        <![endif]-->
      </td>
    </tr>
  </table>
</body>
</html>";
    }

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
}

internal class CasetaDetalleDbRow
{
    public string Relno { get; set; } = string.Empty;
    public string EstatusCarga { get; set; } = string.Empty;
    public string STATUS { get; set; } = string.Empty;
    public string Cnombre { get; set; } = string.Empty;
    public string Op_transfer { get; set; } = string.Empty;
    public string Caja { get; set; } = string.Empty;
    public string Sellos { get; set; } = string.Empty;
    public string Sellos2 { get; set; } = string.Empty;
    public string Sellos3 { get; set; } = string.Empty;
    public string Sellos4 { get; set; } = string.Empty;
    public string Num_tractor { get; set; } = string.Empty;
    public string Gafete_op { get; set; } = string.Empty;
    public string Usuedit { get; set; } = string.Empty;
}

internal sealed record SalidaNotificacionEnviada(string Cliente, string ClienteId, List<string> Reales, List<string> EnviadoA, bool Exito);

internal sealed class RelacionSalidaRow
{
    public string Estatus { get; set; } = string.Empty;
    public DateTime? SalidaCaseta { get; set; }
    public string Usuario { get; set; } = string.Empty;
}

internal class TraficoNotiRow
{
    public string Creferencia { get; set; } = string.Empty;
    public string Cclienteidtraf { get; set; } = string.Empty;
    public string Cnombre { get; set; } = string.Empty;
    public string Cidmanejo { get; set; } = string.Empty;
    public string Pedimento { get; set; } = string.Empty;
}

internal class CasetaEntradaRegistro
{
    public int IdCaseta { get; set; }
    public string CasetaId { get; set; } = string.Empty;
    public string IdBodega { get; set; } = string.Empty;
    public string Bodega { get; set; } = string.Empty;
    public string TipoOp { get; set; } = string.Empty;
    public string TipoMov { get; set; } = string.Empty;
    public string TipoRegistro { get; set; } = "CAJA";
    public string IdUnidad { get; set; } = string.Empty;
    public string Tipouni { get; set; } = string.Empty;
    public string NumUnidad { get; set; } = string.Empty;
    public string IdTransporteTractor { get; set; } = string.Empty;
    public string Transpor { get; set; } = string.Empty;
    public string Nequipo { get; set; } = string.Empty;
    public string IdTransporteCaja { get; set; } = string.Empty;
    public string Transporte { get; set; } = string.Empty;
    public string Placas { get; set; } = string.Empty;
    public string Sellos { get; set; } = string.Empty;
    public DateTime Farribo { get; set; }
    public string Usucrea { get; set; } = string.Empty;
    public string NombreUsuario { get; set; } = string.Empty;
    public DateTime Fcrea { get; set; }
    public string Usumodi { get; set; } = string.Empty;
    public DateTime Fmodi { get; set; }
    public string IdCliente { get; set; } = string.Empty;
    public string NombreCliente { get; set; } = string.Empty;
    public string Observaciones { get; set; } = string.Empty;
    public string Pedimento { get; set; } = string.Empty;
    public string Temperatura { get; set; } = string.Empty;
    public int Guias { get; set; }
    public string IdPais { get; set; } = string.Empty;
    public string Pais { get; set; } = string.Empty;
    public string Operador { get; set; } = string.Empty;
    public string Combustible { get; set; } = string.Empty;
    public string Planta { get; set; } = string.Empty;
    public bool RequiereImpo { get; set; }
    public bool Actualizado { get; set; }
    public bool EsPreRegistro { get; set; }
}

internal class CasetaEntradaFoto
{
    public byte[] BytesFoto { get; set; } = [];
    public string Base64 { get; set; } = string.Empty;
    public string NombreArchivo { get; set; } = string.Empty;
    public string TipoEtiqueta { get; set; } = string.Empty;
    public int Indice { get; set; }
}
