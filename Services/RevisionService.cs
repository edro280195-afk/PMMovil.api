using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using MySqlConnector;

using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public sealed class RevisionEnUsoException : InvalidOperationException
{
    public RevisionEnUsoException(string usuario)
        : base($"La entrada ya está tomada por {usuario}.")
    {
        Usuario = usuario;
    }

    public string Usuario { get; }
}

public class RevisionService
{
    private const string Obsrev2DesdeAppSql = @"
        obsrev2 = CASE
            WHEN @observacionApp IS NULL OR @observacionApp = '' THEN obsrev2
            WHEN LOCATE(
                CONCAT(CHAR(10), @observacionApp, CHAR(10)),
                CONCAT(CHAR(10), COALESCE(obsrev2, ''), CHAR(10))) > 0 THEN obsrev2
            WHEN NULLIF(TRIM(COALESCE(obsrev2, '')), '') IS NULL THEN @observacionApp
            ELSE CONCAT(obsrev2, CHAR(10), @observacionApp)
        END";

    private readonly BaseRepository _db;
    private readonly IHubContext<WarehouseHub> _hub;
    private readonly ILogger<RevisionService> _logger;
    private readonly RevisionActivityService _activityService;
    private readonly RevisionNormalCompletionService _normalCompletionService;
    private readonly MonitorTvRealtimePublisher _monitorTvRealtimePublisher;
    private readonly EntrySearchIndex _searchIndex;

    public RevisionService(
        BaseRepository db,
        IHubContext<WarehouseHub> hub,
        ILogger<RevisionService> logger,
        RevisionActivityService activityService,
        RevisionNormalCompletionService normalCompletionService,
        MonitorTvRealtimePublisher monitorTvRealtimePublisher,
        EntrySearchIndex searchIndex)
    {
        _db = db;
        _hub = hub;
        _logger = logger;
        _activityService = activityService;
        _normalCompletionService = normalCompletionService;
        _monitorTvRealtimePublisher = monitorTvRealtimePublisher;
        _searchIndex = searchIndex;
    }

    /// <summary>
    /// Búsqueda rápida para la pantalla de Revisión: las mejores coincidencias salen del índice en memoria y solo a esas
    /// pocas (8 por omisión) se les consulta el estado real, en una sola pasada. No recorre la lista de pendientes.
    /// </summary>
    public async Task<BusquedaRapidaResultado> BuscarRapidoAsync(string? consulta, int limite, CancellationToken ct)
    {
        EntrySearchIndex.Snapshot snapshot = await _searchIndex.GetAsync(ct);
        EntrySearchResult found = EntrySearchRules.Search(snapshot.Entries, consulta, limite);
        if (found.Hits.Count == 0)
        {
            return new BusquedaRapidaResultado { Total = 0, Tolerante = found.Tolerant };
        }

        using IDbConnection conn = _db.CreateConnection();
        Dictionary<string, EntradaPendiente> resumenes = await CargarResumenesAsync(
            conn,
            found.Hits.Select(h => h.Entry.Reference).ToList(),
            ct);

        var hits = new List<EntradaBusquedaHit>(found.Hits.Count);
        foreach (EntrySearchHit hit in found.Hits)
        {
            // Una entrada del índice que ya no se puede resumir (se movió entre renovaciones) se omite en vez de mostrarla vacía.
            if (resumenes.TryGetValue(hit.Entry.Reference, out EntradaPendiente? item))
            {
                hits.Add(new EntradaBusquedaHit { Item = item, Aproximada = hit.Approximate });
            }
        }

        return new BusquedaRapidaResultado { Hits = hits, Total = found.Total, Tolerante = found.Tolerant };
    }

    /// <summary>
    /// El estado real de unas pocas entradas: facturas y partidas, quién la revisa, si la relación ya cerró. Mismas
    /// reglas que <see cref="GetEntradasPendientesAsync"/>, pero acotado a las referencias pedidas.
    /// </summary>
    private static async Task<Dictionary<string, EntradaPendiente>> CargarResumenesAsync(
        IDbConnection conn,
        IReadOnlyList<string> refs,
        CancellationToken ct)
    {
        List<EntradaPendiente> baseData = (await conn.QueryAsync<EntradaPendiente>(new CommandDefinition(@"
            SELECT
                f.cref_agrupada AS CrefAgrupada,
                MAX(f.cclienteid) AS ClienteId,
                MAX(IF(f.dfecha < '2000-01-01', NULL, f.dfecha)) AS dFecha,
                COUNT(DISTINCT f.cnofact) AS TotalFacturas,
                COUNT(DISTINCT CASE WHEN f.status_rev = 1 THEN f.cnofact END) AS FacturasCompletas,
                COUNT(DISTINCT rd.idrevdet) AS TotalPartidas,
                COUNT(DISTINCT CASE WHEN rd.revisada = 1 THEN rd.idrevdet END) AS PartidasRevisadas,
                MAX(cl.cnombre) AS NombreCliente
            FROM fact f
            LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid AND cl.inactivo = 0
            LEFT JOIN revdet rd ON rd.idfactura = f.idfactura
            WHERE f.cref_agrupada IN @refs
            GROUP BY f.cref_agrupada", new { refs }, cancellationToken: ct))).ToList();

        // Las que solo existen como código de barras no tienen facturas: salen de traf.
        HashSet<string> conFacturas = baseData.Select(e => e.CrefAgrupada).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> soloBarras = refs.Where(r => !conFacturas.Contains(r)).ToList();
        if (soloBarras.Count > 0)
        {
            baseData.AddRange(await conn.QueryAsync<EntradaPendiente>(new CommandDefinition(@"
                SELECT
                    t.creferencia AS CrefAgrupada,
                    MAX(t.cclienteidtraf) AS ClienteId,
                    MAX(IF(t.fcreatrafico < '2000-01-01', NULL, t.fcreatrafico)) AS dFecha,
                    0 AS TotalFacturas,
                    0 AS FacturasCompletas,
                    0 AS TotalPartidas,
                    0 AS PartidasRevisadas,
                    MAX(cl.cnombre) AS NombreCliente
                FROM traf t
                LEFT JOIN clientes cl ON cl.cidcte = t.cclienteidtraf AND cl.inactivo = 0
                WHERE t.creferencia IN @refs
                GROUP BY t.creferencia", new { refs = soloBarras }, cancellationToken: ct)));
        }

        var sesiones = (await conn.QueryAsync(new CommandDefinition(@"
            SELECT
                t1.creferencia AS CrefAgrupada,
                IF(t1.finiciorevision < '2000-01-01', NULL, t1.finiciorevision) AS finiciorevision,
                IF(t1.ffinrevision < '2000-01-01', NULL, t1.ffinrevision) AS ffinrevision,
                t1.usuario,
                IF(t1.ftomadocumento < '2000-01-01', NULL, t1.ftomadocumento) AS ftomadocumento
            FROM traftiemposrevision t1
            INNER JOIN (
                SELECT creferencia, MAX(idtiemposrevision) AS maxid
                FROM traftiemposrevision
                WHERE creferencia IN @refs
                GROUP BY creferencia
            ) t2 ON t1.idtiemposrevision = t2.maxid", new { refs }, cancellationToken: ct)))
            .ToDictionary((dynamic x) => (string)x.CrefAgrupada, (dynamic x) => (object)x, StringComparer.OrdinalIgnoreCase);

        var relaciones = (await conn.QueryAsync(new CommandDefinition(@"
            SELECT tf2.CREF_ORIGEN AS CrefAgrupada,
                   COUNT(*) AS Total,
                   SUM(CASE WHEN COALESCE(r2.STATUS,'') != 'COMPLETA' THEN 1 ELSE 0 END) AS Pendientes
            FROM traf2 tf2
            LEFT JOIN relacion r2 ON tf2.nrelacion = r2.relno
            WHERE tf2.CREF_ORIGEN IN @refs
            GROUP BY tf2.CREF_ORIGEN", new { refs }, cancellationToken: ct)))
            .ToDictionary((dynamic x) => (string)x.CrefAgrupada, (dynamic x) => ((long)x.Total, (long)x.Pendientes), StringComparer.OrdinalIgnoreCase);

        foreach (EntradaPendiente item in baseData)
        {
            if (sesiones.TryGetValue(item.CrefAgrupada, out dynamic s))
            {
                DateTime? inicio = s.finiciorevision as DateTime?;
                DateTime? fin = s.ffinrevision as DateTime?;
                DateTime? toma = s.ftomadocumento as DateTime?;
                item.InicioRevision = inicio?.ToString("yyyy/MM/dd HH:mm:ss");
                item.FinRevision = fin?.ToString("yyyy/MM/dd HH:mm:ss");
                item.UsuarioRevisando = (item.InicioRevision != null && item.FinRevision == null) ? (string)s.usuario : null;
                // Igual que en Pendientes: el usuario de la toma solo cuenta mientras la sesión sigue abierta.
                if (item.UsuarioRevisando == null && fin == null && toma.HasValue)
                {
                    item.UsuarioTomoDocumento = (string)s.usuario;
                }
            }
            item.TodasRelacionCompleta = relaciones.TryGetValue(item.CrefAgrupada, out var rel)
                && rel.Item1 > 0
                && rel.Item2 == 0;
        }

        // «¿Quién trae la 123?»: si alguien tomó la carpeta, el resultado de la búsqueda lo dice.
        await CarpetaTomaService.AdjuntarTomasAsync(conn, baseData);

        return baseData
            .GroupBy(e => e.CrefAgrupada, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<(List<EntradaPendiente> Items, bool Truncado)> GetEntradasPendientesAsync(string filtro, bool strict)
    {
        using IDbConnection conn = _db.CreateConnection();
        bool tieneFiltro = !string.IsNullOrWhiteSpace(filtro);
        bool useStrict = strict && tieneFiltro;
        var safeFiltro = (filtro ?? "").Replace("%", "\\%").Replace("_", "\\_");

        string filterSql = useStrict
            ? "f.cref_agrupada = @filtroExacto"
            : "(@filtro = '' OR f.cref_agrupada LIKE @filtroLike OR f.creferencia LIKE @filtroLike OR f.cclienteid LIKE @filtroLike OR cl.cnombre LIKE @filtroLike)";

        string filterSqlBarcode = useStrict
            ? "t.creferencia = @filtroExacto"
            : "(@filtro = '' OR t.creferencia LIKE @filtroLike OR t.cclienteidtraf LIKE @filtroLike OR t.cidprovtraf LIKE @filtroLike OR cl.cnombre LIKE @filtroLike)";

        string sqlIds = @"
            SELECT f.cref_agrupada 
            FROM fact f 
            LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid AND cl.inactivo = 0 
            WHERE f.cref_agrupada != '' AND f.cref_agrupada IS NOT NULL 
              AND (@tieneFiltro = 1 OR f.dfecha >= DATE_SUB(NOW(), INTERVAL 60 DAY) OR YEAR(f.dfecha) < 2000) 
              AND " + filterSql + @" 
            GROUP BY f.cref_agrupada 
            ORDER BY MAX(f.dfecha) DESC 
            LIMIT @limitValue";

        string sqlBarcodeIds = @"
            SELECT t.creferencia
            FROM traf t
            INNER JOIN cteprov cp
                    ON cp.cidcte = t.cclienteidtraf
                   AND cp.cidprov = t.cidprovtraf
                   AND cp.inactivo = 0
                   AND cp.barcode_long > 0
            LEFT JOIN clientes cl
                   ON cl.cidcte = t.cclienteidtraf
                  AND cl.inactivo = 0
            WHERE t.creferencia != ''
              AND t.creferencia IS NOT NULL
              AND NOT EXISTS (
                    SELECT 1
                    FROM fact f0
                    WHERE f0.cref_agrupada = t.creferencia
              )
              AND (@tieneFiltro = 1 OR t.fcreatrafico >= DATE_SUB(NOW(), INTERVAL 60 DAY) OR YEAR(t.fcreatrafico) < 2000)
              AND " + filterSqlBarcode + @"
            GROUP BY t.creferencia
            ORDER BY MAX(t.fcreatrafico) DESC
            LIMIT @limitValue";

        List<string> pendingTimerRefs = (await conn.QueryAsync<string>(
"SELECT DISTINCT creferencia FROM traftiemposrevision WHERE ffinrevision IS NULL OR ffinrevision < '2000-01-01'"
)).ToList();
        List<string> filteredFactRefs = (await conn.QueryAsync<string>(sqlIds, new
        {
            filtro = safeFiltro,
            filtroLike = "%" + safeFiltro + "%",
            filtroExacto = safeFiltro,
            tieneFiltro = tieneFiltro ? 1 : 0,
            limitValue = !tieneFiltro ? 500 : (useStrict ? 1 : 999999)
        })).ToList();
        List<string> filteredBarcodeRefs = (await conn.QueryAsync<string>(sqlBarcodeIds, new
        {
            filtro = safeFiltro,
            filtroLike = "%" + safeFiltro + "%",
            filtroExacto = safeFiltro,
            tieneFiltro = tieneFiltro ? 1 : 0,
            limitValue = !tieneFiltro ? 500 : (useStrict ? 1 : 999999)
        })).ToList();
        List<string> filteredRefs = filteredFactRefs
            .Union(filteredBarcodeRefs)
            .Distinct()
            .ToList();

        List<string> finalIds = useStrict
? filteredRefs
: Enumerable.Union(filteredRefs, pendingTimerRefs).Distinct().ToList();

        if (!finalIds.Any())
        {
            return (new List<EntradaPendiente>(), false);
        }

        string sqlBase = @"
            SELECT 
                f.cref_agrupada AS CrefAgrupada,
                MAX(f.creferencia) AS NombreReferencia,
                MAX(f.cclienteid) AS ClienteId,
                MAX(IF(f.dfecha < '2000-01-01', NULL, f.dfecha)) AS dFecha,
                COUNT(DISTINCT f.cnofact) AS TotalFacturas,
                COUNT(DISTINCT CASE WHEN f.status_rev = 1 THEN f.cnofact END) AS FacturasCompletas,
                COUNT(DISTINCT rd.idrevdet) AS TotalPartidas,
                COUNT(DISTINCT CASE WHEN rd.revisada = 1 THEN rd.idrevdet END) AS PartidasRevisadas,
                MAX(cl.cnombre) AS NombreCliente 
            FROM fact f 
            LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid 
            LEFT JOIN revdet rd ON rd.idfactura = f.idfactura 
            WHERE f.cref_agrupada IN @finalIds 
            GROUP BY f.cref_agrupada";

        List<EntradaPendiente> baseData = (await conn.QueryAsync<EntradaPendiente>(sqlBase, new { finalIds })).ToList();

        string sqlBarcodeBase = @"
            SELECT
                t.creferencia AS CrefAgrupada,
                MAX(t.creferencia) AS NombreReferencia,
                MAX(t.cclienteidtraf) AS ClienteId,
                MAX(IF(t.fcreatrafico < '2000-01-01', NULL, t.fcreatrafico)) AS dFecha,
                0 AS TotalFacturas,
                0 AS FacturasCompletas,
                0 AS TotalPartidas,
                0 AS PartidasRevisadas,
                MAX(cl.cnombre) AS NombreCliente
            FROM traf t
            INNER JOIN cteprov cp
                    ON cp.cidcte = t.cclienteidtraf
                   AND cp.cidprov = t.cidprovtraf
                   AND cp.inactivo = 0
                   AND cp.barcode_long > 0
            LEFT JOIN clientes cl
                   ON cl.cidcte = t.cclienteidtraf
                  AND cl.inactivo = 0
            WHERE t.creferencia IN @finalIds
              AND NOT EXISTS (
                    SELECT 1
                    FROM fact f0
                    WHERE f0.cref_agrupada = t.creferencia
              )
            GROUP BY t.creferencia";

        baseData.AddRange(await conn.QueryAsync<EntradaPendiente>(sqlBarcodeBase, new { finalIds }));

        string sqlSessions = @"
            SELECT 
                t1.creferencia AS CrefAgrupada, 
                IF(t1.finiciorevision < '2000-01-01', NULL, t1.finiciorevision) AS finiciorevision,
                IF(t1.ffinrevision < '2000-01-01', NULL, t1.ffinrevision) AS ffinrevision,
                t1.usuario, 
                IF(t1.ftomadocumento < '2000-01-01', NULL, t1.ftomadocumento) AS ftomadocumento 
            FROM traftiemposrevision t1 
            INNER JOIN (
                SELECT creferencia, MAX(idtiemposrevision) as maxid 
                FROM traftiemposrevision 
                WHERE creferencia IN @finalIds 
                GROUP BY creferencia
            ) t2 ON t1.idtiemposrevision = t2.maxid";

        var sessionRows = await conn.QueryAsync(sqlSessions, new { finalIds });
        Dictionary<string, object> sessionData = sessionRows.ToDictionary(
            (dynamic x) => (string)x.CrefAgrupada,
            (dynamic x) => (object)x
        );

        // Mismo criterio que EsRelacionCerradaAsync / GetRevisionFacturaAsync:
        // LEFT JOIN (no INNER) para que una entrada sin relación asociada, o con
        // nrelacion que no matchea ninguna fila de relacion, cuente como
        // "pendiente" en vez de desaparecer del resultado del join. Con el
        // INNER JOIN de antes esas entradas no salían aquí, y el código de
        // abajo las marcaba TodasRelacionCompleta=true por default -- justo lo
        // contrario de lo que dice el criterio canónico (COUNT(*)=0 => no
        // cerrada) en ese mismo caso.
        string sqlRel = @"
            SELECT tf2.CREF_ORIGEN AS CrefAgrupada,
                   COUNT(*) AS Total,
                   SUM(CASE WHEN COALESCE(r2.STATUS,'') != 'COMPLETA' THEN 1 ELSE 0 END) AS Pendientes
            FROM traf2 tf2
            LEFT JOIN relacion r2 ON tf2.nrelacion = r2.relno
            WHERE tf2.CREF_ORIGEN IN @finalIds
            GROUP BY tf2.CREF_ORIGEN";

        var relRows = await conn.QueryAsync(sqlRel, new { finalIds });
        Dictionary<string, (long Total, long Pendientes)> relData = relRows.ToDictionary(
            (dynamic x) => (string)x.CrefAgrupada,
            (dynamic x) => ((long)x.Total, (long)x.Pendientes)
        );

        foreach (EntradaPendiente item in baseData)
        {
            if (sessionData.TryGetValue(item.CrefAgrupada, out dynamic s))
            {
                DateTime? inicio = s.finiciorevision as DateTime?;
                DateTime? fin = s.ffinrevision as DateTime?;
                DateTime? toma = s.ftomadocumento as DateTime?;
                item.InicioRevision = inicio?.ToString("yyyy/MM/dd HH:mm:ss");
                item.FinRevision = fin?.ToString("yyyy/MM/dd HH:mm:ss");
                item.UsuarioRevisando = (item.InicioRevision != null && item.FinRevision == null) ? (string)s.usuario : null;
                // El usuario de la toma solo representa una sesión abierta.
                // La fila histórica conserva el usuario, pero no debe hacer
                // que una entrada cerrada aparezca bloqueada en Pendientes.
                if (item.UsuarioRevisando == null && fin == null && toma.HasValue)
                {
                    item.UsuarioTomoDocumento = (string)s.usuario;
                }
            }
            if (relData.TryGetValue(item.CrefAgrupada, out var rel))
            {
                item.TodasRelacionCompleta = rel.Total > 0 && rel.Pendientes == 0;
            }
            else
            {
                // Sin ninguna fila en traf2 para esta entrada: igual que
                // EsRelacionCerradaAsync (COUNT(*) = 0), no cuenta como cerrada.
                item.TodasRelacionCompleta = false;
            }
        }

        await CarpetaTomaService.AdjuntarTomasAsync(conn, baseData);

        List<EntradaPendiente> sorted = baseData
            .OrderByDescending(x => x.UsuarioRevisando != null ? 1 : 0)
            .ThenByDescending(x => x.InicioRevision ?? "0000")
            .ThenByDescending(x => x.dFecha)
            .ToList();

        return (sorted, !tieneFiltro);
    }

    public async Task<EntradaPendiente?> BuscarPorCreferenciaAsync(string creferencia)
    {
        if (string.IsNullOrWhiteSpace(creferencia))
        {
            return null;
        }

        using IDbConnection conn = _db.CreateConnection();

        var item = await conn.QueryFirstOrDefaultAsync<EntradaPendiente>(@"
            SELECT
                t.creferencia AS CrefAgrupada,
                t.cclienteidtraf AS ClienteId,
                MAX(c.cnombre) AS NombreCliente,
                MAX(t.fcreatrafico) AS dFecha,
                COUNT(DISTINCT f.cnofact) AS TotalFacturas,
                COUNT(DISTINCT CASE WHEN f.status_rev = 1 THEN f.cnofact END) AS FacturasCompletas,
                COUNT(DISTINCT rd.idrevdet) AS TotalPartidas,
                COUNT(DISTINCT CASE WHEN rd.revisada = 1 THEN rd.idrevdet END) AS PartidasRevisadas
            FROM traf t
            LEFT JOIN clientes c ON c.cidcte = t.cclienteidtraf
            LEFT JOIN fact f ON f.cref_agrupada = t.creferencia
            LEFT JOIN revdet rd ON rd.idfactura = f.idfactura
            WHERE t.creferencia = @creferencia
            GROUP BY t.creferencia, t.cclienteidtraf",
new { creferencia });

        if (item == null || item.TotalFacturas == 0)
        {
            // La pantalla de detalle se alimenta de fact.cref_agrupada, pero
            // algunas entradas no tienen una fila equivalente en traf. En
            // esos casos el QR sí representa una entrada válida y debe poder
            // abrirse usando las facturas como fuente de verdad.
            item = await conn.QueryFirstOrDefaultAsync<EntradaPendiente>(@"
                SELECT
                    f.cref_agrupada AS CrefAgrupada,
                    MAX(f.cclienteid) AS ClienteId,
                    MAX(c.cnombre) AS NombreCliente,
                    MAX(f.dfecha) AS dFecha,
                    COUNT(DISTINCT f.cnofact) AS TotalFacturas,
                    COUNT(DISTINCT CASE WHEN f.status_rev = 1 THEN f.cnofact END) AS FacturasCompletas,
                    COUNT(DISTINCT rd.idrevdet) AS TotalPartidas,
                    COUNT(DISTINCT CASE WHEN rd.revisada = 1 THEN rd.idrevdet END) AS PartidasRevisadas
                FROM fact f
                LEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0
                LEFT JOIN revdet rd ON rd.idfactura = f.idfactura
                WHERE f.cref_agrupada = @creferencia
                GROUP BY f.cref_agrupada",
                new { creferencia });

            if (item == null)
            {
                item = await conn.QueryFirstOrDefaultAsync<EntradaPendiente>(@"
                    SELECT
                        t.creferencia AS CrefAgrupada,
                        MAX(t.creferencia) AS NombreReferencia,
                        MAX(t.cclienteidtraf) AS ClienteId,
                        MAX(c.cnombre) AS NombreCliente,
                        MAX(IF(t.fcreatrafico < '2000-01-01', NULL, t.fcreatrafico)) AS dFecha,
                        0 AS TotalFacturas,
                        0 AS FacturasCompletas,
                        0 AS TotalPartidas,
                        0 AS PartidasRevisadas
                    FROM traf t
                    INNER JOIN cteprov cp
                            ON cp.cidcte = t.cclienteidtraf
                           AND cp.cidprov = t.cidprovtraf
                           AND cp.inactivo = 0
                           AND cp.barcode_long > 0
                    LEFT JOIN clientes c
                           ON c.cidcte = t.cclienteidtraf
                          AND c.inactivo = 0
                    WHERE t.creferencia = @creferencia
                      AND NOT EXISTS (
                            SELECT 1
                            FROM fact f0
                            WHERE f0.cref_agrupada = t.creferencia
                      )
                    GROUP BY t.creferencia",
                    new { creferencia });

                if (item == null)
                {
                    _logger.LogWarning(
                        "No se encontró la referencia agrupada {CrefAgrupada} ni en traf.creferencia ni en fact.cref_agrupada",
                        creferencia);
                    return null;
                }

                _logger.LogInformation(
                    "Referencia de código de barras {CrefAgrupada} encontrada solo en traf: {TotalFacturas} facturas, {TotalPartidas} partidas",
                    creferencia,
                    item.TotalFacturas,
                    item.TotalPartidas);
            }
            else
            {
                _logger.LogInformation(
                    "Referencia agrupada {CrefAgrupada} encontrada por fact.cref_agrupada: {TotalFacturas} facturas, {TotalPartidas} partidas",
                    creferencia,
                    item.TotalFacturas,
                    item.TotalPartidas);
            }
        }

        var sessionRow = await conn.QueryFirstOrDefaultAsync(@"
            SELECT
                IF(t1.finiciorevision < '2000-01-01', NULL, t1.finiciorevision) AS finiciorevision,
                IF(t1.ffinrevision < '2000-01-01', NULL, t1.ffinrevision) AS ffinrevision,
                t1.usuario,
                IF(t1.ftomadocumento < '2000-01-01', NULL, t1.ftomadocumento) AS ftomadocumento
            FROM traftiemposrevision t1
            INNER JOIN (
                SELECT creferencia, MAX(idtiemposrevision) AS maxid
                FROM traftiemposrevision
                WHERE creferencia = @creferencia
                GROUP BY creferencia
            ) t2 ON t1.idtiemposrevision = t2.maxid",
new { creferencia });

        if (sessionRow != null)
        {
            dynamic s = sessionRow;
            DateTime? inicio = s.finiciorevision as DateTime?;
            DateTime? fin = s.ffinrevision as DateTime?;
            DateTime? toma = s.ftomadocumento as DateTime?;
            item.InicioRevision = inicio?.ToString("yyyy/MM/dd HH:mm:ss");
            item.FinRevision = fin?.ToString("yyyy/MM/dd HH:mm:ss");
            item.UsuarioRevisando = (item.InicioRevision != null && item.FinRevision == null) ? (string)s.usuario : null;
            if (item.UsuarioRevisando == null && fin == null && toma.HasValue)
            {
                item.UsuarioTomoDocumento = (string)s.usuario;
            }
        }

        // Mismo criterio que EsRelacionCerradaAsync: LEFT JOIN (no INNER, para
        // no perder las entradas sin relación asociada) y comparando por
        // CREF_ORIGEN -- no por "creferencia", que en traf2 es una columna
        // distinta (el legacy las mantiene como dos campos separados en el
        // mismo UPDATE). @creferencia aquí viene de traf.creferencia, que es
        // el mismo valor que fact.cref_agrupada, así que compara correcto
        // contra CREF_ORIGEN.
        // COALESCE en el SUM: sin GROUP BY, si no hay ninguna fila de traf2 que
        // matchee el WHERE, COUNT(*) da 0 pero SUM(...) da NULL -- y Dapper no
        // puede mapear NULL a long.
        var relRow = await conn.QueryFirstOrDefaultAsync<(long Total, long Pendientes)>(@"
            SELECT COUNT(*) AS Total,
                   COALESCE(SUM(CASE WHEN COALESCE(r2.STATUS,'') != 'COMPLETA' THEN 1 ELSE 0 END), 0) AS Pendientes
            FROM traf2 tf2
            LEFT JOIN relacion r2 ON tf2.nrelacion = r2.relno
            WHERE tf2.CREF_ORIGEN = @creferencia",
new { creferencia });

        item.TodasRelacionCompleta = relRow.Total > 0 && relRow.Pendientes == 0;

        await CarpetaTomaService.AdjuntarTomasAsync(conn, new[] { item });

        return item;
    }

    /// <summary>
    /// Lee en dos consultas agrupadas los datos necesarios para las tarjetas AR.
    /// Es de solo lectura y la lista ya viene limitada a seis referencias por el controlador.
    /// </summary>
    public async Task<List<EntryArCardDto>> GetEntryArCardsAsync(IReadOnlyCollection<string> references)
    {
        using IDbConnection conn = _db.CreateConnection();
        const string sql = @"
            SELECT
                t.creferencia AS Reference,
                1 AS `Exists`,
                COALESCE(MAX(c.cnombre), '') AS CustomerName,
                COALESCE(MAX(td.Description), '') AS Description,
                COALESCE(MAX(td.Location), '') AS Location,
                CASE WHEN MAX(t.fcreatrafico) < '2000-01-01' THEN NULL
                     ELSE DATE_FORMAT(MAX(t.fcreatrafico), '%Y-%m-%dT%H:%i:%s') END AS ArrivalDate,
                MAX(CASE WHEN COALESCE(t.lhazmat, 0) = 1 THEN 1 ELSE 0 END) AS HasHazmat,
                MAX(COALESCE(t3.HasDamage, 0)) AS HasDamage,
                COALESCE(MAX(t3.DamageDescription), '') AS DamageDescription
            FROM traf t
            LEFT JOIN clientes c ON c.cidcte = t.cclienteidtraf AND c.inactivo = 0
            LEFT JOIN (
                SELECT
                    creferencia,
                    GROUP_CONCAT(DISTINCT NULLIF(TRIM(cdescripcion), '') SEPARATOR ' · ') AS Description,
                    GROUP_CONCAT(DISTINCT NULLIF(TRIM(ubicacion), '') SEPARATOR ' · ') AS Location
                FROM trafdet
                GROUP BY creferencia
            ) td ON td.creferencia = t.creferencia
            LEFT JOIN (
                SELECT
                    fk_idtraf,
                    MAX(CASE WHEN COALESCE(danada, 0) = 1 THEN 1 ELSE 0 END) AS HasDamage,
                    MAX(COALESCE(danada_descripcion, '')) AS DamageDescription
                FROM traf3
                GROUP BY fk_idtraf
            ) t3 ON t3.fk_idtraf = t.idtraf
            WHERE t.creferencia IN @References
            GROUP BY t.creferencia

            UNION ALL

            SELECT
                f.cref_agrupada AS Reference,
                1 AS `Exists`,
                COALESCE(MAX(c.cnombre), '') AS CustomerName,
                COALESCE(MAX(td.Description), '') AS Description,
                COALESCE(MAX(td.Location), '') AS Location,
                CASE WHEN MAX(f.dfecha) < '2000-01-01' THEN NULL
                     ELSE DATE_FORMAT(MAX(f.dfecha), '%Y-%m-%dT%H:%i:%s') END AS ArrivalDate,
                0 AS HasHazmat,
                0 AS HasDamage,
                '' AS DamageDescription
            FROM fact f
            LEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0
            LEFT JOIN (
                SELECT
                    creferencia,
                    GROUP_CONCAT(DISTINCT NULLIF(TRIM(cdescripcion), '') SEPARATOR ' · ') AS Description,
                    GROUP_CONCAT(DISTINCT NULLIF(TRIM(ubicacion), '') SEPARATOR ' · ') AS Location
                FROM trafdet
                GROUP BY creferencia
            ) td ON td.creferencia = f.cref_agrupada
            WHERE f.cref_agrupada IN @References
              AND NOT EXISTS (
                  SELECT 1 FROM traf t0 WHERE t0.creferencia = f.cref_agrupada
              )
            GROUP BY f.cref_agrupada";

        List<EntryArCardDto> found = (await conn.QueryAsync<EntryArCardDto>(sql, new { References = references }))
            .GroupBy(item => item.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        Dictionary<string, RevisionNormalCompletionSummary> summaries = await _normalCompletionService
            .ObtenerResumenesAsync(conn, references);
        foreach (EntryArCardDto item in found)
        {
            if (summaries.TryGetValue(item.Reference, out RevisionNormalCompletionSummary? summary))
            {
                item.ReviewStatus = summary.ReviewStatus;
                item.TotalInvoices = summary.TotalInvoices;
                item.CompletedInvoices = summary.CompletedInvoices;
                item.TotalLines = summary.TotalLines;
                item.ReviewedLines = summary.ReviewedLines;
            }
        }
        Dictionary<string, EntryArCardDto> byReference = found.ToDictionary(
            item => item.Reference,
            StringComparer.OrdinalIgnoreCase);

        return references.Select(reference => byReference.TryGetValue(reference, out EntryArCardDto? item)
            ? item
            : new EntryArCardDto { Reference = reference, Exists = false })
            .ToList();
    }

    public async Task<List<EntradaPendiente>> GetMisActivasAsync(string username)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new List<EntradaPendiente>();
        }

        using IDbConnection conn = _db.CreateConnection();
        string sqlRefs = @"
            SELECT 
                t1.creferencia, 
                IF(t1.finiciorevision < '2000-01-01', NULL, t1.finiciorevision) AS finiciorevision, 
                IF(t1.ffinrevision < '2000-01-01', NULL, t1.ffinrevision) AS ffinrevision, 
                IF(t1.ftomadocumento < '2000-01-01', NULL, t1.ftomadocumento) AS ftomadocumento 
            FROM traftiemposrevision t1 
            INNER JOIN (
                SELECT creferencia, MAX(idtiemposrevision) AS maxid
                FROM traftiemposrevision
                WHERE usuario = @username
                GROUP BY creferencia
            ) latest ON t1.idtiemposrevision = latest.maxid
            WHERE (t1.ffinrevision IS NULL OR YEAR(t1.ffinrevision) < 2000)
               OR COALESCE(t1.estatus_final, 0) NOT IN (1, 2)
            ORDER BY t1.finiciorevision DESC
            LIMIT 40";

        List<object> sesiones = (await conn.QueryAsync(sqlRefs, new { username })).ToList();
        HashSet<string> timerRefs = sesiones.Select((dynamic val) => (string)val.creferencia).ToHashSet();

        List<dynamic> asignaciones = (await conn.QueryAsync(@"
            SELECT id_asignacion AS IdAsignacion, cref_agrupada AS CrefAgrupada, prioridad AS Prioridad 
            FROM asignaciones_revision 
            WHERE usuario_asignado = @username AND estatus IN (0, 1) 
            ORDER BY prioridad DESC, fecha_asignacion DESC 
            LIMIT 20", new { username })).ToList();

        List<object> asigNuevas = asignaciones.Where((dynamic a) => !timerRefs.Contains((string)a.CrefAgrupada)).ToList();
        List<string> todasRefs = timerRefs.Union(asigNuevas.Select((dynamic a) => (string)a.CrefAgrupada)).ToList();

        if (!todasRefs.Any())
        {
            return new List<EntradaPendiente>();
        }

        string sqlBase = @"
            SELECT 
                f.cref_agrupada AS CrefAgrupada, 
                MAX(f.cclienteid) AS ClienteId, 
                MAX(cl.cnombre) AS NombreCliente, 
                COUNT(DISTINCT f.cnofact) AS TotalFacturas, 
                COUNT(DISTINCT CASE WHEN f.status_rev = 1 THEN f.cnofact END) AS FacturasCompletas, 
                COUNT(DISTINCT rd.idrevdet) AS TotalPartidas, 
                COUNT(DISTINCT CASE WHEN rd.revisada = 1 THEN rd.idrevdet END) AS PartidasRevisadas 
            FROM fact f 
            LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid AND cl.inactivo = 0 
            LEFT JOIN revdet rd ON rd.idfactura = f.idfactura 
            WHERE f.cref_agrupada IN @refs 
            GROUP BY f.cref_agrupada";

        List<EntradaPendiente> items = (await conn.QueryAsync<EntradaPendiente>(sqlBase, new { refs = todasRefs })).ToList();

        // Antes esta lista ("Mis activas") no calculaba TodasRelacionCompleta
        // en absoluto -- quedaba en el default de C# (false), coincidiendo por
        // accidente con "no cerrada" pero sin ser un cálculo real. Mismo
        // criterio (LEFT JOIN) que GetEntradasPendientesAsync/EsRelacionCerradaAsync.
        string sqlRel = @"
            SELECT tf2.CREF_ORIGEN AS CrefAgrupada,
                   COUNT(*) AS Total,
                   SUM(CASE WHEN COALESCE(r2.STATUS,'') != 'COMPLETA' THEN 1 ELSE 0 END) AS Pendientes
            FROM traf2 tf2
            LEFT JOIN relacion r2 ON tf2.nrelacion = r2.relno
            WHERE tf2.CREF_ORIGEN IN @refs
            GROUP BY tf2.CREF_ORIGEN";
        var relRows = await conn.QueryAsync(sqlRel, new { refs = todasRefs });
        Dictionary<string, (long Total, long Pendientes)> relData = relRows.ToDictionary(
            (dynamic x) => (string)x.CrefAgrupada,
            (dynamic x) => ((long)x.Total, (long)x.Pendientes)
        );

        Dictionary<string, dynamic> sesionDict = sesiones.ToDictionary((dynamic val) => (string)val.creferencia, (dynamic result) => result);
        Dictionary<string, dynamic> asigDict = asigNuevas.ToDictionary((dynamic a) => (string)a.CrefAgrupada, (dynamic a) => a);

        foreach (EntradaPendiente item in items)
        {
            if (sesionDict.TryGetValue(item.CrefAgrupada, out var s))
            {
                DateTime? inicio = s.finiciorevision as DateTime?;
                DateTime? fin = s.ffinrevision as DateTime?;
                DateTime? toma = s.ftomadocumento as DateTime?;
                bool timerActivo = inicio.HasValue && (!fin.HasValue || fin.Value.Year < 2000);
                item.InicioRevision = inicio?.ToString("yyyy/MM/dd HH:mm:ss");
                item.FinRevision = fin?.ToString("yyyy/MM/dd HH:mm:ss");
                item.UsuarioRevisando = timerActivo ? username : null;
                item.UsuarioTomoDocumento = !timerActivo && toma.HasValue ? username : null;
            }
            if (asigDict.TryGetValue(item.CrefAgrupada, out var asig))
            {
                item.EsAsignada = true;
                item.AsignPrioridad = (int)asig.Prioridad;
                item.IdAsignacion = (int)asig.IdAsignacion;
            }
            item.TodasRelacionCompleta = relData.TryGetValue(item.CrefAgrupada, out var rel)
                && rel.Total > 0
                && rel.Pendientes == 0;
        }

        await CarpetaTomaService.AdjuntarTomasAsync(conn, items);

        return items
            .OrderByDescending(i => (i.EsAsignada ?? false) ? (i.AsignPrioridad.GetValueOrDefault() + 10) : 0)
            .ThenByDescending(i => i.InicioRevision)
            .ToList();
    }

    public async Task<IEnumerable<FacturaRevision>> GetFacturasEntradaAsync(string cref_agrupada)
    {
        using IDbConnection conn = _db.CreateConnection();
        string sql = @"
            SELECT
                TRIM(f.cnofact) AS Cnofact,
                MAX(f.cclienteid) AS cclienteid,
                MAX(f.tipoop) AS tipoop,
                COALESCE(MAX(f.status_rev), 0) AS StatusRev,
                COALESCE(MAX(NULLIF(TRIM(f.usurev), '')), '') AS Usurev,
                COALESCE(MAX(NULLIF(TRIM(f.obsrev), '')), '') AS Obsrev,
                COALESCE(MAX(NULLIF(TRIM(f.obsrev2), '')), '') AS Obsrev2,
                COALESCE(MAX(f.totbultos), 0) AS TotBultos,
                COALESCE(MAX(f.npesolbs), 0) AS nPesoLbs,
                COALESCE(MAX(f.npesokgs), 0) AS nPesoKgs,
                COALESCE(MAX(f.nsubtotal), 0) AS nSubtotal,
                COALESCE(MAX(f.nvalorme), 0) AS nValorme,
                COALESCE(MAX(f.totbultosdoc), 0) AS TotBultosDoc,
                COALESCE(MAX(f.marcas), '') AS Marcas,
                COALESCE(MAX(f.numeros), '') AS Numeros,
                COALESCE(MAX(f.usuedit), '') AS UsuEdit,
                DATE_FORMAT(MAX(f.dfecha), '%d/%m/%Y') AS dFecha,
                COALESCE(MAX(NULLIF(TRIM(f.cpedido), '')), '') AS cpedido,
                COALESCE(MAX(NULLIF(TRIM(f.bodegafact), '')), '') AS bodegaFact,
                MAX(cl.cnombre)         AS NombreCliente,
                GROUP_CONCAT(DISTINCT p.cnombre SEPARATOR ', ') AS NombreProveedor
            FROM fact f
            LEFT JOIN clientes cl
                ON cl.cidcte = (SELECT cclienteid FROM fact WHERE cnofact = f.cnofact AND cclienteid IS NOT NULL AND cclienteid != '' ORDER BY fmodi DESC LIMIT 1)
                AND cl.inactivo = 0
            -- Una sola pasada por revdet para resolver TODOS los cprovid
            -- de las facturas de esta entrada (1 lookup por factura).
            LEFT JOIN (
                SELECT rd.cnofact, rd.cprovid
                FROM revdet rd
                INNER JOIN fact f2 ON f2.idfactura = rd.idfactura
                WHERE f2.cref_agrupada = @cref
                GROUP BY rd.cnofact, rd.cprovid
            ) prov_map ON prov_map.cnofact = f.cnofact
            LEFT JOIN provs p
                ON p.cidprov = prov_map.cprovid
                AND p.inactivo = 0
            WHERE f.cref_agrupada = @cref
            -- FIX 2026-09-10: antes se agrupaba tambien por p.cnombre. Si una
            -- factura tiene partidas de mas de un proveedor, prov_map regresa
            -- varias filas para el mismo cnofact y ese GROUP BY las dejaba
            -- pasar como facturas duplicadas en la respuesta. Android usa
            -- Cnofact como key unica del LazyColumn, crash en produccion
            -- (IllegalArgumentException: Key ya usada). Agrupar
            -- solo por f.cnofact garantiza una fila por factura siempre;
            -- los proveedores multiples se listan juntos con GROUP_CONCAT.
            GROUP BY f.cnofact
            ORDER BY f.cnofact";
        return await conn.QueryAsync<FacturaRevision>(sql, new { cref = cref_agrupada });
    }

    public async Task<FacturaRevision?> GetRevisionFacturaAsync(
        string cnofact,
        string? crefAgrupada = null,
        string? usuario = null)
    {
        using IDbConnection conn = _db.CreateConnection();
        string? entrada = await ResolverEntradaDeFacturaAsync(conn, cnofact, crefAgrupada, usuario);
        if (entrada == null)
        {
            return null;
        }

        string sql = @"
            SELECT
                f.creferencia,
                f.cref_agrupada        AS CrefAgrupada,
                TRIM(f.cnofact) AS Cnofact,
                DATE_FORMAT(f.dfecha, '%d/%m/%Y') AS dFecha,
                COALESCE(f.cpedido, '')        AS Cpedido,
                COALESCE(f.bodegafact, '')     AS Bodegafact,
                f.cidfacturo,
                f.cclienteid            AS Cclienteid,
                cl.cnombre               AS NombreCliente,
                p.cnombre                AS NombreProveedor,
                prov_lookup.cprovid      AS Cprovid,
                COALESCE(f.npesolbs, 0)        AS nPesoLbs,
                COALESCE(f.npesokgs, 0)        AS nPesoKgs,
                COALESCE(f.totbultos, 0)       AS TotBultos,
                COALESCE(f.tipoop, '')         AS Tipoop,
                COALESCE(f.marcas, '')         AS Marcas,
                COALESCE(f.numeros, '')        AS Numeros,
                COALESCE(f.obsrev, '')         AS Obsrev,
                COALESCE(f.discrepancias, '')  AS Discrepancias,
                COALESCE(f.usurev, '')         AS Usurev,
                COALESCE(f.status_rev, 0)      AS StatusRev,
                COALESCE(f.totbultosdoc, 0)    AS TotBultosDoc,
                COALESCE(f.usumodi, '')        AS Usumodi,
                DATE_FORMAT(f.fmodi, '%d/%m/%Y %H:%i') AS fModi,
                COALESCE(f.obsrev2, '')        AS Obsrev2,
                -- RelacionCompleta: ahora en un solo LEFT JOIN con subselect mínimo.
                -- Si la subquery no encuentra filas, la condición NOT EXISTS es TRUE,
                -- pero la primera EXISTS (necesaria para que tenga sentido) la cambiamos
                -- por un COUNT en la misma subquery. Aquí lo dejamos con doble EXISTS
                -- porque EXISTS es muy eficiente cuando el WHERE usa índice (CREF_ORIGEN).
                (CASE WHEN rel_count.total > 0 AND rel_count.pendientes = 0 THEN 1 ELSE 0 END) AS RelacionCompleta,
                COALESCE(f.usuedit, '')        AS UsuEdit
            FROM fact f
            LEFT JOIN clientes cl
                ON cl.cidcte = f.cclienteid
                AND cl.inactivo = 0
            -- Resolver proveedor en 1 solo lookup: cogemos el cprovid de la primera
            -- partida de la factura (subquery que se cachea como derived table).
            -- Las partidas son las de ESTA factura (revdet.idfactura), no las de cualquier factura con el mismo número.
            LEFT JOIN (
                SELECT rd0.idfactura, rd0.cprovid
                FROM revdet rd0
                WHERE rd0.idfactura IN (
                    SELECT idfactura FROM fact WHERE cnofact = @cnofact AND cref_agrupada = @entrada)
                ORDER BY rd0.idrevdet ASC
                LIMIT 1
            ) prov_lookup ON prov_lookup.idfactura = f.idfactura
            LEFT JOIN provs p
                ON p.cidprov = prov_lookup.cprovid
                AND p.inactivo = 0
            -- Conteo de relaciones pendientes en una sola pasada.
            LEFT JOIN (
                SELECT
                    tf2.CREF_ORIGEN AS cref_origen,
                    COUNT(*) AS total,
                    SUM(CASE WHEN COALESCE(r.STATUS, '') != 'COMPLETA' THEN 1 ELSE 0 END) AS pendientes
                FROM traf2 tf2
                LEFT JOIN relacion r ON tf2.nrelacion = r.relno
                WHERE tf2.CREF_ORIGEN = @entrada
                GROUP BY tf2.CREF_ORIGEN
            ) rel_count ON rel_count.cref_origen = f.cref_agrupada
            WHERE f.cnofact = @cnofact AND f.cref_agrupada = @entrada
            ORDER BY
                CASE WHEN TRIM(COALESCE(f.usurev, '')) != '' THEN 0 ELSE 1 END,
                f.fmodi DESC
            LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<FacturaRevision>(sql, new { cnofact, entrada });
    }

    public async Task<IEnumerable<PartidaRevision>> GetPartidasEntradaAsync(string cref_agrupada)
    {
        using IDbConnection conn = _db.CreateConnection();
        string optionalColumns = await GetOptionalLabelColumnsAsync(conn);
        string sql = $@"
            SELECT
                rd.idrevdet   AS IdRevdet,
                TRIM(rd.cnofact) AS Cnofact,
                rd.cnoparte   AS CnoParte,
                COALESCE(rd.cdescripcion, '') AS CDescripcion,
                rd.marca,
                rd.modelo,
                rd.numser,
                COALESCE(rd.numlote, '') AS NumLote,
                COALESCE(rd.ncantfactura, 0)   AS NCantFactura,
                rd.ncantout,
                rd.cpaisorigen,
                COALESCE(rd.cpt_cve_tmateria, '') AS CptCveTmateria,
                COALESCE(rd.cpt_desc_tmateria, '') AS CptDescTmateria,
                COALESCE(rd.tipo_material, '')    AS TipoMaterial,
                rd.revisada,
                COALESCE(rd.obsrevpartida, '') AS Obsrevpartida,
                COALESCE(rd.mnotapartida, '')  AS Mnotapartida,
                COALESCE(rd.npeso, 0)          AS NPeso,
                COALESCE(rd.cantumc, 0)        AS CantidadComercial,
                COALESCE(rd.umcint, '')        AS UnidadComercial,
                COALESCE(rd.cunidadfactura, '') AS UnidadFactura,
                COALESCE(rd.ncanttarifa, 0)    AS CantidadTarifa,
                COALESCE(rd.cunidadtarifa, '') AS UnidadTarifa,
                COALESCE(rd.npesounitario, 0)  AS PesoUnitario,
                COALESCE(rd.valuni, 0)          AS ValorUnitario,
                COALESCE(rd.nvaloragregado, 0)  AS ValorAgregado,
                COALESCE(rd.nvalorpartidame, 0) AS ValorPartidaMe,
                COALESCE(f.nfactorequiv, 0)     AS FactorEquivalencia,
                COALESCE(rd.cclienteid, '')    AS Cclienteid,
                {optionalColumns}
            FROM fact f
            INNER JOIN revdet rd ON rd.idfactura = f.idfactura
            WHERE f.cref_agrupada = @cref
            ORDER BY f.cnofact, rd.idrevdet";
        return await conn.QueryAsync<PartidaRevision>(sql, new { cref = cref_agrupada });
    }

    /// <summary>
    /// Devuelve el tiempo acumulado de las sesiones cerradas y válidas de una
    /// entrada. Se comparte la misma regla que usan el cronómetro, el monitor
    /// y los reportes para que el detalle histórico no muestre cero ni vuelva
    /// a sumar sesiones inválidas.
    /// </summary>
    public async Task<long> GetSegundosAcumuladosAsync(string crefAgrupada)
    {
        using IDbConnection conn = _db.CreateConnection();
        string sql = @"
            SELECT COALESCE(SUM(TIMESTAMPDIFF(SECOND, finiciorevision, ffinrevision)), 0)
            FROM traftiemposrevision
            WHERE creferencia = @cref
              AND " + RevisionTimeRules.ClosedSessionPredicate();

        return await conn.ExecuteScalarAsync<long>(sql, new { cref = crefAgrupada });
    }

    /// <summary>
    /// Catálogo de UMC equivalente a Combo18/unidades2 del formulario
    /// frmclasdet.SCT (el combo que alimenta RevDet.cUnidadFactura, es decir
    /// la "unidad comercial" visible): solo unidades activas para M3 o AMBOS.
    /// OJO: no es el mismo filtro que Combo17/unidades (COVE o AMBOS), que
    /// alimenta revdet.umcint -- son dos catálogos distintos derivados de la
    /// misma tabla "unidades" con sistema diferente. Usar el filtro de COVE
    /// aquí hacía que la lista mostrara unidades de aduana (BALE, KEG,
    /// PALETA...) que Combo18 nunca ofrece, y ocultaba nada práctico porque
    /// las unidades reales (KILO, LIBRAS, LITRO, GALON) son todas "AMBOS".
    /// </summary>
    public async Task<IEnumerable<UnidadComercial>> GetUnidadesComercialesAsync()
    {
        using IDbConnection conn = _db.CreateConnection();
        const string sql = @"
            SELECT
                TRIM(cidunidad) AS Codigo,
                TRIM(COALESCE(cnombre, cidunidad)) AS Nombre,
                TRIM(umcint) AS CodigoInterno
            FROM unidades
            WHERE inactivo = 0
              AND sistema IN ('M3', 'AMBOS')
              AND TRIM(COALESCE(cidunidad, '')) <> ''
              AND TRIM(COALESCE(umcint, '')) <> ''
            ORDER BY cnombre, cidunidad";

        return await conn.QueryAsync<UnidadComercial>(sql);
    }

    /// <summary>
    /// Replica el comportamiento de las dos listas del legacy en una sola
    /// operación: cantidad comercial (cantumc), UMC interna (umcint) y la
    /// unidad visible/origen (cunidadfactura) siempre quedan sincronizadas.
    /// </summary>
    public async Task<bool> GuardarCantidadComercialAsync(
        int id,
        decimal cantidad,
        string unidad,
        string usuario)
    {
        if (id <= 0)
        {
            throw new ArgumentException("La partida no es válida.", nameof(id));
        }

        if (cantidad < 0)
        {
            throw new ArgumentException("La cantidad comercial no puede ser negativa.", nameof(cantidad));
        }

        string codigoSolicitado = unidad.Trim();
        if (string.IsNullOrWhiteSpace(codigoSolicitado))
        {
            throw new ArgumentException("Selecciona una unidad comercial.", nameof(unidad));
        }

        using IDbConnection conn = _db.CreateConnection();
        UnidadComercial? unidadCatalogo = await conn.QueryFirstOrDefaultAsync<UnidadComercial>(@"
            SELECT
                TRIM(cidunidad) AS Codigo,
                TRIM(COALESCE(cnombre, cidunidad)) AS Nombre,
                TRIM(umcint) AS CodigoInterno
            FROM unidades
            WHERE inactivo = 0
              AND sistema IN ('M3', 'AMBOS')
              AND UPPER(TRIM(cidunidad)) = UPPER(TRIM(@unidad))
              AND TRIM(COALESCE(umcint, '')) <> ''
            LIMIT 1",
            new { unidad = codigoSolicitado });

        if (unidadCatalogo == null)
        {
            throw new ArgumentException(
                $"La unidad '{codigoSolicitado}' no está activa en el catálogo legacy de UMC.",
                nameof(unidad));
        }

        int rows = await conn.ExecuteAsync(@"
            UPDATE revdet
            SET cantumc = @cantidad,
                umcint = @codigoInterno,
                cunidadfactura = @codigo,
                usumodi = @usuario,
                fmodi = NOW()
            WHERE idrevdet = @id",
            new
            {
                id,
                cantidad,
                codigo = unidadCatalogo.Codigo,
                codigoInterno = unidadCatalogo.CodigoInterno,
                usuario
            });

        if (rows == 0)
        {
            // Con UseAffectedRows=true, MySQL devuelve cero cuando el dato ya
            // tenía exactamente esos valores. Eso es éxito, no una partida
            // inexistente; distinguimos ambos casos antes de responder error.
            int existe = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM revdet WHERE idrevdet = @id",
                new { id });
            if (existe == 0)
            {
                return false;
            }
        }

        if (rows > 0)
        {
            (string? cref, string? factura) = await ObtenerPartidaActividadAsync(conn, id);
            string detalle = $"Cantidad comercial actualizada: {cantidad:0.###} {unidadCatalogo.Codigo} (UMC {unidadCatalogo.CodigoInterno})";
            await _activityService.RegistrarAsync(
                cref ?? string.Empty,
                usuario,
                RevisionActivityTypes.CampoPartida,
                factura,
                id,
                detalle);
            if (!string.IsNullOrWhiteSpace(cref))
            {
                await _hub.Clients.All.SendAsync(
                    "onActividadRecibida",
                    cref,
                    usuario,
                    factura ?? string.Empty,
                    detalle);
                await NotificarPartidaActualizadaAsync(cref, id, usuario);
            }
        }

        return true;
    }

    public async Task<bool> GuardarCampoFacturaAsync(
        string cnofact,
        string campo,
        string valor,
        string usuario,
        string? crefAgrupada = null)
    {
        var columnMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["marcas"] = "marcas",
            ["numeros"] = "numeros",
            ["obsrev"] = "obsrev",
            ["obsrev2"] = "obsrev2",
            ["discrepancias"] = "discrepancias",
            ["status_rev"] = "status_rev",
            ["npesolbs"] = "npesolbs",
            ["npesokgs"] = "npesokgs",
            ["totbultos"] = "totbultos",
            ["totbultosdoc"] = "totbultosdoc"
        };

        if (!columnMap.TryGetValue(campo, out string? columna))
        {
            throw new ArgumentException("Campo no permitido", nameof(campo));
        }

        using IDbConnection conn = _db.CreateConnection();

        // El número de factura se repite entre entradas: todo UPDATE se acota a la entrada de la petición para no
        // tocar las facturas de otras entradas que comparten el número (ver RevisionFacturaAlcance).
        string? entrada = await ResolverEntradaDeFacturaAsync(conn, cnofact, crefAgrupada, usuario);
        if (entrada == null)
        {
            return false;
        }

        string colUsurev = (campo.ToLower() == "status_rev") ? ", usurev = @usuario" : "";
        const string soloEstaFactura = "WHERE cnofact = @cnofact AND cref_agrupada = @entrada";

        // Replica la validación del formulario VFP original:
        // - libras siempre recalcula kg con ROUND(lb * .4536, 3);
        // - kilos solo calcula libras cuando el valor anterior de libras era
        //   cero, tal como txtnPesoKgs.Valid en frmopera_factura.SCX.
        // Ambos cambios quedan en una sola sentencia atómica.
        object parameters;
        string sql;
        if (columna.Equals("obsrev", StringComparison.OrdinalIgnoreCase))
        {
            string? observacionApp = CrearObservacionApp(valor);
            parameters = new { valor, observacionApp, usuario, cnofact, entrada };
            sql = $"UPDATE fact SET obsrev = @valor, {Obsrev2DesdeAppSql}, usumodi = @usuario, fmodi = NOW() {soloEstaFactura}";
        }
        else if (columna.Equals("npesolbs", StringComparison.OrdinalIgnoreCase) &&
            decimal.TryParse(valor, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal libras) &&
            libras != 0)
        {
            decimal kilos = Math.Round(libras * 0.4536m, 3);
            parameters = new { libras, kilos, usuario, cnofact, entrada };
            sql = $"UPDATE fact SET npesolbs = @libras, npesokgs = @kilos{colUsurev}, usumodi = @usuario, fmodi = NOW() {soloEstaFactura}";
        }
        else if (columna.Equals("npesokgs", StringComparison.OrdinalIgnoreCase) &&
            decimal.TryParse(valor, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal kilosCapturados) &&
            kilosCapturados != 0)
        {
            decimal librasCalculadas = Math.Round(kilosCapturados / 0.4536m, 3);
            parameters = new { valor = kilosCapturados, librasCalculadas, usuario, cnofact, entrada };
            sql = $"UPDATE fact SET npesokgs = @valor, npesolbs = IF(COALESCE(npesolbs, 0) = 0 AND @valor <> 0, @librasCalculadas, npesolbs){colUsurev}, usumodi = @usuario, fmodi = NOW() {soloEstaFactura}";
        }
        else
        {
            parameters = new { valor, usuario, cnofact, entrada };
            sql = $"UPDATE fact SET {columna} = @valor{colUsurev}, usumodi = @usuario, fmodi = NOW() {soloEstaFactura}";
        }

        int rows = await conn.ExecuteAsync(sql, parameters);
        long? activityEventId = null;

        if (rows > 0)
        {
            // La entrada ya es conocida: antes se buscaba con "WHERE cnofact = ... LIMIT 1", que con un número
            // repetido devolvía la entrada de OTRA factura (o vacía) y el evento se perdía en silencio.
            string tipoActividad = campo.Equals("status_rev", StringComparison.OrdinalIgnoreCase)
                ? valor == "1" ? RevisionActivityTypes.FacturaCompletada : valor == "2" ? RevisionActivityTypes.FacturaDiscrepancia : RevisionActivityTypes.CampoFactura
                : RevisionActivityTypes.CampoFactura;
            activityEventId = await _activityService.RegistrarAsync(
                entrada,
                usuario,
                tipoActividad,
                cnofact,
                detalle: $"Campo actualizado: {campo}");
            await _hub.Clients.All.SendAsync(
                "onActividadRecibida",
                entrada,
                usuario,
                cnofact,
                $"Campo actualizado: {campo}");
            await NotificarFacturaActualizadaAsync(cnofact, usuario, entrada);
        }

        if (rows > 0 && campo.ToLower() == "status_rev" && valor == "2")
        {
            dynamic data = await conn.QueryFirstOrDefaultAsync<object>(
                "SELECT cref_agrupada, usurev, discrepancias FROM fact WHERE cnofact = @cnofact AND cref_agrupada = @entrada LIMIT 1",
                new { cnofact, entrada });
            if (data != null)
            {
                string cref = (string)data.cref_agrupada;
                string usu = (string)data.usurev;
                string mot = (string)data.discrepancias;
                await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync("onDiscrepanciaReportada", cref, usu, cnofact, mot);
                await _monitorTvRealtimePublisher.DiscrepanciaReportadaAsync(cref, usu, cnofact, mot, eventId: activityEventId);
            }
        }

        return rows > 0;
    }

    /// <summary>Observación general de TODA la revisión (traf3.observacion_revision) --
    /// una sola nota por entrada, no por factura (para eso ya existe fact.obsrev).</summary>
    public async Task<string> GetObservacionRevisionAsync(string crefAgrupada)
    {
        using IDbConnection conn = _db.CreateConnection();
        string? valor = await conn.ExecuteScalarAsync<string?>(
            "SELECT observacion_revision FROM traf3 WHERE creferencia = @cref LIMIT 1",
            new { cref = crefAgrupada });
        return valor ?? string.Empty;
    }

    public async Task<bool> GuardarObservacionRevisionAsync(string crefAgrupada, string valor, string usuario)
    {
        using IDbConnection conn = _db.CreateConnection();
        if (conn.State != ConnectionState.Open)
        {
            conn.Open();
        }

        int rows;
        using (IDbTransaction transaction = conn.BeginTransaction())
        {
            try
            {
                rows = await conn.ExecuteAsync(
                    "UPDATE traf3 SET observacion_revision = @valor WHERE creferencia = @cref",
                    new { valor = NullIfEmpty(valor), cref = crefAgrupada },
                    transaction);

                int existe = rows > 0
                    ? 1
                    : await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(1) FROM traf3 WHERE creferencia = @cref",
                        new { cref = crefAgrupada },
                        transaction);

                if (existe == 0)
                {
                    transaction.Rollback();
                    return false;
                }

                string? observacionApp = CrearObservacionApp(valor);
                if (observacionApp != null)
                {
                    // Es una observación por entrada: se guarda en una sola factura
                    // para que el GROUP_CONCAT del reporte no la repita por factura.
                    string? cnofact = await conn.QueryFirstOrDefaultAsync<string>(
                        "SELECT cnofact FROM fact WHERE cref_agrupada = @cref ORDER BY cnofact LIMIT 1",
                        new { cref = crefAgrupada },
                        transaction);

                    if (!string.IsNullOrWhiteSpace(cnofact))
                    {
                        // Acotado a la entrada: el mismo número de factura existe en otras entradas.
                        await conn.ExecuteAsync(
                            $"UPDATE fact SET {Obsrev2DesdeAppSql}, usumodi = @usuario, fmodi = NOW() WHERE cnofact = @cnofact AND cref_agrupada = @cref",
                            new { observacionApp, usuario, cnofact, cref = crefAgrupada },
                            transaction);
                    }
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        if (rows > 0)
        {
            await _activityService.RegistrarAsync(
                crefAgrupada,
                usuario,
                RevisionActivityTypes.ObservacionGeneral,
                detalle: "Observación general actualizada");
            await _hub.Clients.All.SendAsync(
                "onActividadRecibida", crefAgrupada, usuario, "", "Observación general actualizada");
            await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync(
                "onRevisionActualizada",
                new WarehouseHub.RevisionActualizadaEvent
                {
                    Entidad = "ENTRADA",
                    Creferencia = crefAgrupada,
                    Usuario = usuario,
                    Fecha = DateTime.UtcNow.ToString("O"),
                    ObservacionRevision = valor
                });
            return true;
        }

        return true;
    }

    private static string? CrearObservacionApp(string valor)
    {
        return string.IsNullOrWhiteSpace(valor)
            ? null
            : $"Obs. desde App: {valor.Trim()}";
    }

    /// <summary>
    /// Guarda los tres datos del rombo de mercancía peligrosa SEPARADOS.
    ///
    /// Hasta ahora no había forma de escribirlos desde Revisión, y en los datos se
    /// ve el resultado: hazmat_un (varchar(15)) acaba cargando UN + clase + grupo
    /// revueltos -- '1866 3,III', 'UN1866', '(6.1) 2922,ii' -- y 75 filas ya topan
    /// el límite de la columna, truncadas a media palabra ('1268 3 2491 8 P').
    /// Por eso 1,781 de 3,015 comparaciones contra revdet.cpt_cve_hazmat no empatan:
    /// no es que el número esté mal, es que no había dónde guardarlo aparte.
    ///
    /// Requiere la columna traf3.hazmat_grupo (ver sql/2026-10-14-hazmat-grupo.sql).
    /// </summary>
    public async Task<bool> GuardarHazmatRevisionAsync(
        string crefAgrupada,
        string? un,
        string? clase,
        string? grupo,
        string usuario)
    {
        LabelCaptureRules.ValidateHazmat(un, clase, grupo);
        if (un is null && clase is null && grupo is null) return true;
        using MySqlConnection conn = (MySqlConnection)_db.CreateConnection();
        await conn.OpenAsync();
        if (clase is not null && await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM clase_hazmat WHERE clase_hazmat=@clase AND inactivo=0",
            new { clase = clase.Trim() }) == 0)
            throw new ArgumentException("La clase Hazmat no existe en el catálogo activo.");

        var parametros = new
        {
            cref = crefAgrupada,
            un = un is null ? null : LabelCaptureRules.NormalizeUn(un),
            clase = NullIfEmpty(clase),
            grupo = NullIfEmpty(grupo)?.ToUpperInvariant()
        };

        string lockName = $"hazmat_{crefAgrupada.Trim()}";
        if (await conn.ExecuteScalarAsync<int>("SELECT GET_LOCK(@lockName,10)", new { lockName }) != 1)
            throw new InvalidOperationException("Los datos Hazmat se están actualizando. Reintenta en unos segundos.");
        try
        {
        // Se cuenta primero en vez de confiar en las filas afectadas del UPDATE:
        // con UseAffectedRows=true, reescribir el mismo valor devuelve 0 y
        // acabaríamos insertando una fila duplicada.
        int existentes = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM traf3 WHERE creferencia = @cref",
            new { cref = crefAgrupada });

        if (existentes > 0)
        {
            // creferencia NO es única (el índice CREF es NON_UNIQUE) y hay 5
            // referencias con más de una fila: se actualizan todas para no dejar
            // una con el dato y otra sin él.
            await conn.ExecuteAsync(
                @"UPDATE traf3
                     SET hazmat_un    = COALESCE(@un, hazmat_un),
                         hazmat_clase = COALESCE(@clase, hazmat_clase),
                         hazmat_grupo = COALESCE(@grupo, hazmat_grupo)
                   WHERE creferencia  = @cref",
                parametros);
        }
        else
        {
            // Las revisiones que nunca pasaron por Entrada Bodega no tienen fila en
            // traf3. fk_idtraf se resuelve desde traf; si tampoco está, queda en 0,
            // que es como la dejan los flujos viejos.
            await conn.ExecuteAsync(
                @"INSERT INTO traf3 (fk_idtraf, creferencia, hazmat_un, hazmat_clase, hazmat_grupo)
                  SELECT COALESCE((SELECT idtraf FROM traf WHERE creferencia = @cref LIMIT 1), 0),
                         @cref, @un, @clase, @grupo",
                parametros);
        }

        }
        finally
        {
            await conn.ExecuteAsync("SELECT RELEASE_LOCK(@lockName)", new { lockName });
        }
        string detalle = $"HazMat registrado: UN {parametros.un ?? "sin cambio"}, clase {parametros.clase ?? "sin cambio"}, grupo {parametros.grupo ?? "sin cambio"}";
        await _activityService.RegistrarAsync(
            crefAgrupada,
            usuario,
            RevisionActivityTypes.HazmatRegistrado,
            detalle: detalle);
        await _hub.Clients.All.SendAsync(
            "onActividadRecibida", crefAgrupada, usuario, "", detalle);

        return true;
    }

    private static string? NullIfEmpty(string? valor) =>
        string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private static readonly HashSet<string> ClientesPesoLibras = new(StringComparer.OrdinalIgnoreCase)
    {
        "2944", "CL7168", "CL7480", "CL7481", "CL7482"
    };

    public async Task<bool> GuardarCampoProductoAsync(int id, string campo, string valor, string usuario)
    {
        using IDbConnection conn = _db.CreateConnection();

        if (campo.Equals("npesolibras", StringComparison.OrdinalIgnoreCase))
        {
            if (!decimal.TryParse(valor, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal libras))
            {
                return false;
            }

            dynamic? datos = await conn.QueryFirstOrDefaultAsync<object>(
                "SELECT cclienteid, cunidadtarifa, ncanttarifa FROM revdet WHERE idrevdet = @id",
                new { id });

            if (datos == null)
            {
                return false;
            }

            string cliente = (string?)datos.cclienteid ?? "";
            string umt = ((string?)datos.cunidadtarifa ?? "").Trim();
            decimal ncanttarifaActual = (decimal)datos.ncanttarifa;

            if (ClientesPesoLibras.Contains(cliente) &&
                umt.Equals("KG", StringComparison.OrdinalIgnoreCase) &&
                ncanttarifaActual == 0)
            {
                decimal ncanttarifaNueva = Math.Round(libras * 0.4536m, 3);
                await conn.ExecuteAsync(
                    "UPDATE revdet SET ncanttarifa = @valor, usumodi = @usuario, fmodi = NOW() WHERE idrevdet = @id",
                    new { valor = ncanttarifaNueva, usuario, id });
            }

            (string? cref, string? factura) = await ObtenerPartidaActividadAsync(conn, id);
            await _activityService.RegistrarAsync(
                cref ?? string.Empty,
                usuario,
                RevisionActivityTypes.CampoPartida,
                factura,
                id,
                "Campo actualizado: npesolibras");
            if (!string.IsNullOrWhiteSpace(cref))
            {
                await _hub.Clients.All.SendAsync("onActividadRecibida", cref, usuario, factura ?? string.Empty, "Campo actualizado: npesolibras");
                await NotificarPartidaActualizadaAsync(cref, id, usuario);
            }
            return true;
        }

        var columnMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["marca"] = "marca",
            ["modelo"] = "modelo",
            ["numser"] = "numser",
            ["numlote"] = "numlote",
            ["cpaisorigen"] = "cpaisorigen",
            ["ncantfactura"] = "ncantfactura",
            ["ncantout"] = "ncantout",
            ["revisada"] = "revisada",
            ["cdescripcion"] = "cdescripcion",
            ["obsrevpartida"] = "obsrevpartida",
            ["mnotapartida"] = "mnotapartida",
            ["npeso"] = "npeso",
            ["cpt_cve_tmateria"] = "cpt_cve_tmateria",
            ["cpt_desc_tmateria"] = "cpt_desc_tmateria",
            ["tipo_material"] = "tipo_material"
        };

        if (!columnMap.TryGetValue(campo, out string? columna))
        {
            throw new ArgumentException("Campo no permitido", nameof(campo));
        }

        string sql = $"UPDATE revdet SET {columna} = @valor, usumodi = @usuario, fmodi = NOW() WHERE idrevdet = @id";
        int rows = await conn.ExecuteAsync(sql, new { valor, usuario, id });


        // Cada vez que cambia el peso de la partida (Peso lb o Peso kg: la pantalla manda siempre libras), la cantidad
        // tarifa se actualiza con el peso que corresponde a la UMT elegida (KG -> kilos, LB -> libras). Antes solo se
        // llenaba cuando ncanttarifa estaba en 0 y copiaba las libras tal cual aunque la UMT fuera KG. Con una UMT que
        // no es de peso (litros, piezas...) no se toca. Ver RevisionPesoTarifa.
        if (rows > 0 && campo.Equals("npeso", StringComparison.OrdinalIgnoreCase) &&
            decimal.TryParse(valor, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal pesoLibras))
        {
            string? umt = await conn.ExecuteScalarAsync<string?>(
                "SELECT cunidadtarifa FROM revdet WHERE idrevdet = @id",
                new { id });

            decimal? cantidadTarifa = RevisionPesoTarifa.CantidadTarifaPorPeso(umt, pesoLibras);
            if (cantidadTarifa.HasValue)
            {
                await conn.ExecuteAsync(
                    "UPDATE revdet SET ncanttarifa = @valor, usumodi = @usuario, fmodi = NOW() WHERE idrevdet = @id",
                    new { valor = cantidadTarifa.Value, usuario, id });
            }
        }

        if (rows > 0)
        {
            (string? cref, string? factura) = await ObtenerPartidaActividadAsync(conn, id);
            string tipoActividad = campo.Equals("revisada", StringComparison.OrdinalIgnoreCase)
                ? valor == "1" ? RevisionActivityTypes.PartidaRevisada : RevisionActivityTypes.CampoPartida
                : RevisionActivityTypes.CampoPartida;
            string detalle = campo.Equals("revisada", StringComparison.OrdinalIgnoreCase)
                ? valor == "1" ? "Partida revisada" : "Partida reabierta"
                : $"Campo actualizado: {campo}";
            await _activityService.RegistrarAsync(cref ?? string.Empty, usuario, tipoActividad, factura, id, detalle);
            if (!string.IsNullOrWhiteSpace(cref))
            {
                await _hub.Clients.All.SendAsync("onActividadRecibida", cref, usuario, factura ?? string.Empty, detalle);
                await NotificarPartidaActualizadaAsync(cref, id, usuario);
            }
        }

        return rows > 0;
    }

    public async Task<int> AgregarProductoAsync(string cnofact, string cref, string usuario)
    {
        using IDbConnection conn = _db.CreateConnection();
        conn.Open();

        // La partida nueva cuelga de la factura de ESTA entrada. Antes tomaba el idfactura de "la primera factura con
        // ese número", que con un número repetido era la de otra entrada: la partida quedaba pegada a una factura
        // ajena y el sistema de escritorio (que carga las partidas por idfactura) nunca la mostraba.
        string entrada = (cref ?? string.Empty).Trim();
        if (entrada.Length == 0)
        {
            return 0;
        }

        // Se mide el efecto del INSERT: LAST_INSERT_ID() devuelve el último id de la conexión, así que si el INSERT no
        // insertó nada (factura que no existe en esa entrada) devolvía un id viejo y la app creía tener la partida.
        int insertadas = await conn.ExecuteAsync(@"
            INSERT INTO revdet (cnofact, cref_agrupada, idfactura, cdescripcion, revisada, ncantfactura, ncantout, usucrea, fcrea)
            SELECT @cnofact, @entrada, idfactura, '— Nueva Partida —', 0, 0, 0, @usuario, NOW()
            FROM fact WHERE cnofact = @cnofact AND cref_agrupada = @entrada
            ORDER BY idfact DESC LIMIT 1",
            new { cnofact, entrada, usuario });
        int id = insertadas > 0 ? await conn.ExecuteScalarAsync<int>("SELECT LAST_INSERT_ID()") : 0;
        if (id > 0)
        {
            await _activityService.RegistrarAsync(
                cref,
                usuario,
                RevisionActivityTypes.PartidaAgregada,
                cnofact,
                id,
                "Partida agregada");
            await _hub.Clients.All.SendAsync("onActividadRecibida", cref, usuario, cnofact, "Partida agregada");
            await NotificarPartidaActualizadaAsync(cref, id, usuario);
        }
        return id;
    }

    public async Task<bool> EliminarProductoAsync(int id, string usuario)
    {
        using IDbConnection conn = _db.CreateConnection();
        (string? cref, string? factura) = await ObtenerPartidaActividadAsync(conn, id);
        string sql = "DELETE FROM revdet WHERE idrevdet = @id";
        int rows = await conn.ExecuteAsync(sql, new { id });
        if (rows > 0)
        {
            await _activityService.RegistrarAsync(
                cref ?? string.Empty,
                usuario,
                RevisionActivityTypes.PartidaEliminada,
                factura,
                id,
                "Partida eliminada");
            if (!string.IsNullOrWhiteSpace(cref))
            {
                await _hub.Clients.All.SendAsync("onActividadRecibida", cref, usuario, factura ?? string.Empty, "Partida eliminada");
                await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync(
                    "onRevisionActualizada",
                    new WarehouseHub.RevisionActualizadaEvent
                    {
                        Entidad = "PARTIDA_ELIMINADA",
                        Creferencia = cref,
                        Usuario = usuario,
                        Fecha = DateTime.UtcNow.ToString("O"),
                        Cnofact = factura,
                        IdRevdet = id
                    });
            }
        }
        return rows > 0;
    }

    public async Task<bool> AclararDiscrepanciasAsync(string cnofact, string usuario, string? crefAgrupada = null)
    {
        using IDbConnection conn = _db.CreateConnection();
        string? entrada = await ResolverEntradaDeFacturaAsync(conn, cnofact, crefAgrupada, usuario);
        if (entrada == null)
        {
            return false;
        }

        string sql = @"
            UPDATE fact
            SET obsrev2 = IF(TRIM(COALESCE(obsrev2,'')) = '',
                             CONCAT(COALESCE(discrepancias,''), ' AUTORIZO: ', @usuario),
                             CONCAT(TRIM(obsrev2), ' | ', COALESCE(discrepancias,''), ' AUTORIZO: ', @usuario)),
                discrepancias = '',
                usumodi = @usuario,
                fmodi = NOW()
            WHERE cnofact = @cnofact AND cref_agrupada = @entrada";
        int rows = await conn.ExecuteAsync(sql, new { usuario, cnofact, entrada });
        if (rows > 0)
        {
            await _activityService.RegistrarAsync(
                entrada,
                usuario,
                RevisionActivityTypes.DiscrepanciasAclaradas,
                cnofact,
                detalle: "Discrepancias aclaradas");
            await _hub.Clients.All.SendAsync("onActividadRecibida", entrada, usuario, cnofact, "Discrepancias aclaradas");
            await NotificarFacturaActualizadaAsync(cnofact, usuario, entrada);
        }
        return rows > 0;
    }

    public async Task<IEnumerable<dynamic>> ValidarHojasSeguridadAsync(
        string cnofact,
        string? crefAgrupada = null,
        string? usuario = null)
    {
        using IDbConnection conn = _db.CreateConnection();
        string? entrada = await ResolverEntradaDeFacturaAsync(conn, cnofact, crefAgrupada, usuario);
        if (entrada == null)
        {
            return new List<object>();
        }

        // Partidas de la factura de esta entrada (revdet.idfactura), no de cualquier factura con ese número.
        string sqlPartes = @"
            SELECT rd.idrevdet, rd.cnoparte, rd.lhazmat, p.idparte
            FROM revdet rd
            LEFT JOIN products p ON rd.cclienteid = p.cclienteid
                                AND rd.cprovid = p.cprovid
                                AND rd.cnoparte = p.cidproduct
                                AND p.lconstancia = 1
            WHERE rd.idfactura IN (SELECT idfactura FROM fact WHERE cnofact = @cnofact AND cref_agrupada = @entrada)
              AND (rd.lhazmat = 'S' OR rd.lhazmat = 1)";

        IEnumerable<dynamic> partes = await conn.QueryAsync<object>(sqlPartes, new { cnofact, entrada });
        List<object> problemList = new List<object>();

        foreach (dynamic p in partes)
        {
            if (p.idparte == null)
            {
                problemList.Add(new
                {
                    cnoparte = (object)p.cnoparte,
                    problema = "Producto no encontrado en el catálogo de seguridad"
                });
                continue;
            }

            string sqlDocs = "SELECT ntipodocto FROM documentos_partes WHERE id_parte = @idParte AND ntipodocto IN (800,801)";
            IEnumerable<int> docs = await conn.QueryAsync<int>(sqlDocs, new
            {
                idParte = (object)p.idparte
            });

            if (!docs.Contains(800))
            {
                problemList.Add(new
                {
                    cnoparte = (object)p.cnoparte,
                    problema = "Falta Hoja de Seguridad en Inglés (800)"
                });
            }
            if (!docs.Contains(801))
            {
                problemList.Add(new
                {
                    cnoparte = (object)p.cnoparte,
                    problema = "Falta Hoja de Seguridad en Español (801)"
                });
            }
        }

        return problemList;
    }

    public async Task<IEnumerable<CatalogoItem>> GetPaisesAsync()
    {
        using IDbConnection conn = _db.CreateConnection();
        string sql = "SELECT Codigo, Descrip FROM view_catalogo3 WHERE UPPER(Tabla) = 'PAISES' ORDER BY Descrip";
        return await conn.QueryAsync<CatalogoItem>(sql);
    }

    public async Task<IEnumerable<PaisFrecuente>> GetPaisesFrecuentesAsync()
    {
        using IDbConnection conn = _db.CreateConnection();
        const string topSql = @"
            SELECT
                TRIM(rd.cpaisorigen) AS Codigo,
                COUNT(*) AS Usos
            FROM revdet rd
            WHERE rd.cpaisorigen IS NOT NULL
              AND rd.cpaisorigen <> ''
            GROUP BY rd.cpaisorigen
            ORDER BY Usos DESC
            LIMIT 5";
        var frecuentes = (await conn.QueryAsync<PaisFrecuente>(topSql)).ToList();
        if (frecuentes.Count == 0)
        {
            return frecuentes;
        }

        const string paisesSql = @"
            SELECT Codigo, Descrip
            FROM view_catalogo3
            WHERE UPPER(Tabla) = 'PAISES'";
        var nombres = (await conn.QueryAsync<CatalogoItem>(paisesSql))
            .Where(item => !string.IsNullOrWhiteSpace(item.Codigo))
            .GroupBy(item => item.Codigo.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Descrip, StringComparer.OrdinalIgnoreCase);

        foreach (var frecuente in frecuentes)
        {
            frecuente.Descrip = nombres.TryGetValue(frecuente.Codigo.Trim(), out string? descrip)
                ? descrip
                : frecuente.Codigo.Trim();
        }

        return frecuentes;
    }

    public async Task<bool> EsRelacionCerradaAsync(string crefAgrupada, IDbConnection? conn = null)
    {
        bool disponerConexion = conn == null;
        IDbConnection connection = conn ?? _db.CreateConnection();
        try
        {
            bool? relacionCerrada = await connection.QueryFirstOrDefaultAsync<bool?>(@"
                SELECT CASE WHEN COUNT(*) > 0 AND SUM(CASE WHEN COALESCE(r.STATUS, '') != 'COMPLETA' THEN 1 ELSE 0 END) = 0
                            THEN 1 ELSE 0 END
                FROM traf2 tf2
                LEFT JOIN relacion r ON tf2.nrelacion = r.relno
                WHERE tf2.CREF_ORIGEN = @cref", new { cref = crefAgrupada });
            return relacionCerrada == true;
        }
        finally
        {
            if (disponerConexion) connection.Dispose();
        }
    }

    public async Task<(bool Success, string Message, int Id)> SolicitarDesbloqueoAsync(string cnofact, string motivo, string usuario)
    {
        using IDbConnection conn = _db.CreateConnection();

        // Una solicitud a NIVEL ENTRADA (cnofact = "ENTRY_{cref_agrupada}") nunca
        // debe aceptarse si la relación asociada ya se cerró. Antes esto solo
        // se filtraba en el cliente (y ni siquiera ahí, a nivel entrada)
        // dejando pasar cualquier solicitud ENTRY_ al backend sin validar nada.
        if (cnofact.StartsWith("ENTRY_", StringComparison.OrdinalIgnoreCase))
        {
            string crefEntrada = cnofact.Substring("ENTRY_".Length);
            if (await EsRelacionCerradaAsync(crefEntrada, conn))
            {
                return (false, "No se puede solicitar edición: la relación de esta entrada ya fue cerrada.", 0);
            }
        }

        string sqlExpire = @"
            UPDATE rev_desbloqueos 
            SET estatus = 2, usuario_autoriza = 'SYSTEM_EXP', fecha_autorizacion = NOW() 
            WHERE estatus = 0 AND fecha_solicitud < DATE_SUB(NOW(), INTERVAL 24 HOUR)";
        await conn.ExecuteAsync(sqlExpire);

        conn.Open();
        using IDbTransaction transaction = conn.BeginTransaction();

        try
        {
            string sqlCheck = "SELECT COUNT(*) FROM rev_desbloqueos WHERE cnofact = @cnofact AND estatus = 0 FOR UPDATE";
            if (await conn.ExecuteScalarAsync<int>(sqlCheck, new { cnofact }, transaction) > 0)
            {
                transaction.Rollback();
                return (false, "Ya tienes una solicitud pendiente para esta factura", 0);
            }

            string sqlIns = "INSERT INTO rev_desbloqueos (cnofact, usuario_solicita, motivo, estatus) VALUES (@cnofact, @usuario, @motivo, 0); SELECT LAST_INSERT_ID();";
            int nuevoId = await conn.ExecuteScalarAsync<int>(sqlIns, new { cnofact, usuario, motivo }, transaction);

            transaction.Commit();
            try
            {
                await _hub.Clients.All.SendAsync("onNuevaSolicitud", cnofact, usuario, motivo, nuevoId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "La solicitud {Id} se guardó, pero no llegó el evento en vivo.", nuevoId);
            }
            return (true, string.Empty, nuevoId);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<object?> VerificarDesbloqueoAsync(string cnofact)
    {
        using IDbConnection conn = _db.CreateConnection();
        string sql = @"
            SELECT 
                usuario_autoriza AS AutorizadoPor, 
                DATE_FORMAT(fecha_expiracion, '%Y-%m-%dT%H:%i:%s') AS FechaExpiracion 
            FROM rev_desbloqueos 
            WHERE cnofact = @cnofact 
              AND estatus = 1 
              AND (fecha_expiracion IS NULL OR fecha_expiracion > NOW()) 
            ORDER BY id_desbloqueo DESC 
            LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<object>(sql, new { cnofact });
    }

    /// <summary>
    /// Cierra cualquier permiso de desbloqueo que siga vigente para esta
    /// entrada (a nivel "ENTRY_{cref}" o de alguna de sus facturas) cuando la
    /// revisión se vuelve a finalizar. Sin esto, un desbloqueo otorgado antes
    /// de terminar sobrevivía con su TTL de 30 min: seguía apareciendo en
    /// "Permisos abiertos ahora" del Monitor y, peor, VerificarDesbloqueoAsync
    /// lo seguía reportando como aprobado -- dejando la entrada editable en
    /// el cliente aunque status_rev ya hubiera vuelto a 1. Mismo criterio que
    /// RevocarPermiso (MonitorController): no se toca "estatus", solo se
    /// cierra la ventana de tiempo con fecha_expiracion = NOW().
    /// </summary>
    public async Task RevocarDesbloqueosDeEntradaAsync(string crefAgrupada)
    {
        using IDbConnection conn = _db.CreateConnection();
        string sqlWhere = @"
            estatus = 1
            AND (fecha_expiracion IS NULL OR fecha_expiracion > NOW())
            AND (cnofact = CONCAT('ENTRY_', @cref)
                 OR cnofact IN (SELECT cnofact FROM fact WHERE cref_agrupada = @cref))";

        IEnumerable<dynamic> activos = await conn.QueryAsync<dynamic>(
            $"SELECT cnofact, usuario_solicita FROM rev_desbloqueos WHERE {sqlWhere}",
            new { cref = crefAgrupada });
        var lista = activos.ToList();
        if (lista.Count == 0) return;

        await conn.ExecuteAsync(
            $"UPDATE rev_desbloqueos SET fecha_expiracion = NOW() WHERE {sqlWhere}",
            new { cref = crefAgrupada });

        foreach (dynamic permiso in lista)
        {
            string cnofact = (string)permiso.cnofact;
            await _hub.Clients.Group("USER_" + (string)permiso.usuario_solicita).SendAsync(
                "onDesbloqueoRechazado",
                new { cnofact, supervisor = "SYSTEM" });
        }
    }

    public async Task<IEnumerable<string>> ValidarEntradaAntesDeCerrarAsync(string creferencia)
    {
        using IDbConnection conn = _db.CreateConnection();
        string sql = @"
            SELECT cnofact 
            FROM (
                SELECT cnofact, MAX(status_rev) AS status_rev, MAX(NULLIF(TRIM(COALESCE(usurev, '')), '')) AS usurev 
                FROM fact 
                WHERE cref_agrupada = @cref 
                GROUP BY cnofact
            ) t 
            WHERE status_rev = 1 AND usurev IS NULL";
        return await conn.QueryAsync<string>(sql, new { cref = creferencia });
    }

    public async Task AsignarRevisoresPendientesAsync(string creferencia, string usuario)
    {
        using IDbConnection conn = _db.CreateConnection();
        var parameters = new { usuario, cref = creferencia };
        string sqlFacturasElegibles = @"
            SELECT TRIM(cnofact)
            FROM fact
            WHERE cref_agrupada = @cref
              AND status_rev = 1
              AND (usurev IS NULL OR TRIM(usurev) = '')
              AND cnofact NOT IN (
                  SELECT cnofact FROM (
                      SELECT cnofact FROM fact
                      WHERE cref_agrupada = @cref AND TRIM(COALESCE(usurev, '')) != ''
                  ) already_reviewed
              )";
        List<string> cnofacts = (await conn.QueryAsync<string>(sqlFacturasElegibles, parameters))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string sql = @"
            UPDATE fact 
            SET usurev = @usuario, usumodi = @usuario, fmodi = NOW() 
            WHERE cref_agrupada = @cref 
              AND status_rev = 1 
              AND (usurev IS NULL OR TRIM(usurev) = '') 
              AND cnofact NOT IN (
                  SELECT cnofact FROM (
                      SELECT cnofact FROM fact 
                      WHERE cref_agrupada = @cref AND TRIM(COALESCE(usurev, '')) != ''
                      ) already_reviewed
              )";
        int rows = await conn.ExecuteAsync(sql, parameters);
        if (rows == 0)
        {
            return;
        }

        foreach (string cnofact in cnofacts)
        {
            await NotificarFacturaActualizadaAsync(cnofact, usuario, creferencia);
        }
    }

    public async Task<bool> LimpiarUsurevAsync(string cnofact, string usuario, string? crefAgrupada = null)
    {
        using IDbConnection conn = _db.CreateConnection();
        string? entrada = await ResolverEntradaDeFacturaAsync(conn, cnofact, crefAgrupada, usuario);
        if (entrada == null)
        {
            return false;
        }

        string sql = "UPDATE fact SET usurev = '', usumodi = @usuario, fmodi = NOW() WHERE cnofact = @cnofact AND cref_agrupada = @entrada";
        int rows = await conn.ExecuteAsync(sql, new { usuario, cnofact, entrada });
        if (rows > 0)
        {
            await NotificarFacturaActualizadaAsync(cnofact, usuario, entrada);
        }
        return rows > 0;
    }

    public async Task<string> GetLugarUsuarioAsync(string usuario)
    {
        using IDbConnection conn = _db.CreateReportesConnection();
        string sql = "SELECT COALESCE(lugar, '') FROM usuarios WHERE claveusuario = @clave LIMIT 1";
        return (await conn.ExecuteScalarAsync<string>(sql, new { clave = usuario })) ?? "";
    }

    public async Task RegistrarTomaDocumentoAsync(string creferencia, string usuario)
    {
        // Sin referencia o sin usuario no hay "toma de documento" que registrar. traftiemposrevision tiene DEFAULT ''
        // y '0000-00-00' en casi todas sus columnas, así que un INSERT con datos vacíos deja una fila en blanco que
        // las consultas de pendientes cuentan como sesión abierta. Mejor fallar fuerte que guardar basura.
        if (string.IsNullOrWhiteSpace(creferencia) || string.IsNullOrWhiteSpace(usuario))
        {
            throw new ArgumentException("La toma de documentos requiere referencia y usuario.");
        }

        using IDbConnection conn = _db.CreateConnection();
        conn.Open();
        using IDbTransaction transaction = conn.BeginTransaction();

        try
        {
            string sqlCheck = @"
                SELECT idtiemposrevision, COALESCE(usuario, '') AS usuario, ftomadocumento
                FROM traftiemposrevision
                WHERE creferencia = @cref
                  AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                  AND (
                        (finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000)
                     OR (ftomadocumento IS NOT NULL AND YEAR(ftomadocumento) >= 2000)
                  )
                ORDER BY CASE
                             WHEN finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000
                             THEN finiciorevision
                             ELSE ftomadocumento
                         END DESC,
                         idtiemposrevision DESC
                LIMIT 1
                FOR UPDATE";

            dynamic? abierta = await conn.QueryFirstOrDefaultAsync(sqlCheck, new { cref = creferencia }, transaction);

            bool notificarToma = abierta == null;
            if (abierta == null)
            {
                string sqlIns = "INSERT INTO traftiemposrevision (creferencia, ftomadocumento, usuario, finiciorevision) VALUES (@cref, NOW(), @usuario, NULL)";
                await conn.ExecuteAsync(sqlIns, new { cref = creferencia, usuario }, transaction);
            }
            else
            {
                string usuarioAbierta = (string)abierta.usuario;
                int idAbierta = (int)abierta.idtiemposrevision;

                if (usuarioAbierta == usuario || string.IsNullOrEmpty(usuarioAbierta))
                {
                    // Misma persona (o fila sin dueño todavía): reutilizamos
                    // la fila, incluso si todavía no tiene cronómetro. Esto
                    // evita que la lista y el detalle creen sesiones paralelas.
                    string sqlUpd = "UPDATE traftiemposrevision SET ftomadocumento = NOW(), usuario = @usuario WHERE idtiemposrevision = @id";
                    await conn.ExecuteAsync(sqlUpd, new { usuario, id = idAbierta }, transaction);
                    notificarToma = abierta.ftomadocumento == null
                        || (abierta.ftomadocumento is DateTime fechaToma && fechaToma.Year < 2000);
                }
                else
                {
                    throw new RevisionEnUsoException(usuarioAbierta);
                }
            }

            transaction.Commit();
            if (notificarToma)
            {
                await _activityService.RegistrarAsync(
                    creferencia,
                    usuario,
                    RevisionActivityTypes.TomaDocumentos,
                    detalle: "Documentos tomados");
                await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync(
                    "onRevisionEstadoCambiado",
                    new
                    {
                        creferencia,
                        usuario,
                        estado = "DOCUMENTOS_TOMADOS",
                        fecha = DateTime.Now.ToString("o"),
                        estatusFinal = 0
                    });
                await _hub.Clients.All.SendAsync("onActividadRecibida", creferencia, usuario, "", "");
            }
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Entrada (cref_agrupada) a la que se refiere una petición que trae el número de factura. Si la app manda la
    /// entrada, esa es; si no (versiones anteriores), se deduce con <see cref="RevisionFacturaAlcance"/>. Nunca se
    /// escribe con solo el número: el mismo número existe en otras entradas.
    /// </summary>
    private async Task<string?> ResolverEntradaDeFacturaAsync(
        IDbConnection conn,
        string cnofact,
        string? crefIndicado,
        string? usuario)
    {
        if (!string.IsNullOrWhiteSpace(crefIndicado))
        {
            return crefIndicado.Trim();
        }

        List<string> candidatas = (await conn.QueryAsync<string>(@"
            SELECT TRIM(cref_agrupada)
            FROM fact
            WHERE cnofact = @cnofact AND TRIM(COALESCE(cref_agrupada, '')) <> ''
            GROUP BY TRIM(cref_agrupada)
            ORDER BY MAX(idfact) DESC",
            new { cnofact })).ToList();

        string? entradaActiva = null;
        if (candidatas.Count > 1 && !string.IsNullOrWhiteSpace(usuario))
        {
            entradaActiva = await conn.ExecuteScalarAsync<string?>(@"
                SELECT creferencia
                FROM traftiemposrevision
                WHERE usuario = @usuario
                ORDER BY idtiemposrevision DESC
                LIMIT 1",
                new { usuario });
        }

        string? entrada = RevisionFacturaAlcance.ElegirEntrada(null, candidatas, entradaActiva);
        if (candidatas.Count > 1)
        {
            _logger.LogWarning(
                "La factura {Cnofact} existe en {Candidatas} entradas y la petición de {Usuario} no indicó cuál; se usó {Entrada}. La app debe mandar crefAgrupada.",
                cnofact,
                candidatas.Count,
                usuario,
                entrada);
        }

        return entrada;
    }

    private static async Task<(string? Cref, string? Cnofact)> ObtenerPartidaActividadAsync(IDbConnection conn, int id)
    {
        dynamic? row = await conn.QueryFirstOrDefaultAsync<object>(@"
            SELECT f.cref_agrupada AS Cref, rd.cnofact AS Cnofact
            FROM revdet rd
            INNER JOIN fact f ON f.idfactura = rd.idfactura
            WHERE rd.idrevdet = @id
            LIMIT 1", new { id });

        return row == null
            ? (null, null)
            : ((string?)row.Cref, (string?)row.Cnofact);
    }

    public async Task<bool> RegistrarEditorFacturaAsync(string cnofact, string usuario, string? crefAgrupada = null)
    {
        using IDbConnection conn = _db.CreateConnection();
        string? entrada = await ResolverEntradaDeFacturaAsync(conn, cnofact, crefAgrupada, usuario);
        if (entrada == null)
        {
            return false;
        }

        string sql = "UPDATE fact SET usuedit = @usuario WHERE cnofact = @cnofact AND cref_agrupada = @entrada";
        int rows = await conn.ExecuteAsync(sql, new { usuario, cnofact, entrada });
        if (rows > 0)
        {
            await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync("onFacturaEnUso", new { cnofact, usuario });
            await NotificarFacturaActualizadaAsync(cnofact, usuario, entrada);
        }
        return rows > 0;
    }

    public async Task<bool> LimpiarEditorFacturaAsync(string cnofact, string? crefAgrupada = null, string? usuario = null)
    {
        using IDbConnection conn = _db.CreateConnection();
        string? entrada = await ResolverEntradaDeFacturaAsync(conn, cnofact, crefAgrupada, usuario);
        if (entrada == null)
        {
            return false;
        }

        string sql = "UPDATE fact SET usuedit = '' WHERE cnofact = @cnofact AND cref_agrupada = @entrada";
        int rows = await conn.ExecuteAsync(sql, new { cnofact, entrada });
        if (rows > 0)
        {
            await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync("onFacturaLiberada", new { cnofact });
            await NotificarFacturaActualizadaAsync(cnofact, string.Empty, entrada);
        }
        return rows > 0;
    }

    public async Task LimpiarEditoresEntradaAsync(string cref)
    {
        using IDbConnection conn = _db.CreateConnection();
        string sql = "UPDATE fact SET usuedit = '' WHERE cref_agrupada = @cref";
        int rows = await conn.ExecuteAsync(sql, new { cref });
        if (rows > 0)
        {
            List<string> cnofacts = (await conn.QueryAsync<string>(
                "SELECT TRIM(cnofact) FROM fact WHERE cref_agrupada = @cref",
                new { cref })).ToList();
            await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync(
                "onRevisionActualizada",
                new WarehouseHub.RevisionActualizadaEvent
                {
                    Entidad = "EDITORES_ENTRADA",
                    Creferencia = cref,
                    Usuario = string.Empty,
                    Fecha = DateTime.UtcNow.ToString("O"),
                    Cnofacts = cnofacts
                });
        }
    }

    private async Task NotificarFacturaActualizadaAsync(string cnofact, string usuario, string crefAgrupada)
    {
        FacturaRevision? factura = await GetRevisionFacturaAsync(cnofact, crefAgrupada, usuario);
        if (factura == null)
        {
            return;
        }

        string oficinaOrigen = string.IsNullOrWhiteSpace(usuario)
            ? string.Empty
            : await GetLugarUsuarioAsync(usuario);

        var evento = new WarehouseHub.RevisionActualizadaEvent
        {
            Entidad = "FACTURA",
            Creferencia = factura.CrefAgrupada,
            Usuario = usuario,
            Fecha = DateTime.UtcNow.ToString("O"),
            Cnofact = factura.Cnofact,
            Factura = factura,
            OficinaOrigen = oficinaOrigen
        };

        await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync(
            "onRevisionActualizada",
            evento);
        await _monitorTvRealtimePublisher.RevisionActualizadaAsync(
            evento.Creferencia,
            evento.Usuario,
            evento.Entidad,
            evento.OficinaOrigen);
    }

    private async Task NotificarPartidaActualizadaAsync(string creferencia, int id, string usuario)
    {
        PartidaRevision? partida = await ObtenerPartidaActualizadaAsync(id);
        if (partida == null)
        {
            return;
        }

        await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync(
            "onRevisionActualizada",
            new WarehouseHub.RevisionActualizadaEvent
            {
                Entidad = "PARTIDA",
                Creferencia = creferencia,
                Usuario = usuario,
                Fecha = DateTime.UtcNow.ToString("O"),
                Cnofact = partida.Cnofact,
                IdRevdet = partida.IdRevdet,
                Partida = partida
            });

        await _monitorTvRealtimePublisher.RevisionActualizadaAsync(
            creferencia,
            usuario,
            "PARTIDA",
            oficinaOrigen: null);
    }

    private async Task<PartidaRevision?> ObtenerPartidaActualizadaAsync(int id)
    {
        using IDbConnection conn = _db.CreateConnection();
        string optionalColumns = await GetOptionalLabelColumnsAsync(conn);
        return await conn.QueryFirstOrDefaultAsync<PartidaRevision>($@"
            SELECT
                rd.idrevdet AS IdRevdet,
                TRIM(rd.cnofact) AS Cnofact,
                rd.cnoparte AS CnoParte,
                COALESCE(rd.cdescripcion, '') AS CDescripcion,
                rd.marca,
                rd.modelo,
                rd.numser,
                COALESCE(rd.numlote, '') AS NumLote,
                COALESCE(rd.ncantfactura, 0) AS NCantFactura,
                rd.ncantout,
                rd.cpaisorigen,
                COALESCE(rd.cpt_cve_tmateria, '') AS CptCveTmateria,
                COALESCE(rd.cpt_desc_tmateria, '') AS CptDescTmateria,
                COALESCE(rd.tipo_material, '') AS TipoMaterial,
                rd.revisada,
                COALESCE(rd.obsrevpartida, '') AS Obsrevpartida,
                COALESCE(rd.mnotapartida, '') AS Mnotapartida,
                COALESCE(rd.npeso, 0) AS NPeso,
                COALESCE(rd.cantumc, 0) AS CantidadComercial,
                COALESCE(rd.umcint, '') AS UnidadComercial,
                COALESCE(rd.cunidadfactura, '') AS UnidadFactura,
                COALESCE(rd.ncanttarifa, 0) AS CantidadTarifa,
                COALESCE(rd.cunidadtarifa, '') AS UnidadTarifa,
                COALESCE(rd.npesounitario, 0) AS PesoUnitario,
                COALESCE(rd.valuni, 0) AS ValorUnitario,
                COALESCE(rd.nvaloragregado, 0) AS ValorAgregado,
                COALESCE(rd.nvalorpartidame, 0) AS ValorPartidaMe,
                COALESCE(f.nfactorequiv, 0) AS FactorEquivalencia,
                COALESCE(rd.cclienteid, '') AS Cclienteid,
                {optionalColumns}
            FROM revdet rd
            INNER JOIN fact f ON f.idfactura = rd.idfactura
            WHERE rd.idrevdet = @id
            LIMIT 1", new { id });
    }

    private static async Task<string> GetOptionalLabelColumnsAsync(IDbConnection connection)
    {
        // Las instalaciones legacy no tienen siempre las mismas columnas. Solo se
        // proyectan nombres constantes después de verificar el esquema real.
        HashSet<string> columns = (await connection.QueryAsync<string>(@"
            SELECT column_name FROM information_schema.columns WHERE table_schema=DATABASE()
            AND table_name='revdet' AND column_name IN ('parte_cte','lhazmat','cpt_cve_hazmat')"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string customerPart = columns.Contains("parte_cte") ? "COALESCE(rd.parte_cte,'')" : "''";
        string hazmat = columns.Contains("lhazmat") ? "CASE WHEN rd.lhazmat IN ('S','1') THEN 1 ELSE 0 END" : "0";
        string un = columns.Contains("cpt_cve_hazmat") ? "COALESCE(rd.cpt_cve_hazmat,'')" : "''";
        return $"{customerPart} AS ParteCte, {hazmat} AS Hazmat, {un} AS CptCveHazmat";
    }
}
