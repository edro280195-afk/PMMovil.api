using System.Data;

using Dapper;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public class SalidaCasetaService
{
    private readonly BaseRepository _db;
    private readonly ILogger<SalidaCasetaService> _logger;

    public SalidaCasetaService(BaseRepository db, ILogger<SalidaCasetaService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<SalidaCajaItem>> GetPendientesAsync(string planta)
    {
        // LEFT JOIN (antes INNER JOIN): los camiones que vienen de un
        // pre-registro entran a `caseta` con cclienteid vacio (no aplica un
        // cliente para un camion sin caja). Con INNER JOIN esas filas
        // desaparecian por completo de "Dar salida" -- solo se podia cerrar
        // su salida entrando directo a FoxPro, que consulta `caseta` sin este
        // join. COALESCE evita que NombreCliente llegue null al DTO.
        const string sql = @"
            SELECT
                c.casetaid  AS CasetaId,
                c.nequipo   AS NumCaja,
                c.transpore AS LineaCaja,
                c.nunidad   AS NumCamion,
                c.placas    AS Placas,
                COALESCE(cl.cnombre, '') AS NombreCliente,
                c.tipouni   AS TipoUnidad,
                c.tipomov   AS TipoMovimiento,
                CASE
                    WHEN c.tipoop = 'IMPORTACION' THEN 'IMPO'
                    WHEN c.tipoop = 'EXPORTACION' THEN 'EXPO'
                    ELSE c.tipoop
                END         AS TipoOp,
                c.planta    AS Planta,
                CASE WHEN YEAR(c.farribo) >= 2000 THEN c.farribo END AS FechaArribo,
                DATEDIFF(NOW(), c.farribo) AS DiasEnPatio
            FROM caseta c
            LEFT JOIN clientes cl ON c.cclienteid = cl.cidcte
            WHERE (c.fsalida IS NULL OR c.fsalida < '2000-01-01')
              AND c.estatus NOT IN ('CANCELADO', 'CANCELADA')
              AND (@planta = '' OR c.planta = @planta)
            ORDER BY c.farribo DESC";

        using IDbConnection conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<SalidaCajaItem>(sql, new { planta });
        return rows.ToList();
    }

    public async Task<string> RevisarRelacionAsync(string casetaId)
    {
        const string sql = @"
            SELECT relacion
            FROM caseta
            WHERE casetaid = @casetaId AND LEFT(relacion, 2) = '01'
            LIMIT 1";

        using IDbConnection conn = _db.CreateConnection();
        var relacion = await conn.ExecuteScalarAsync<string?>(sql, new { casetaId });
        return relacion ?? string.Empty;
    }

    /// <summary>
    /// Salida MANUAL de una caja sin relación. Candado estructural (no una regla
    /// de negocio): una caja ligada a una relación jamás sale por aquí, porque
    /// esa relación seguiría AUTORIZADA y se podría sacar dos veces, sin aviso al
    /// cliente. Antes este candado vivía solo en la app (aviso de 5 s).
    /// Deja <c>caseta.relacion = 'NO_RELACION'</c>, el mismo marcador que ya usaba
    /// el sistema anterior (ver ImexService) para distinguir salidas manuales.
    /// </summary>
    public async Task<RegistrarSalidaCajaResult> RegistrarSalidaAsync(
        string casetaId, string usuario, string observaciones, string planta = "")
    {
        using IDbConnection conn = _db.CreateConnection();
        conn.Open();
        using IDbTransaction tx = conn.BeginTransaction();
        try
        {
            var actual = await conn.QueryFirstOrDefaultAsync<CajaSalidaRow>(@"
                SELECT CASE WHEN YEAR(fsalida) >= 2000 THEN fsalida END AS Fsalida, TRIM(IFNULL(relacion, '')) AS Relacion,
                       IFNULL(estatus, '') AS Estatus, IFNULL(planta, '') AS Planta
                FROM caseta WHERE casetaid = @casetaId LIMIT 1",
                new { casetaId }, tx);

            if (actual is null)
            {
                tx.Rollback();
                return new RegistrarSalidaCajaResult { Success = false, Message = "No se encontró el registro de la caja." };
            }

            if (actual.Fsalida.HasValue && actual.Fsalida.Value > new DateTime(2000, 1, 1))
            {
                tx.Rollback();
                return new RegistrarSalidaCajaResult
                {
                    Success = false,
                    Message = "Esta salida ya fue registrada anteriormente."
                };
            }

            if (actual.Estatus.StartsWith("CANCELAD", StringComparison.OrdinalIgnoreCase))
            {
                tx.Rollback();
                return new RegistrarSalidaCajaResult { Success = false, Message = "La caja está cancelada." };
            }

            if (!string.IsNullOrWhiteSpace(planta) && !string.Equals(actual.Planta, planta, StringComparison.OrdinalIgnoreCase))
            {
                tx.Rollback();
                return new RegistrarSalidaCajaResult { Success = false, Message = "La caja pertenece a otra planta." };
            }

            if (TieneRelacion(actual.Relacion))
            {
                string? estatusRelacion = await ObtenerEstatusRelacionAsync(conn, actual.Relacion, tx);
                tx.Rollback();
                string mensaje = estatusRelacion switch
                {
                    "SALIDA" => $"La relación {actual.Relacion} ya salió. Usa «Cerrar registro» para quitar la caja del patio.",
                    null => $"La caja tiene la relación {actual.Relacion}, pero no se encontró. Avisa a Sistemas.",
                    _ => $"La caja pertenece a la relación {actual.Relacion}. La salida se da con la relación, no de forma manual."
                };
                return new RegistrarSalidaCajaResult { Success = false, Message = mensaje };
            }

            var consecutivo = await conn.ExecuteScalarAsync<int>(
    "SELECT numero FROM consecutivo WHERE formulario = 'CASETAS' AND prefijo = 'S'",
    transaction: tx);
            consecutivo++;
            await conn.ExecuteAsync(
                "UPDATE consecutivo SET numero = @numero WHERE formulario = 'CASETAS' AND prefijo = 'S'",
                new { numero = consecutivo }, tx);

            var salida = DateTime.Now;
            var rows = await conn.ExecuteAsync(@"
                UPDATE caseta
                SET fsalida    = @salida,
                    estatus    = 'SALIDA',
                    ususalida  = @usuario,
                    usumodi    = @usuario,
                    fmodi      = @salida,
                    obs_salida = @observaciones,
                    relacion   = 'NO_RELACION'
                WHERE casetaid = @casetaId",
                new { salida, usuario, observaciones, casetaId }, tx);

            if (rows > 0)
            {
                tx.Commit();
                return new RegistrarSalidaCajaResult
                {
                    Success = true,
                    Message = "Salida registrada correctamente."
                };
            }

            tx.Rollback();
            return new RegistrarSalidaCajaResult
            {
                Success = false,
                Message = "No se encontró el registro de la caja."
            };
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ── Pantalla unificada ─────────────────────────────────────────────────

    private const string SinRelacionMarcador = "NO_RELACION";

    /// <summary>Hay una relación real (no vacío ni el marcador de "sin relación").</summary>
    private static bool TieneRelacion(string? relacion)
        => !string.IsNullOrWhiteSpace(relacion)
           && !string.Equals(relacion.Trim(), SinRelacionMarcador, StringComparison.OrdinalIgnoreCase);

    /// <summary>Misma regla que CasetaService.ResolverTabla: 'C' en la 3ª posición = relacion_imex.</summary>
    private static string TablaRelacion(string relno)
        => relno.Length > 2 && relno[2] == 'C' ? "relacion_imex" : "relacion";

    private static bool EsAnioValido(DateTime? fecha) => fecha.HasValue && fecha.Value.Year >= 2000;

    private async Task<string?> ObtenerEstatusRelacionAsync(IDbConnection conn, string relno, IDbTransaction? tx = null)
    {
        string tabla = TablaRelacion(relno);
        var fila = await conn.QueryFirstOrDefaultAsync<EstatusRelacionRow>(
            $"SELECT IFNULL(estatus_carga, '') AS EstatusCarga, CASE WHEN YEAR(salida_caseta) >= 2000 THEN salida_caseta END AS SalidaCaseta FROM {tabla} WHERE relno = @relno LIMIT 1",
            new { relno }, tx);
        if (fila is null) return null;
        return EsAnioValido(fila.SalidaCaseta) ? "SALIDA" : fila.EstatusCarga;
    }

    /// <summary>
    /// Todo lo que sigue en el patio, ya clasificado por carril. Una relación con
    /// varias cajas es UN solo renglón (confirmarla cierra todas sus cajas).
    /// </summary>
    public async Task<SalidaPatioResult> GetPatioAsync(string planta)
    {
        using IDbConnection conn = _db.CreateConnection();

        var cajas = (await conn.QueryAsync<PatioCajaRow>(@"
            SELECT
                c.casetaid  AS CasetaId,
                IFNULL(c.nequipo, '')   AS NumCaja,
                IFNULL(c.transpore, '') AS LineaCaja,
                IFNULL(c.nunidad, '')   AS NumCamion,
                IFNULL(c.placas, '')    AS Placas,
                COALESCE(cl.cnombre, '') AS NombreCliente,
                IFNULL(c.tipouni, '')   AS TipoUnidad,
                IFNULL(c.tipomov, '')   AS TipoMovimiento,
                CASE
                    WHEN c.tipoop = 'IMPORTACION' THEN 'IMPO'
                    WHEN c.tipoop = 'EXPORTACION' THEN 'EXPO'
                    ELSE IFNULL(c.tipoop, '')
                END         AS TipoOp,
                IFNULL(c.planta, '')    AS Planta,
                CASE WHEN YEAR(c.farribo) >= 2000 THEN c.farribo END AS FechaArribo,
                IFNULL(c.tiporegistro, 'CAJA') AS TipoRegistro,
                IFNULL(c.estatus, '')   AS EstatusCaseta,
                TRIM(IFNULL(c.relacion, '')) AS Relacion
            FROM caseta c
            LEFT JOIN clientes cl ON c.cclienteid = cl.cidcte
            WHERE (c.fsalida IS NULL OR c.fsalida < '2000-01-01')
              AND c.estatus NOT IN ('CANCELADO', 'CANCELADA')
              AND (@planta = '' OR c.planta = @planta)",
            new { planta })).ToList();

        var relnos = cajas.Select(c => c.Relacion).Where(TieneRelacion).Distinct().ToList();

        var relaciones = new Dictionary<string, PatioRelacionRow>(StringComparer.OrdinalIgnoreCase);
        var clientesPorRelacion = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var bultosPorRelacion = new Dictionary<string, (int Cargados, int Total)>(StringComparer.OrdinalIgnoreCase);

        if (relnos.Count > 0)
        {
            const string columnasRelacion = @"
                r.relno AS Relno, IFNULL(r.estatus_carga, '') AS EstatusCarga,
                CASE WHEN YEAR(r.autoriza_carga) >= 2000 THEN r.autoriza_carga END AS AutorizaCarga, IFNULL(r.usuautoriza, '') AS UsuAutoriza,
                IFNULL(r.caja, '') AS CajaRelacion, IFNULL(t.cnombre, '') AS Transfer,
                IFNULL(r.op_transfer, '') AS Operador, IFNULL(r.num_tractor, '') AS Tractor,
                IFNULL(r.gafete_op, '') AS Gafete,
                TRIM(CONCAT_WS(' ', NULLIF(TRIM(r.sellos), ''), NULLIF(TRIM(r.sellos2), ''),
                                    NULLIF(TRIM(r.sellos3), ''), NULLIF(TRIM(r.sellos4), ''))) AS Sellos,
                IFNULL(r.usuedit, '') AS UsuEdit, IFNULL(r.usucargamcia, '') AS UsuCarga,
                CASE WHEN YEAR(r.fcruce) >= 2000 THEN r.fcruce END AS FCruce,
                CASE WHEN YEAR(r.salida_caseta) >= 2000 THEN r.salida_caseta END AS SalidaCaseta,
                CASE WHEN YEAR(r.inicio_carga) >= 2000 THEN r.inicio_carga END AS InicioCarga";

            var filas = await conn.QueryAsync<PatioRelacionRow>($@"
                SELECT {columnasRelacion} FROM relacion r
                LEFT JOIN tranfers t ON r.cidtransfer = t.cidtransfer
                WHERE r.relno IN @relnos
                UNION ALL
                SELECT {columnasRelacion} FROM relacion_imex r
                LEFT JOIN tranfers t ON r.cidtransfer = t.cidtransfer
                WHERE r.relno IN @relnos", new { relnos });
            foreach (var f in filas) relaciones[f.Relno] = f;

            var relnosI = relnos.Where(r => TablaRelacion(r) == "relacion").ToList();
            var relnosC = relnos.Where(r => TablaRelacion(r) == "relacion_imex").ToList();

            // Mismo camino de joins que usa hoy cada variante (ver GetPendientesAsync / SalidaService).
            if (relnosI.Count > 0)
            {
                foreach (var f in await conn.QueryAsync<ClienteRelacionRow>(@"
                    SELECT t2.nrelacion AS Relno, cl.cnombre AS Cliente
                    FROM traf2 t2
                    LEFT JOIN traf t ON t2.creferencia = t.creferencia
                    LEFT JOIN clientes cl ON t.cclienteidtraf = cl.cidcte
                    WHERE t2.nrelacion IN @relnos AND cl.cnombre IS NOT NULL
                    GROUP BY t2.nrelacion, cl.cnombre", new { relnos = relnosI }))
                    AgregarCliente(clientesPorRelacion, f);

                foreach (var f in await conn.QueryAsync<BultosRelacionRow>(@"
                    SELECT traf2.nrelacion AS Relno,
                           COUNT(DISTINCT CASE WHEN e.estatus <> 'ELIMINAR' THEN e.id_etiqueta_mcia END) AS Total,
                           COUNT(DISTINCT CASE WHEN e.estatus IN ('CARGADO', 'SALIDA') THEN e.id_etiqueta_mcia END) AS Cargados
                    FROM traf2
                    LEFT JOIN agrupa ON traf2.creferencia = agrupa.creferencia
                    LEFT JOIN traf c ON c.creferencia = agrupa.cref_agrupada
                    LEFT JOIN etiqueta_mcia e ON c.creferencia = e.creferencia AND e.textoetiqueta <> ''
                    WHERE traf2.nrelacion IN @relnos
                    GROUP BY traf2.nrelacion", new { relnos = relnosI }))
                    bultosPorRelacion[f.Relno] = (f.Cargados, f.Total);
            }

            if (relnosC.Count > 0)
            {
                foreach (var f in await conn.QueryAsync<ClienteRelacionRow>(@"
                    SELECT t2.nrelacion AS Relno, cl.cnombre AS Cliente
                    FROM traf2 t2
                    LEFT JOIN agrupa ag ON t2.creferencia = ag.cref_agrupada
                    LEFT JOIN traf t ON ag.cref_agrupada = t.creferencia
                    LEFT JOIN clientes cl ON t.cclienteidtraf = cl.cidcte
                    WHERE t2.nrelacion IN @relnos AND cl.cnombre IS NOT NULL
                    GROUP BY t2.nrelacion, cl.cnombre", new { relnos = relnosC }))
                    AgregarCliente(clientesPorRelacion, f);

                foreach (var f in await conn.QueryAsync<BultosRelacionRow>(@"
                    SELECT traf2.nrelacion AS Relno,
                           COUNT(DISTINCT CASE WHEN e.estatus <> 'ELIMINAR' THEN e.id_etiqueta_mcia END) AS Total,
                           COUNT(DISTINCT CASE WHEN e.estatus IN ('CARGADO', 'SALIDA') THEN e.id_etiqueta_mcia END) AS Cargados
                    FROM traf2
                    LEFT JOIN agrupa ON traf2.creferencia = agrupa.cref_agrupada
                    LEFT JOIN traf c ON c.creferencia = agrupa.cref_agrupada
                    LEFT JOIN etiqueta_mcia e ON c.creferencia = e.creferencia AND e.textoetiqueta <> ''
                    WHERE traf2.nrelacion IN @relnos
                    GROUP BY traf2.nrelacion", new { relnos = relnosC }))
                    bultosPorRelacion[f.Relno] = (f.Cargados, f.Total);
            }
        }

        // Fotos de llegada (solo cajas sin relación: ayudan a reconocerlas en el patio).
        var idsManuales = cajas.Where(c => !TieneRelacion(c.Relacion)).Select(c => c.CasetaId).ToList();
        var fotosPorCaja = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        if (idsManuales.Count > 0)
        {
            foreach (var f in await conn.QueryAsync<FotoCajaRow>(@"
                SELECT creferencia AS CasetaId, nlineno AS Indice
                FROM fotos
                WHERE ntipodocto = 106 AND creferencia IN @ids
                ORDER BY creferencia, nlineno", new { ids = idsManuales }))
            {
                if (!fotosPorCaja.TryGetValue(f.CasetaId, out var lista))
                    fotosPorCaja[f.CasetaId] = lista = new List<int>();
                if (lista.Count < 6 && !lista.Contains(f.Indice)) lista.Add(f.Indice);
            }
        }

        DateTime ahora = DateTime.Now;
        var items = new List<SalidaPatioItem>();

        // Clave de agrupación: la relación, o el casetaId cuando no tiene (cada caja manual es su propio renglón).
        foreach (var grupo in cajas.GroupBy(c => TieneRelacion(c.Relacion) ? "R|" + c.Relacion : "C|" + c.CasetaId))
        {
            var primera = grupo.First();
            DateTime? arribo = grupo.Where(c => EsAnioValido(c.FechaArribo)).Select(c => c.FechaArribo).Min();
            var item = new SalidaPatioItem
            {
                TipoOp = primera.TipoOp,
                TipoMovimiento = primera.TipoMovimiento,
                EsTractocamion = string.Equals(primera.TipoRegistro, "TRACTOCAMION", StringComparison.OrdinalIgnoreCase),
                Planta = primera.Planta,
                FechaArribo = arribo,
                Cajas = grupo.Select(c => new SalidaPatioCaja
                {
                    CasetaId = c.CasetaId,
                    NumCaja = c.NumCaja,
                    LineaCaja = c.LineaCaja,
                    NumCamion = c.NumCamion,
                    Placas = c.Placas,
                    TipoUnidad = c.TipoUnidad,
                    EstatusCaseta = c.EstatusCaseta,
                    FotosEntrada = fotosPorCaja.TryGetValue(c.CasetaId, out var fs) ? fs : new List<int>()
                }).OrderBy(c => c.NumCaja, StringComparer.OrdinalIgnoreCase).ToList()
            };
            if (arribo.HasValue)
            {
                TimeSpan enPatio = ahora - arribo.Value;
                item.DiasEnPatio = Math.Max(0, (int)enPatio.TotalDays);
                item.HorasEnPatio = Math.Max(0, (int)enPatio.TotalHours);
            }

            if (!TieneRelacion(primera.Relacion))
            {
                item.Id = primera.CasetaId;
                item.Carril = SalidaCarril.Manual;
                item.Clientes = string.IsNullOrWhiteSpace(primera.NombreCliente)
                    ? new List<string>() : new List<string> { primera.NombreCliente };
                items.Add(item);
                continue;
            }

            string relno = primera.Relacion;
            item.Id = relno;
            item.Relno = relno;
            item.Clientes = clientesPorRelacion.TryGetValue(relno, out var cls) ? cls
                : grupo.Select(c => c.NombreCliente).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            if (bultosPorRelacion.TryGetValue(relno, out var b)) { item.BultosCargados = b.Cargados; item.BultosTotal = b.Total; }

            if (!relaciones.TryGetValue(relno, out var rel))
            {
                item.Carril = SalidaCarril.Proceso;
                item.Motivo = $"No se encontró la relación {relno}. Avisa a Sistemas.";
                items.Add(item);
                continue;
            }

            item.EstatusCarga = rel.EstatusCarga;
            item.Transfer = rel.Transfer; item.Operador = rel.Operador; item.Tractor = rel.Tractor; item.Gafete = rel.Gafete;
            item.Sellos = rel.Sellos; item.CajaRelacion = rel.CajaRelacion; item.UsuAutoriza = rel.UsuAutoriza;
            item.UsuCarga = rel.UsuCarga; item.UsuEdit = rel.UsuEdit;
            item.AutorizaCarga = EsAnioValido(rel.AutorizaCarga) ? rel.AutorizaCarga : null;
            if (item.AutorizaCarga.HasValue) item.MinutosEspera = Math.Max(0, (int)(ahora - item.AutorizaCarga.Value).TotalMinutes);
            item.InicioCarga = EsAnioValido(rel.InicioCarga) ? rel.InicioCarga : null;
            if (item.InicioCarga.HasValue) item.MinutosCargando = Math.Max(0, (int)(ahora - item.InicioCarga.Value).TotalMinutes);
            item.SalidaCaseta = EsAnioValido(rel.SalidaCaseta) ? rel.SalidaCaseta : null;

            bool yaSalio = string.Equals(rel.EstatusCarga, "SALIDA", StringComparison.OrdinalIgnoreCase) || item.SalidaCaseta.HasValue;
            bool ocupada = !string.IsNullOrWhiteSpace(rel.UsuEdit);
            bool cruzo = EsAnioValido(rel.FCruce);

            if (yaSalio)
            {
                item.Carril = SalidaCarril.Cerrar;
            }
            else if (string.Equals(rel.EstatusCarga, "AUTORIZADA", StringComparison.OrdinalIgnoreCase) && !ocupada && !cruzo)
            {
                item.Carril = SalidaCarril.Lista;
            }
            else
            {
                item.Carril = SalidaCarril.Proceso;
                item.Motivo = ocupada ? $"La relación está abierta por {rel.UsuEdit}."
                    : cruzo ? "La relación ya cruzó; no se da salida de caseta."
                    : rel.EstatusCarga.ToUpperInvariant() switch
                    {
                        "CARGANDO" => string.IsNullOrWhiteSpace(rel.UsuCarga) ? "Se está cargando." : $"{rel.UsuCarga} la está cargando.",
                        "CARGADA" => "Cargada completa. Falta que un supervisor la autorice.",
                        "DEMASIADO RAPIDO" => "La carga fue marcada como demasiado rápida; la libera un supervisor.",
                        "BLOQUEO" or "BLOQUEO REIMPRESION" => "Bloqueada por un supervisor.",
                        "EN ESPERA" => "En espera: todavía no empieza la carga.",
                        "" => "Relación creada, todavía sin carga iniciada.",
                        _ => $"Estatus {rel.EstatusCarga}."
                    };
            }
            items.Add(item);
        }

        var hoy = await conn.QueryFirstAsync<SalidaPatioHoy>(@"
            SELECT
                CAST(COUNT(DISTINCT CASE WHEN LEFT(IFNULL(relacion, ''), 2) = '01' THEN relacion END) AS SIGNED) AS ConRelacion,
                CAST(IFNULL(SUM(CASE WHEN IFNULL(relacion, '') IN ('', 'NO_RELACION') THEN 1 ELSE 0 END), 0) AS SIGNED) AS Manuales
            FROM caseta
            WHERE fsalida >= CURDATE() AND fsalida < CURDATE() + INTERVAL 1 DAY
              AND estatus = 'SALIDA'
              AND (@planta = '' OR planta = @planta)", new { planta });

        return new SalidaPatioResult { Success = true, Items = items, Hoy = hoy };
    }

    private static void AgregarCliente(Dictionary<string, List<string>> destino, ClienteRelacionRow fila)
    {
        if (!destino.TryGetValue(fila.Relno, out var lista))
            destino[fila.Relno] = lista = new List<string>();
        if (!lista.Contains(fila.Cliente, StringComparer.OrdinalIgnoreCase)) lista.Add(fila.Cliente);
    }

    /// <summary>
    /// La relación ya salió (por la vía de relación o por el sistema de escritorio)
    /// pero sus cajas siguen figurando en el patio. Cierra solo ese registro con la
    /// fecha de salida de la relación: NO genera una segunda salida ni avisa a nadie.
    /// </summary>
    public async Task<RegistrarSalidaCajaResult> CerrarRegistroAsync(string relno, string usuario, string planta = "")
    {
        relno = (relno ?? string.Empty).Trim();
        if (!TieneRelacion(relno))
            return new RegistrarSalidaCajaResult { Success = false, Message = "Falta el número de relación." };

        using IDbConnection conn = _db.CreateConnection();
        conn.Open();
        using IDbTransaction tx = conn.BeginTransaction();
        try
        {
            string tabla = TablaRelacion(relno);
            var rel = await conn.QueryFirstOrDefaultAsync<EstatusRelacionRow>(
                $"SELECT IFNULL(estatus_carga, '') AS EstatusCarga, CASE WHEN YEAR(salida_caseta) >= 2000 THEN salida_caseta END AS SalidaCaseta FROM {tabla} WHERE relno = @relno LIMIT 1",
                new { relno }, tx);

            if (rel is null)
            {
                tx.Rollback();
                return new RegistrarSalidaCajaResult { Success = false, Message = "No se encontró la relación." };
            }

            bool yaSalio = string.Equals(rel.EstatusCarga, "SALIDA", StringComparison.OrdinalIgnoreCase) || EsAnioValido(rel.SalidaCaseta);
            if (!yaSalio)
            {
                tx.Rollback();
                return new RegistrarSalidaCajaResult
                {
                    Success = false,
                    Message = "La relación todavía no ha salido; no se puede cerrar el registro de patio."
                };
            }

            DateTime ahora = DateTime.Now;
            DateTime fechaSalida = EsAnioValido(rel.SalidaCaseta) ? rel.SalidaCaseta!.Value : ahora;

            int filas = await conn.ExecuteAsync(@"
                UPDATE caseta
                SET fsalida   = @fechaSalida,
                    estatus   = 'SALIDA',
                    ususalida = @usuario,
                    usumodi   = @usuario,
                    fmodi     = @ahora,
                    obs_salida = @observacion
                WHERE relacion = @relno
                  AND (fsalida IS NULL OR fsalida < '2000-01-01')
                  AND estatus NOT IN ('CANCELADO', 'CANCELADA')
                  AND (@planta = '' OR planta = @planta)",
                new
                {
                    fechaSalida, usuario, ahora, relno, planta,
                    observacion = $"Cierre de patio: relación {relno} ya salió"
                }, tx);

            if (filas == 0)
            {
                tx.Rollback();
                return new RegistrarSalidaCajaResult { Success = false, Message = "No hay cajas por cerrar para esa relación." };
            }

            tx.Commit();
            return new RegistrarSalidaCajaResult
            {
                Success = true,
                Message = filas == 1 ? "Registro de patio cerrado." : $"Registro de patio cerrado ({filas} cajas)."
            };
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // Filas internas de Dapper (alias en PascalCase: la API no normaliza guion bajo).
    private sealed class CajaSalidaRow
    {
        public DateTime? Fsalida { get; set; }
        public string Relacion { get; set; } = string.Empty;
        public string Estatus { get; set; } = string.Empty;
        public string Planta { get; set; } = string.Empty;
    }

    private sealed class EstatusRelacionRow
    {
        public string EstatusCarga { get; set; } = string.Empty;
        public DateTime? SalidaCaseta { get; set; }
    }

    private sealed class PatioCajaRow
    {
        public string CasetaId { get; set; } = string.Empty;
        public string NumCaja { get; set; } = string.Empty;
        public string LineaCaja { get; set; } = string.Empty;
        public string NumCamion { get; set; } = string.Empty;
        public string Placas { get; set; } = string.Empty;
        public string NombreCliente { get; set; } = string.Empty;
        public string TipoUnidad { get; set; } = string.Empty;
        public string TipoMovimiento { get; set; } = string.Empty;
        public string TipoOp { get; set; } = string.Empty;
        public string Planta { get; set; } = string.Empty;
        public DateTime? FechaArribo { get; set; }
        public string TipoRegistro { get; set; } = string.Empty;
        public string EstatusCaseta { get; set; } = string.Empty;
        public string Relacion { get; set; } = string.Empty;
    }

    private sealed class PatioRelacionRow
    {
        public string Relno { get; set; } = string.Empty;
        public string EstatusCarga { get; set; } = string.Empty;
        public DateTime? AutorizaCarga { get; set; }
        public string UsuAutoriza { get; set; } = string.Empty;
        public string CajaRelacion { get; set; } = string.Empty;
        public string Transfer { get; set; } = string.Empty;
        public string Operador { get; set; } = string.Empty;
        public string Tractor { get; set; } = string.Empty;
        public string Gafete { get; set; } = string.Empty;
        public string Sellos { get; set; } = string.Empty;
        public string UsuEdit { get; set; } = string.Empty;
        public string UsuCarga { get; set; } = string.Empty;
        public DateTime? FCruce { get; set; }
        public DateTime? SalidaCaseta { get; set; }
        public DateTime? InicioCarga { get; set; }
    }

    private sealed class ClienteRelacionRow
    {
        public string Relno { get; set; } = string.Empty;
        public string Cliente { get; set; } = string.Empty;
    }

    private sealed class BultosRelacionRow
    {
        public string Relno { get; set; } = string.Empty;
        public int Total { get; set; }
        public int Cargados { get; set; }
    }

    private sealed class FotoCajaRow
    {
        public string CasetaId { get; set; } = string.Empty;
        public int Indice { get; set; }
    }
}
