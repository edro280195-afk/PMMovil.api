using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using PMMovil.Api.Attributes;
using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Controlador de monitoreo de revisiones para la Coordinación de Tráfico (Nuevo Laredo).
/// Permite a coordinadores autorizados consultar en tiempo real el avance de los revisadores
/// y las revisiones concluidas del día sin conceder capacidades de supervisión operativa
/// (como kill switch, autorización de desbloqueos o edición de turnos).
///
/// Se abre desde un enlace (<c>/coordinacion-trafico?usuario=clave</c>) que pide la clave del sistema PMM
/// (la misma de PMMovil, <c>/api/auth/login</c>) y trabaja con el JWT que ese login entrega. La identidad
/// sale únicamente del token, nunca de la URL; <see cref="RequiereModuloAttribute"/> pregunta al servicio
/// de permisos del API si esa persona tiene el módulo 168 en <c>accesos</c> (o es de Sistemas).
/// Sin permiso responde 403 y la pantalla solo informa que no tiene permisos. No hay regla propia: la
/// anterior (módulos 168, 28 y 160) dejaba pasar a casi todos los usuarios activos.
/// </summary>
[Authorize]
[RequiereModulo(MobileModuleIds.TrafficCoordinationMonitor)]
[ApiController]
[Route("api/coordinacion-trafico")]
public sealed class CoordinacionTraficoController : ControllerBase
{
    private readonly BaseRepository _db;
    private readonly ILogger<CoordinacionTraficoController> _logger;
    private readonly string _basePath;

    public CoordinacionTraficoController(
        BaseRepository db,
        IConfiguration config,
        ILogger<CoordinacionTraficoController> logger)
    {
        _db = db;
        _logger = logger;
        _basePath = config["PhotoSettings:BasePath"] ?? "\\\\192.168.10.23\\Programas\\Trafico\\Fotos";
    }

    /// <summary>
    /// Devuelve la información operativa para el tablero:
    /// - Revisadores activos y en qué van
    /// - Revisiones completadas del día ("las que han revisado")
    /// - Resumen cuantitativo (KPIs de avance)
    /// </summary>
    [HttpGet("monitoreo")]
    public async Task<IActionResult> GetMonitoreo(CancellationToken cancellationToken)
    {
        // Identidad del JWT (ya autorizada con el módulo 168 por [RequiereModulo]).
        string coordinador = (User.Identity?.Name ?? string.Empty).Trim();

        try
        {
            using IDbConnection conn = _db.CreateConnection();

            // 1. Revisores activos ("En qué van los revisadores")
            string sqlActivos = @"
                SELECT t.creferencia,
                       MAX(CASE WHEN t.finiciorevision >= '2000-01-01' THEN t.finiciorevision ELSE NULL END) AS finiciorevision,
                       MAX(CASE WHEN t.ffinrevision >= '2000-01-01' THEN t.ffinrevision ELSE NULL END) AS ffinrevision,
                       MAX(CASE WHEN t.ftomadocumento >= '2000-01-01' THEN t.ftomadocumento ELSE NULL END) AS ftomadocumento,
                       MAX(COALESCE(t.estatus_final, 0)) AS estatus_final,
                       COALESCE(
                           (SELECT usuario FROM traftiemposrevision WHERE creferencia = t.creferencia AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000) ORDER BY finiciorevision DESC, ftomadocumento DESC LIMIT 1),
                           (SELECT usuario FROM traftiemposrevision WHERE creferencia = t.creferencia AND usuario IS NOT NULL AND usuario != '' ORDER BY idtiemposrevision DESC LIMIT 1)
                       ) AS usuario,
                       MAX(c.cnombre) AS nombre_cliente,
                       MAX(td.ubicacion) AS ubicacion,
                       COUNT(DISTINCT f.cnofact) AS total_facturas,
                       COUNT(DISTINCT CASE WHEN f.status_rev = 1 THEN f.cnofact END) AS facturas_completas,
                       COUNT(DISTINCT CASE WHEN f.status_rev = 2 THEN f.cnofact END) AS facturas_discrepancia,
                       (SELECT discrepancias FROM fact WHERE cref_agrupada = t.creferencia AND status_rev = 2 ORDER BY dfecha DESC LIMIT 1) AS ultima_discrepancia,
                       (SELECT MAX(CASE WHEN rd.lhazmat = 1 THEN 1 ELSE 0 END) FROM revdet rd WHERE rd.cref = t.creferencia) AS lhazmat,
                       (SELECT COALESCE(SUM(TIMESTAMPDIFF(SECOND, finiciorevision, ffinrevision)), 0)
                        FROM traftiemposrevision t2
                        WHERE t2.creferencia = t.creferencia AND t2.finiciorevision IS NOT NULL AND YEAR(t2.finiciorevision) >= 2000 AND t2.ffinrevision IS NOT NULL AND YEAR(t2.ffinrevision) >= 2000) AS segundos_acumulados,
                       (SELECT MAX(t3.finiciorevision)
                        FROM traftiemposrevision t3
                        WHERE t3.creferencia = t.creferencia AND t3.finiciorevision IS NOT NULL AND YEAR(t3.finiciorevision) >= 2000 AND (t3.ffinrevision IS NULL OR YEAR(t3.ffinrevision) < 2000) AND t3.finiciorevision >= DATE_SUB(NOW(), INTERVAL 18 HOUR)) AS inicio_sesion_actual
                FROM traftiemposrevision t
                LEFT JOIN fact f ON t.creferencia = f.cref_agrupada
                LEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0
                LEFT JOIN trafdet td ON td.creferencia = t.creferencia
                WHERE ((t.ffinrevision IS NULL OR YEAR(t.ffinrevision) < 2000)
                       AND t.usuario IS NOT NULL AND t.usuario != ''
                       AND ((t.finiciorevision IS NOT NULL AND YEAR(t.finiciorevision) >= 2000 AND t.finiciorevision >= DATE_SUB(NOW(), INTERVAL 18 HOUR))
                            OR (t.ftomadocumento IS NOT NULL AND YEAR(t.ftomadocumento) >= 2000 AND t.ftomadocumento >= DATE_SUB(NOW(), INTERVAL 1 HOUR))))
                   OR (t.ffinrevision IS NOT NULL AND YEAR(t.ffinrevision) >= 2000 AND t.ffinrevision >= DATE_SUB(NOW(), INTERVAL 15 MINUTE) AND t.estatus_final = 1)
                GROUP BY t.creferencia
                ORDER BY COALESCE(MAX(t.finiciorevision), MAX(t.ftomadocumento)) DESC";

            List<dynamic> rowsActivos = (await conn.QueryAsync<dynamic>(new CommandDefinition(sqlActivos, cancellationToken: cancellationToken))).ToList();

            List<string> referenciasActivas = rowsActivos
                .Select(r => (string)r.creferencia)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Partidas por referencia activa
            Dictionary<string, (int Total, int Revisadas)> partidasActivas = new(StringComparer.OrdinalIgnoreCase);
            if (referenciasActivas.Count > 0)
            {
                string sqlPartidas = @"
                    SELECT f.cref_agrupada AS creferencia,
                           COUNT(DISTINCT rd.idrevdet) AS total_partidas,
                           COUNT(DISTINCT CASE WHEN rd.revisada = 1 THEN rd.idrevdet END) AS partidas_revisadas
                    FROM fact f
                    LEFT JOIN revdet rd ON rd.idfactura = f.idfactura
                    WHERE f.cref_agrupada IN @referenciasActivas
                    GROUP BY f.cref_agrupada";

                var partidaRows = await conn.QueryAsync<dynamic>(new CommandDefinition(sqlPartidas, new { referenciasActivas }, cancellationToken: cancellationToken));
                foreach (var pr in partidaRows)
                {
                    partidasActivas[(string)pr.creferencia] = (Convert.ToInt32(pr.total_partidas), Convert.ToInt32(pr.partidas_revisadas));
                }
            }

            // Nombres de usuarios
            var nombresUsuarios = await ObtenerMapaNombresUsuariosAsync(conn, cancellationToken);

            // Factura activa en presencia SignalR
            var presencias = WarehouseHub.GetActivePresenceSnapshot();
            var facturasActuales = presencias
                .Where(p => !string.IsNullOrWhiteSpace(p.Entry) && !string.IsNullOrWhiteSpace(p.Invoice))
                .GroupBy(p => p.Entry, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.LastSeen).First().Invoice, StringComparer.OrdinalIgnoreCase);

            var itemsActivos = rowsActivos.Select(r =>
            {
                string cref = (string)r.creferencia;
                string user = (r.usuario != null) ? ((string)r.usuario).Trim() : "";
                partidasActivas.TryGetValue(cref, out var partidaCount);
                facturasActuales.TryGetValue(cref, out string? facturaActual);
                nombresUsuarios.TryGetValue(user, out string? nombreCompleto);

                DateTime? fin = r.ffinrevision != null && ((DateTime)r.ffinrevision).Year >= 2000 ? (DateTime)r.ffinrevision : null;
                DateTime? inicio = r.finiciorevision != null && ((DateTime)r.finiciorevision).Year >= 2000 ? (DateTime)r.finiciorevision : null;
                DateTime? toma = r.ftomadocumento != null && ((DateTime)r.ftomadocumento).Year >= 2000 ? (DateTime)r.ftomadocumento : null;

                string estado;
                if (fin.HasValue && fin.Value >= DateTime.Now.AddMinutes(-15) && (int)r.estatus_final == 1)
                {
                    estado = "FINALIZADA";
                }
                else if (inicio.HasValue)
                {
                    estado = "EN_REVISION";
                }
                else if (toma.HasValue)
                {
                    estado = "DOCUMENTOS_TOMADOS";
                }
                else
                {
                    estado = "INACTIVA";
                }

                return new
                {
                    creferencia = cref,
                    usuario = user,
                    nombreRevisor = !string.IsNullOrWhiteSpace(nombreCompleto) ? nombreCompleto : user,
                    nombreCliente = (r.nombre_cliente != null) ? (string)r.nombre_cliente : "",
                    ubicacion = (r.ubicacion != null) ? (string)r.ubicacion : "",
                    inicioRevision = inicio?.ToString("yyyy-MM-ddTHH:mm:ss") ?? "",
                    finRevision = fin?.ToString("yyyy-MM-ddTHH:mm:ss") ?? "",
                    tomaDocumento = toma?.ToString("yyyy-MM-ddTHH:mm:ss") ?? "",
                    totalFacturas = (r.total_facturas != null) ? (int)r.total_facturas : 0,
                    facturasCompletas = (r.facturas_completas != null) ? (int)r.facturas_completas : 0,
                    totalPartidas = partidaCount.Total,
                    partidasRevisadas = partidaCount.Revisadas,
                    facturasDiscrepancia = (r.facturas_discrepancia != null) ? (int)r.facturas_discrepancia : 0,
                    ultimaDiscrepancia = (r.ultima_discrepancia != null) ? (string)r.ultima_discrepancia : "",
                    hazmat = (r.lhazmat != null) ? (int)r.lhazmat : 0,
                    segundosAcumulados = (r.segundos_acumulados != null) ? (long)r.segundos_acumulados : 0,
                    inicioSesionActual = (r.inicio_sesion_actual != null) ? ((DateTime)r.inicio_sesion_actual).ToString("yyyy-MM-ddTHH:mm:ss") : null,
                    estado,
                    facturaActual = facturaActual ?? "",
                    recienTerminada = estado == "FINALIZADA"
                };
            }).ToList();

            // 2. Revisiones concluidas hoy ("Las que han revisado")
            string sqlRevisadasHoy = @"
                SELECT t.creferencia,
                       DATE_FORMAT(MAX(t.ffinrevision), '%Y-%m-%dT%H:%i:%s') AS fecha_fin,
                       DATE_FORMAT(MIN(CASE WHEN t.finiciorevision >= '2000-01-01' THEN t.finiciorevision ELSE NULL END), '%Y-%m-%dT%H:%i:%s') AS fecha_inicio,
                       COALESCE(NULLIF(TRIM(t.usurevisa), ''), NULLIF(TRIM(t.usuario), ''), 'N/D') AS usuario,
                       MAX(c.cnombre) AS nombre_cliente,
                       COUNT(DISTINCT f.cnofact) AS total_facturas,
                       COUNT(DISTINCT CASE WHEN f.status_rev = 1 THEN f.cnofact END) AS facturas_revisadas,
                       COUNT(DISTINCT CASE WHEN f.status_rev = 2 THEN f.cnofact END) AS facturas_discrepancia,
                       COALESCE(SUM(TIMESTAMPDIFF(SECOND, t.finiciorevision, t.ffinrevision)), 0) AS duracion_segundos
                FROM traftiemposrevision t
                LEFT JOIN fact f ON f.cref_agrupada = t.creferencia
                LEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0
                WHERE t.ffinrevision IS NOT NULL AND YEAR(t.ffinrevision) >= 2000
                  AND t.estatus_final = 1
                  AND DATE(t.ffinrevision) = CURDATE()
                GROUP BY t.creferencia, t.usurevisa, t.usuario
                ORDER BY MAX(t.ffinrevision) DESC";

            List<dynamic> rowsRevisadas = (await conn.QueryAsync<dynamic>(new CommandDefinition(sqlRevisadasHoy, cancellationToken: cancellationToken))).ToList();

            List<string> referenciasRevisadas = rowsRevisadas
                .Select(r => (string)r.creferencia)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Dictionary<string, (int Total, int Revisadas)> partidasRevisadas = new(StringComparer.OrdinalIgnoreCase);
            if (referenciasRevisadas.Count > 0)
            {
                string sqlPartidasRevisadas = @"
                    SELECT f.cref_agrupada AS creferencia,
                           COUNT(DISTINCT rd.idrevdet) AS total_partidas,
                           COUNT(DISTINCT CASE WHEN rd.revisada = 1 THEN rd.idrevdet END) AS partidas_revisadas
                    FROM fact f
                    LEFT JOIN revdet rd ON rd.idfactura = f.idfactura
                    WHERE f.cref_agrupada IN @referenciasRevisadas
                    GROUP BY f.cref_agrupada";

                var partidaRows = await conn.QueryAsync<dynamic>(new CommandDefinition(sqlPartidasRevisadas, new { referenciasRevisadas }, cancellationToken: cancellationToken));
                foreach (var pr in partidaRows)
                {
                    partidasRevisadas[(string)pr.creferencia] = (Convert.ToInt32(pr.total_partidas), Convert.ToInt32(pr.partidas_revisadas));
                }
            }

            var itemsRevisadasHoy = rowsRevisadas.Select(r =>
            {
                string cref = (string)r.creferencia;
                string user = (r.usuario != null) ? ((string)r.usuario).Trim() : "";
                nombresUsuarios.TryGetValue(user, out string? nombreCompleto);
                partidasRevisadas.TryGetValue(cref, out var pCount);
                bool automatico = user.StartsWith("AUTO", StringComparison.OrdinalIgnoreCase);

                return new
                {
                    creferencia = cref,
                    fechaFin = (string)r.fecha_fin,
                    fechaInicio = (string)(r.fecha_inicio ?? ""),
                    usuario = user,
                    nombreRevisor = !string.IsNullOrWhiteSpace(nombreCompleto) ? nombreCompleto : user,
                    nombreCliente = (r.nombre_cliente != null) ? (string)r.nombre_cliente : "",
                    totalFacturas = Convert.ToInt32(r.total_facturas),
                    facturasRevisadas = Convert.ToInt32(r.facturas_revisadas),
                    facturasDiscrepancia = Convert.ToInt32(r.facturas_discrepancia),
                    totalPartidas = pCount.Total,
                    partidasRevisadas = pCount.Revisadas,
                    duracionSegundos = Convert.ToInt64(r.duracion_segundos),
                    automatico
                };
            }).ToList();

            // 3. Resumen y KPIs
            int revisoresActivosCount = itemsActivos
                .Where(i => i.estado == "EN_REVISION" || i.estado == "DOCUMENTOS_TOMADOS")
                .Select(i => i.usuario)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            int facturasRevisadasTotal = itemsRevisadasHoy.Sum(i => (int)i.facturasRevisadas);
            int partidasRevisadasTotal = itemsRevisadasHoy.Sum(i => (int)i.partidasRevisadas);
            int conDiscrepanciaTotal = itemsRevisadasHoy.Count(i => (int)i.facturasDiscrepancia > 0);

            long duracionTotalSegundos = itemsRevisadasHoy.Where(i => (long)i.duracionSegundos > 0).Sum(i => (long)i.duracionSegundos);
            int duracionCount = itemsRevisadasHoy.Count(i => i.duracionSegundos > 0);
            double promedioMinutos = duracionCount > 0 ? Math.Round((double)duracionTotalSegundos / duracionCount / 60.0, 1) : 0;

            var kpis = new
            {
                revisoresActivos = revisoresActivosCount,
                enRevision = itemsActivos.Count(i => i.estado == "EN_REVISION"),
                documentosTomados = itemsActivos.Count(i => i.estado == "DOCUMENTOS_TOMADOS"),
                revisadasHoy = itemsRevisadasHoy.Count,
                facturasRevisadasHoy = facturasRevisadasTotal,
                partidasRevisadasHoy = partidasRevisadasTotal,
                conDiscrepanciaHoy = conDiscrepanciaTotal,
                promedioMinutosPorRevision = promedioMinutos
            };

            nombresUsuarios.TryGetValue(coordinador, out string? nombreCoordinador);

            return Ok(new
            {
                success = true,
                usuario = coordinador,
                nombreCoordinador = !string.IsNullOrWhiteSpace(nombreCoordinador) ? nombreCoordinador : coordinador,
                revisores = itemsActivos,
                revisadasHoy = itemsRevisadasHoy,
                kpis,
                fechaGeneracion = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al generar datos del tablero de Coordinación de Tráfico.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                mensaje = "Ocurrió un error al consultar la información de monitoreo."
            });
        }
    }

    /// <summary>
    /// Consulta el detalle de facturas y partidas de una referencia para inspección en el tablero.
    /// </summary>
    [HttpGet("detalle-referencia")]
    public async Task<IActionResult> GetDetalleReferencia(
        [FromQuery] string? creferencia,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(creferencia))
        {
            return BadRequest(new { success = false, mensaje = "Se requiere la referencia." });
        }

        try
        {
            using IDbConnection conn = _db.CreateConnection();

            string sqlFacturas = @"
                SELECT f.cnofact,
                       f.cclienteid,
                       COALESCE(c.cnombre, '') AS cliente,
                       COALESCE(f.totbultos, 0) AS bultos,
                       COALESCE(f.status_rev, 0) AS status_rev,
                       COALESCE(f.discrepancias, '') AS discrepancias,
                       f.dfecha AS fecha_factura
                FROM fact f
                LEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0
                WHERE f.cref_agrupada = @creferencia
                ORDER BY f.cnofact";

            var facturas = (await conn.QueryAsync<dynamic>(new CommandDefinition(sqlFacturas, new { creferencia }, cancellationToken: cancellationToken))).ToList();

            string sqlPartidas = @"
                SELECT rd.idrevdet,
                       rd.cnofact,
                       COALESCE(rd.cnoparte, '') AS nparte,
                       COALESCE(rd.cdescripcion, '') AS descripcion,
                       COALESCE(rd.revisada, 0) AS revisada,
                       COALESCE(rd.ncantfactura, 0) AS cantidad,
                       CASE WHEN rd.revisada = 1 THEN COALESCE(rd.ncantfactura, 0) ELSE 0 END AS cant_revisada,
                       COALESCE(rd.lhazmat, 0) AS hazmat,
                       COALESCE(rd.obsrevpartida, '') AS observaciones
                FROM revdet rd
                JOIN fact f ON f.idfactura = rd.idfactura
                WHERE f.cref_agrupada = @creferencia
                ORDER BY rd.cnofact, rd.idrevdet";

            var partidas = (await conn.QueryAsync<dynamic>(new CommandDefinition(sqlPartidas, new { creferencia }, cancellationToken: cancellationToken))).ToList();

            string sqlFotos = @"
                SELECT f.idfotos AS idFotos,
                       f.nlineno AS nLineNo,
                       f.cextension AS cExtension,
                       f.usucrea AS usuCrea,
                       DATE_FORMAT(f.fcrea, '%Y-%m-%d %H:%i:%s') AS fCrea,
                       f.ntipodocto AS nTipoDocto
                FROM fotos f
                WHERE f.creferencia = @creferencia
                  AND f.ntipodocto IN (107, 103)
                ORDER BY f.ntipodocto, f.nlineno";

            var fotosRaw = (await conn.QueryAsync<dynamic>(new CommandDefinition(sqlFotos, new { creferencia }, cancellationToken: cancellationToken))).ToList();

            var fotos = fotosRaw.Select(f =>
            {
                int idFoto = Convert.ToInt32(f.idFotos);
                int tipo = Convert.ToInt32(f.nTipoDocto);
                bool esDiscrepancia = tipo == 103;
                return new
                {
                    idFotos = idFoto,
                    nLineNo = Convert.ToInt32(f.nLineNo),
                    cExtension = PhotoFileNameResolver.NormalizeExtension((string?)f.cExtension),
                    usuCrea = (string)(f.usuCrea ?? ""),
                    fCrea = (string)(f.fCrea ?? ""),
                    nTipoDocto = tipo,
                    esDiscrepancia,
                    tipoEtiqueta = esDiscrepancia ? "Discrepancia" : "Evidencia"
                };
            }).ToList();

            return Ok(new
            {
                success = true,
                creferencia,
                facturas,
                partidas,
                fotos
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar detalle de referencia {Creferencia}", creferencia);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                mensaje = "No se pudo consultar el detalle de la referencia."
            });
        }
    }

    /// <summary>
    /// Sirve una fotografía de revisión (evidencia o discrepancia) para el tablero de Coordinación.
    /// El tablero la descarga como blob con el encabezado Authorization, como el resto de consultas.
    /// </summary>
    [HttpGet("foto/{id:int}")]
    public async Task<IActionResult> GetFoto(
        int id,
        CancellationToken cancellationToken)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            string sql = @"
                SELECT TRIM(f.creferencia) AS Creferencia,
                       f.cextension AS CExtension,
                       f.nlineno AS NLineNo,
                       f.ntipodocto AS NTipoDocto,
                       COALESCE(
                           NULLIF(fa.cclienteidtraf, ''),
                           NULLIF(ff.cclienteid, '')
                       ) AS Cidcte
                FROM fotos f
                LEFT JOIN traf fa ON fa.creferencia = f.creferencia
                LEFT JOIN fact ff
                  ON ff.cnofact = f.creferencia
                  OR ff.cref_agrupada = f.creferencia
                  OR ff.creferencia = f.creferencia
                WHERE f.idfotos = @id
                  AND f.ntipodocto IN (107, 103)
                LIMIT 1";

            var data = await conn.QueryFirstOrDefaultAsync<dynamic>(
                new CommandDefinition(sql, new { id }, cancellationToken: cancellationToken));

            if (data == null)
            {
                return NotFound(new { success = false, mensaje = "Fotografía no encontrada en la base de datos." });
            }

            string cidcte = ((string)(data.Cidcte ?? "")).Trim();
            string sqlCte = "SELECT COALESCE(NULLIF(carpeta,''), cidcte) FROM clientes WHERE cidcte = @id LIMIT 1";
            string folder = ((await conn.ExecuteScalarAsync<string>(new CommandDefinition(sqlCte, new { id = cidcte }, cancellationToken: cancellationToken))) ?? cidcte).Trim();

            string ext = PhotoFileNameResolver.NormalizeExtension((string?)data.CExtension);
            string cref = ((string)data.Creferencia).Trim();
            int line = Convert.ToInt32(data.NLineNo);
            int tipoDocto = Convert.ToInt32(data.NTipoDocto);
            string fileName = PhotoFileNameResolver.Build(cref, tipoDocto, line, ext);
            string? filePath = PhotoFileNameResolver.FindExistingPath(
                _basePath,
                folder,
                cref,
                tipoDocto,
                line,
                ext);

            if (filePath == null || !System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("GetFoto: Archivo físico no encontrado {FileName} en carpeta {Folder}", fileName, folder);
                return NotFound(new
                {
                    success = false,
                    mensaje = "Archivo físico no encontrado en el servidor de imágenes.",
                    fileName,
                    folder
                });
            }

            string contentType = ext.Equals("png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            return PhysicalFile(filePath, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al servir fotografía {Id} de coordinación", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                mensaje = "Error al leer la fotografía."
            });
        }
    }

    private static async Task<Dictionary<string, string>> ObtenerMapaNombresUsuariosAsync(
        IDbConnection conn,
        CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT TRIM(claveusuario) AS Clave,
                   CONCAT(TRIM(nombre), ' ', TRIM(apaterno)) AS NombreCompleto
            FROM usuarios
            WHERE inactivo = 0";

        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            string clave = (string)r.Clave;
            if (!string.IsNullOrWhiteSpace(clave) && !map.ContainsKey(clave))
            {
                map[clave] = ((string)r.NombreCompleto).Trim();
            }
        }
        return map;
    }
}
