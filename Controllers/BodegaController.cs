using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Attributes;
using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
using PMMovil.Api.Models;

namespace PMMovil.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BodegaController : ControllerBase
{
    private readonly BaseRepository _db;
    private readonly ILogger<BodegaController> _logger;
    private readonly IConfiguration _config;
    private readonly IHubContext<BodegaHub> _hub;

    public BodegaController(
        BaseRepository db,
        ILogger<BodegaController> logger,
        IConfiguration config,
        IHubContext<BodegaHub> hub)
    {
        _db = db;
        _logger = logger;
        _config = config;
        _hub = hub;
    }

    private bool ValidarApiKey()
    {
        string expectedKey = _config["DashboardApiKey"] ?? "";
        if (string.IsNullOrWhiteSpace(expectedKey)) return false;

        if (Request.Headers.TryGetValue("X-Dashboard-Key", out var hk) && hk.ToString() == expectedKey)
            return true;

        if (Request.Query.TryGetValue("key", out var qk) && qk.ToString() == expectedKey)
            return true;

        return false;
    }

    [HttpGet("activos")]
    public async Task<IActionResult> GetActivos([FromQuery] string bodega = "01")
    {
        if (!ValidarApiKey())
            return Unauthorized(new { success = false, message = "API Key inválida." });

        try
        {
            using IDbConnection conn = _db.CreateConnection();

            const string sql = @"
SELECT
    r.relno,
    r.usucargamcia                              AS bodeguero,
    r.caja,
    r.bodega,
    r.estatus_carga                             AS estatus,
    r.inicio_carga                              AS inicio_carga,
    MIN(e.time_scan)                            AS primer_escaneo,
    r.fin_carga,
    r.observa,
    r.sellos2                                   AS sellos,
    r.placas,
    COALESCE(MIN(t2.creferencia), '')           AS creferencia,
    COALESCE(MIN(t2.mcargaobserva), '')         AS carga_observa,
    COALESCE(MIN(t.cclienteidtraf), '')         AS cliente_id,
    MIN(t.dfechaarribo)                         AS dfechaarribo,
    MIN(t.dfechasalida)                         AS dfechasalida,
    COALESCE(MIN(t.cnoguia), '')                AS no_guia,
    COALESCE(COUNT(DISTINCT e.id_etiqueta_mcia), 0) AS total_bultos,
    COALESCE(COUNT(DISTINCT CASE WHEN e.estatus = 'CARGADO' THEN e.id_etiqueta_mcia END), 0) AS bultos_cargados,
    CASE
        WHEN r.fin_carga IS NOT NULL AND YEAR(r.fin_carga) >= 2000 THEN 1
        ELSE 0
    END AS finalizada
FROM relacion r
LEFT JOIN traf2 t2 ON t2.nrelacion = r.relno
LEFT JOIN traf t   ON t.creferencia  = t2.creferencia
LEFT JOIN agrupa a ON a.creferencia  = t2.creferencia
LEFT JOIN etiqueta_mcia e ON e.creferencia = a.cref_agrupada
WHERE
    r.bodega       = @bodega
    AND r.usucargamcia <> ''
    AND r.fcrea >= CURDATE()
GROUP BY r.relno, r.inicio_carga, r.fin_carga, r.caja, r.bodega, r.estatus_carga, r.observa, r.sellos2, r.placas, r.usucargamcia
ORDER BY COALESCE(r.inicio_carga, MIN(e.time_scan)) ASC";

            var rows = (await conn.QueryAsync<object>(sql, new { bodega })).ToList();

            var claves = rows
    .Select(r => (string)((dynamic)r).bodeguero)
    .Distinct()
    .ToList();

            Dictionary<string, string> nombres = new(StringComparer.OrdinalIgnoreCase);
            if (claves.Count > 0)
            {
                try
                {
                    using IDbConnection connRep = _db.CreateReportesConnection();
                    var ns = await connRep.QueryAsync<object>(
                         @"SELECT claveusuario,
                           TRIM(CONCAT_WS(' ', TRIM(nombre), TRIM(apaterno), TRIM(amaterno))) AS nombreCompleto
                           FROM usuarios WHERE claveusuario IN @claves",
                        new { claves });
                    foreach (dynamic n in ns)
                    {
                        string nom = (string)n.nombreCompleto;
                        if (!string.IsNullOrWhiteSpace(nom))
                            nombres[(string)n.claveusuario] = nom;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "BodegaController: no se pudieron obtener nombres completos.");
                }
            }

            static string SafeDateStr(object val)
            {
                if (val == null) return "";
                if (val is DateTime dt)
                    return dt.Year >= 2000 ? dt.ToString("yyyy-MM-ddTHH:mm:ss") : "";
                string s = val.ToString();
                if (string.IsNullOrWhiteSpace(s)) return "";
                if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AllowWhiteSpaces, out DateTime d1) && d1.Year >= 2000)
                    return d1.ToString("yyyy-MM-ddTHH:mm:ss");
                if (DateTime.TryParse(s, System.Globalization.CultureInfo.CurrentCulture,
                        System.Globalization.DateTimeStyles.AllowWhiteSpaces, out DateTime d2) && d2.Year >= 2000)
                    return d2.ToString("yyyy-MM-ddTHH:mm:ss");
                try { var d3 = Convert.ToDateTime(val); if (d3.Year >= 2000) return d3.ToString("yyyy-MM-ddTHH:mm:ss"); } catch { }
                return "";
            }

            DateTime now = DateTime.Now;
            var items = rows.Select(r =>
            {
                dynamic row = r;
                string clave = (string)row.bodeguero;
                nombres.TryGetValue(clave, out string nombre);

                string inicioCargaStr = SafeDateStr(row.inicio_carga);
                if (string.IsNullOrEmpty(inicioCargaStr))
                    inicioCargaStr = SafeDateStr(row.primer_escaneo);

                string finCargaStr = SafeDateStr(row.fin_carga);
                bool finalizada = Convert.ToInt32(row.finalizada ?? 0) == 1;

                return new
                {
                    bodeguero = clave,
                    nombreCompleto = nombre ?? clave,
                    relno = (string)row.relno,
                    caja = (string)(row.caja ?? ""),
                    bodega = (string)(row.bodega ?? ""),
                    estatus = (string)(row.estatus ?? ""),
                    inicioCarga = inicioCargaStr,
                    finCarga = finCargaStr,
                    finalizada = finalizada,
                    observa = (string)(row.observa ?? ""),
                    sellos = (string)(row.sellos ?? ""),
                    placas = (string)(row.placas ?? ""),
                    creferencia = (string)(row.creferencia ?? ""),
                    cargaObserva = (string)(row.carga_observa ?? ""),
                    clienteId = (string)(row.cliente_id ?? ""),
                    noGuia = (string)(row.no_guia ?? ""),
                    totalBultos = Convert.ToInt32(row.total_bultos),
                    bultosCargados = Convert.ToInt32(row.bultos_cargados),
                };
            }).ToList();

            return Ok(new
            {
                success = true,
                bodega,
                serverTime = now.ToString("yyyy-MM-ddTHH:mm:ss"),
                items
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BodegaController.GetActivos(bodega={Bodega})", bodega);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpPost("actividad")]
    public async Task<IActionResult> NotificarActividad([FromBody] BodegaActividadEvent ev)
    {
        if (!ValidarApiKey())
            return Unauthorized(new { success = false, message = "API Key inválida." });

        await _hub.Clients.All.SendAsync("onActividadBodega", ev);
        return Ok(new { success = true });
    }

    [HttpGet("lista")]
    [Authorize]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> GetLista()
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            const string sql = @"
                SELECT
                    t.creferencia                                  AS Creferencia,
                    t.ref                                          AS Ref,
                    DATE_FORMAT(t.dfechaarribo, '%Y-%m-%dT%H:%i')  AS FArribo,
                    t.cclienteidtraf                               AS ClaveCte,
                    c.cnombre                                      AS Cliente,
                    t.cnocaja                                      AS Caja,
                    t.cnoguia                                      AS Guia,
                    t.crecibio                                     AS Recibio,
                    tr.cnombre                                     AS Transportista,
                    t.cidprovtraf                                  AS ClaveProv,
                    p.cnombre                                      AS Proveedor,
                    t.cvia                                         AS Via,
                    t.cidmanejo                                    AS Manejo,
                    t.tipomcia                                     AS TipoMcia,
                    CAST(COALESCE(t.linbond, 0) AS UNSIGNED)       AS Inbond,
                    CAST(COALESCE(t.lhazmat, 0) AS UNSIGNED)       AS Hazmat,
                    DATE_FORMAT(t.fcreatrafico, '%Y-%m-%dT%H:%i:%s') AS FCreacion,
                    DATE_FORMAT(t.tdatetimetrafico, '%Y-%m-%dT%H:%i:%s') AS FModi
                FROM traf t
                LEFT JOIN clientes c  ON c.cidcte       = t.cclienteidtraf
                LEFT JOIN provs    p  ON p.cidprov      = t.cidprovtraf
                LEFT JOIN transpor tr ON tr.cidtranspor = t.cidtransportraf
                WHERE
                    t.tipop IN ('C', 'D')
                    AND t.fcreatrafico IS NOT NULL
                    AND DATE(t.fcreatrafico) >= DATE_SUB(CURDATE(), INTERVAL 10 DAY)
                ORDER BY t.fcreatrafico DESC
                LIMIT 300";

            var items = (await conn.QueryAsync<BodegaEntradaDto>(sql)).ToList();
            return Ok(new { success = true, items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BodegaController.GetLista");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpGet("buscar")]
    [Authorize]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> Buscar([FromQuery] string? q)
    {
        try
        {
            string filtro = (q ?? string.Empty).Trim();
            string paramLike = "%" + filtro + "%";

            bool isLikelyCref = filtro.Length == 15 && filtro.StartsWith("01", StringComparison.Ordinal);

            using IDbConnection conn = _db.CreateConnection();
            string sql = @"
                SELECT
                    t.creferencia                                  AS Creferencia,
                    t.ref                                          AS Ref,
                    DATE_FORMAT(t.dfechaarribo, '%Y-%m-%dT%H:%i')  AS FArribo,
                    t.cclienteidtraf                               AS ClaveCte,
                    c.cnombre                                      AS Cliente,
                    t.cnocaja                                      AS Caja,
                    t.cnoguia                                      AS Guia,
                    t.crecibio                                     AS Recibio,
                    tr.cnombre                                     AS Transportista,
                    t.cidprovtraf                                  AS ClaveProv,
                    p.cnombre                                      AS Proveedor,
                    t.cvia                                         AS Via,
                    t.cidmanejo                                    AS Manejo,
                    t.tipomcia                                     AS TipoMcia,
                    CAST(COALESCE(t.linbond, 0) AS UNSIGNED)       AS Inbond,
                    CAST(COALESCE(t.lhazmat, 0) AS UNSIGNED)       AS Hazmat,
                    DATE_FORMAT(t.fcreatrafico, '%Y-%m-%dT%H:%i:%s') AS FCreacion,
                    DATE_FORMAT(t.tdatetimetrafico, '%Y-%m-%dT%H:%i:%s') AS FModi
                FROM traf t
                LEFT JOIN clientes c  ON c.cidcte       = t.cclienteidtraf
                LEFT JOIN provs    p  ON p.cidprov      = t.cidprovtraf
                LEFT JOIN transpor tr ON tr.cidtranspor = t.cidtransportraf
                WHERE 1 = 1";

            if (string.IsNullOrEmpty(filtro))
            {
                sql += " ORDER BY t.fcreatrafico DESC LIMIT 300";
                var all = (await conn.QueryAsync<BodegaEntradaDto>(sql)).ToList();
                return Ok(new { success = true, items = all });
            }

            if (isLikelyCref)
            {
                sql += @" AND (
                    t.creferencia LIKE @filtro
                    OR c.cnombre LIKE @filtro
                    OR p.cnombre LIKE @filtro
                    OR t.creferencia = @crefExacta
                )";
            }
            else
            {
                sql += @" AND (
                    t.creferencia LIKE @filtro
                    OR c.cnombre LIKE @filtro
                    OR p.cnombre LIKE @filtro
                )";
            }

            sql += " ORDER BY t.fcreatrafico DESC LIMIT 200";

            var items = (await conn.QueryAsync<BodegaEntradaDto>(sql, new { filtro = paramLike, crefExacta = filtro })).ToList();
            return Ok(new { success = true, items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BodegaController.Buscar(q={Q})", q);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}
