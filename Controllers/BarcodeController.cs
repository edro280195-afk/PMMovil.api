using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PMMovil.Api.Attributes;
using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiereModulo(MobileModuleIds.Review)]
public class BarcodeController : ControllerBase
{
    public class IniciarSesionRequest
    {
        public string CrefAgrupada { get; set; } = "";
    }

    public class LecturaRequest
    {
        public int IdSesion { get; set; }

        public string CodigoRaw { get; set; } = "";

        public string Cnofact { get; set; } = "";

        public string ClienteScanId { get; set; } = "";
    }

    public class RestarLecturaRequest
    {
        public int IdSesion { get; set; }

        public string Cidproduct { get; set; } = "";

        public string CodigoRaw { get; set; } = "";
    }

    public class ResumenProducto
    {
        public string Cidproduct { get; set; } = "";

        public string Descripcion { get; set; } = "";

        public int Cantidad { get; set; }

        public bool Existe { get; set; }
    }

    public class FinalizarRequest
    {
        public bool EnviarCorreo { get; set; } = true;

        public string Observaciones { get; set; } = "";
    }

    private readonly BaseRepository _db;
    private readonly IRevisionReportService _reportService;
    private readonly IHubContext<WarehouseHub> _hub;
    private readonly ILogger<BarcodeController> _logger;
    private readonly RevisionActivityService _activityService;
    private readonly RevisionNormalCompletionService _normalCompletionService;

    public BarcodeController(BaseRepository db, IRevisionReportService reportService, IHubContext<WarehouseHub> hub, ILogger<BarcodeController> logger, RevisionActivityService activityService, RevisionNormalCompletionService normalCompletionService)
    {
        _db = db;
        _reportService = reportService;
        _hub = hub;
        _logger = logger;
        _activityService = activityService;
        _normalCompletionService = normalCompletionService;
    }

    private static async Task RecalculateSessionTotalsAsync(
        IDbConnection conn,
        IDbTransaction transaction,
        int idSesion)
    {
        await conn.ExecuteAsync(
            @"
 UPDATE pmmutileria.revisiones_barcode_sesion
 SET total_lecturas = (
         SELECT COUNT(*)
         FROM pmmutileria.revisiones_barcode_lectura
         WHERE id_sesion = @id AND eliminado = 0
     ),
     total_existentes = (
         SELECT COUNT(*)
         FROM pmmutileria.revisiones_barcode_lectura
         WHERE id_sesion = @id AND eliminado = 0 AND existe = 1
     ),
     total_inexistentes = (
         SELECT COUNT(*)
         FROM pmmutileria.revisiones_barcode_lectura
         WHERE id_sesion = @id AND eliminado = 0 AND existe = 0
     )
 WHERE id_sesion = @id",
            new { id = idSesion },
            transaction);
    }

    [HttpGet("config/{crefAgrupada}")]
    public async Task<IActionResult> GetConfig(string crefAgrupada)
    {
        _ = 7;
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            conn.Open();
            string sqlTraf = " SELECT MAX(NULLIF(cclienteidtraf,'')) AS cidcte,  MAX(NULLIF(cidprovtraf,'')) AS cidprov FROM traf  WHERE creferencia = @cref";
            dynamic datosTraf = await conn.QueryFirstOrDefaultAsync(sqlTraf, new
            {
                cref = crefAgrupada
            });
            string cidcte = datosTraf?.cidcte;
            string cidprov = datosTraf?.cidprov;
            if (string.IsNullOrWhiteSpace(cidcte))
            {
                cidcte = await conn.ExecuteScalarAsync<string>("SELECT MAX(NULLIF(cclienteid,'')) FROM fact WHERE creferencia = @cref", new
                {
                    cref = crefAgrupada
                });
            }
            if (string.IsNullOrWhiteSpace(cidprov))
            {
                cidprov = await conn.ExecuteScalarAsync<string>("SELECT rd.cprovid FROM revdet rd INNER JOIN fact f ON rd.idfactura = f.idfactura WHERE f.creferencia = @cref AND NULLIF(rd.cprovid,'') IS NOT NULL LIMIT 1", new
                {
                    cref = crefAgrupada
                });
            }
            if (string.IsNullOrWhiteSpace(cidcte))
            {
                return NotFound(new
                {
                    success = false,
                    message = "Cliente no encontrado para la entrada."
                });
            }
            if (string.IsNullOrWhiteSpace(cidprov))
            {
                return NotFound(new
                {
                    success = false,
                    message = "Proveedor no encontrado para la entrada."
                });
            }
            int? barcodeLong = await conn.ExecuteScalarAsync<int?>(" SELECT barcode_long FROM cteprov WHERE cidcte = @cidcte AND cidprov = @cidprov AND inactivo = 0 LIMIT 1", new { cidcte, cidprov });
            string nombreCliente = await conn.ExecuteScalarAsync<string>("SELECT cnombre FROM clientes WHERE cidcte = @cidcte LIMIT 1", new { cidcte });
            string nombreProveedor = await conn.ExecuteScalarAsync<string>("SELECT cnombre FROM provs WHERE cidprov = @cidprov LIMIT 1", new { cidprov });
            int totalCatalogo = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM products WHERE cclienteid = @cidcte AND cprovid = @cidprov", new { cidcte, cidprov });
            return Ok(new
            {
                success = true,
                crefAgrupada = crefAgrupada,
                cidcte = cidcte,
                cidprov = cidprov,
                barcodeLong = barcodeLong.GetValueOrDefault(),
                nombreCliente = (nombreCliente ?? ""),
                nombreProveedor = (nombreProveedor ?? ""),
                totalCatalogo = totalCatalogo,
                configurado = (barcodeLong.GetValueOrDefault() > 0)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BarcodeController.GetConfig({Cref})", crefAgrupada);
            return StatusCode(500, new
            {
                success = false,
                message = "Error obteniendo configuración: " + ex.Message
            });
        }
    }

    [HttpGet("estado/{crefAgrupada}")]
    public async Task<IActionResult> GetEstado(string crefAgrupada)
    {

        try
        {
            using IDbConnection conn = _db.CreateConnection();
            conn.Open();
            dynamic sesion = await conn.QueryFirstOrDefaultAsync<object>(" SELECT s.id_sesion, s.cref_agrupada, s.cidcte, s.cidprov, s.barcode_long, s.usuario, DATE_FORMAT(s.fecha_inicio, '%Y-%m-%dT%H:%i:%s') AS fecha_inicio, COALESCE(cl.cnombre, '') AS nombre_cliente, COALESCE(pr.cnombre, '') AS nombre_proveedor FROM pmmutileria.revisiones_barcode_sesion s LEFT JOIN clientes cl ON cl.cidcte = s.cidcte LEFT JOIN provs    pr ON pr.cidprov = s.cidprov WHERE s.cref_agrupada = @cref AND s.fecha_fin IS NULL ORDER BY s.id_sesion DESC LIMIT 1", new
            {
                cref = crefAgrupada
            });
            if (sesion == null)
            {
                _logger.LogInformation(
                    "BARCODE_TRACE GET_ESTADO sinSesion cref={Cref}",
                    crefAgrupada);
                return Ok(new
                {
                    success = true,
                    activa = false
                });
            }
            List<dynamic> lecturas = (await conn.QueryAsync<object>(" SELECT CASE WHEN l.existe = 1 THEN l.cidproduct WHEN s.barcode_long > 0 THEN LEFT(l.codigo_raw, s.barcode_long) ELSE l.codigo_raw END AS cidproduct, MAX(l.codigo_raw) AS codigoRaw, COUNT(DISTINCT l.id_lectura) AS cantidad, MAX(l.existe) AS existe, COALESCE(MAX(p.cdescripcio), '') AS descripcion, MAX(UNIX_TIMESTAMP(l.fecha_lectura) * 1000) AS ultimaLectura FROM pmmutileria.revisiones_barcode_lectura l INNER JOIN pmmutileria.revisiones_barcode_sesion s ON s.id_sesion = l.id_sesion LEFT JOIN products p ON p.cidproduct = l.cidproduct  AND p.cclienteid = @cidcte  AND p.cprovid    = @cidprov WHERE l.id_sesion = @id AND l.eliminado = 0 GROUP BY CASE WHEN l.existe = 1 THEN l.cidproduct WHEN s.barcode_long > 0 THEN LEFT(l.codigo_raw, s.barcode_long) ELSE l.codigo_raw END ORDER BY ultimaLectura DESC", new
            {
                id = (int)sesion.id_sesion,
                cidcte = (string)sesion.cidcte,
                cidprov = (string)sesion.cidprov
            })).ToList();
            List<dynamic> auditoria = (await conn.QueryAsync(@"
                SELECT
                    LEFT(l.codigo_raw, 120) AS CodigoRaw,
                    l.cidproduct AS Cidproduct,
                    l.existe AS Existe,
                    COUNT(*) AS Cantidad,
                    COUNT(DISTINCT NULLIF(l.cliente_scan_id, '')) AS ScanIds,
                    SUM(CASE WHEN NULLIF(l.cliente_scan_id, '') IS NULL THEN 1 ELSE 0 END) AS SinScanId
                FROM pmmutileria.revisiones_barcode_lectura l
                WHERE l.id_sesion = @id AND l.eliminado = 0
                GROUP BY l.codigo_raw, l.cidproduct, l.existe
                ORDER BY Cantidad DESC, CodigoRaw", new
            {
                id = (int)sesion.id_sesion
            })).ToList();
            string grupos = string.Join(" | ", lecturas.Select(l =>
                $"cid={Convert.ToString(l.cidproduct)};n={Convert.ToInt32(l.cantidad)};ex={Convert.ToInt32(l.existe)}"));
            string auditoriaTexto = string.Join(" | ", auditoria.Select(a =>
                $"raw={Convert.ToString(a.CodigoRaw)};cid={Convert.ToString(a.Cidproduct)};" +
                $"ex={Convert.ToInt32(a.Existe)};n={Convert.ToInt32(a.Cantidad)};" +
                $"ids={Convert.ToInt32(a.ScanIds)};sinId={Convert.ToInt32(a.SinScanId)}"));
            _logger.LogInformation(
                "BARCODE_TRACE GET_ESTADO cref={Cref} sesion={IdSesion} " +
                "total={Total} grupos={Grupos} auditoria={Auditoria}",
                crefAgrupada,
                (int)sesion.id_sesion,
                lecturas.Sum(l => Convert.ToInt32(l.cantidad)),
                grupos,
                auditoriaTexto);
            return Ok(new
            {
                success = true,
                activa = true,
                idSesion = (int)sesion.id_sesion,
                crefAgrupada = (string)sesion.cref_agrupada,
                cidcte = (string)sesion.cidcte,
                cidprov = (string)sesion.cidprov,
                barcodeLong = (int)sesion.barcode_long,
                nombreCliente = (string)sesion.nombre_cliente,
                nombreProveedor = (string)sesion.nombre_proveedor,
                fechaInicio = Convert.ToString(sesion.fecha_inicio),
                usuario = (string)sesion.usuario,
                lecturas = lecturas.Select((dynamic l) => new
                {
                    cidproduct = (string)l.cidproduct,
                    codigoRaw = (string)l.codigoRaw,
                    descripcion = (string)l.descripcion,
                    cantidad = (object)Convert.ToInt32(l.cantidad),
                    existe = (object)(Convert.ToInt32(l.existe) == 1),
                    ultimaLectura = (object)Convert.ToInt64(l.ultimaLectura)
                })
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "BarcodeController.GetEstado({Cref})", crefAgrupada);
            return StatusCode(500, new
            {
                success = false,
                message = "Error recuperando estado de sesión."
            });
        }
    }

    [HttpPost("iniciar-sesion")]
    public async Task<IActionResult> IniciarSesion([FromBody] IniciarSesionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CrefAgrupada))
        {
            return BadRequest(new
            {
                success = false,
                message = "crefAgrupada es requerida."
            });
        }
        try
        {
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            using IDbConnection conn = _db.CreateConnection();
            conn.Open();

            dynamic sesionExistente = await conn.QueryFirstOrDefaultAsync("SELECT id_sesion, cidcte, cidprov, barcode_long FROM pmmutileria.revisiones_barcode_sesion WHERE cref_agrupada = @cref AND fecha_fin IS NULL ORDER BY id_sesion DESC LIMIT 1", new { cref = req.CrefAgrupada });

            if (sesionExistente != null)
            {
                return Ok(new
                {
                    success = true,
                    idSesion = (int)sesionExistente.id_sesion,
                    cidcte = (string)sesionExistente.cidcte,
                    cidprov = (string)sesionExistente.cidprov,
                    barcodeLong = (int)sesionExistente.barcode_long
                });
            }

            ObjectResult cfgResult = (await GetConfig(req.CrefAgrupada)) as ObjectResult;
            if (cfgResult?.Value == null)
            {
                return cfgResult;
            }
            dynamic cfg = cfgResult.Value;
            if (!(bool)cfg.success)
            {
                return cfgResult;
            }
            int idSesion = await conn.ExecuteScalarAsync<int>(" INSERT INTO pmmutileria.revisiones_barcode_sesion   (cref_agrupada, cidcte, cidprov, barcode_long, usuario, fecha_inicio) VALUES   (@cref, @cidcte, @cidprov, @bl, @usuario, NOW()); SELECT LAST_INSERT_ID();", new
            {
                cref = req.CrefAgrupada,
                cidcte = (string)cfg.cidcte,
                cidprov = (string)cfg.cidprov,
                bl = (int)cfg.barcodeLong,
                usuario = usuario
            });

            await _hub.Clients.All.SendAsync("onActividadRecibida", req.CrefAgrupada, usuario, "BC-INICIO", "");
            await _activityService.RegistrarAsync(
                req.CrefAgrupada,
                usuario,
                RevisionActivityTypes.CambioActividad,
                detalle: "Inicio de sesión de lectura de códigos");

            return Ok(new
            {
                success = true,
                idSesion = idSesion,
                cidcte = (string)cfg.cidcte,
                cidprov = (string)cfg.cidprov,
                barcodeLong = (int)cfg.barcodeLong,
                nombreCliente = (string)cfg.nombreCliente,
                nombreProveedor = (string)cfg.nombreProveedor
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BarcodeController.IniciarSesion");
            return StatusCode(500, new
            {
                success = false,
                message = "Error iniciando sesión: " + ex.Message
            });
        }
    }

    [HttpPost("lectura")]
    public async Task<IActionResult> RegistrarLectura([FromBody] LecturaRequest req)
    {
        if (req.IdSesion <= 0 || string.IsNullOrWhiteSpace(req.CodigoRaw))
        {
            return BadRequest(new { success = false, message = "idSesion y codigoRaw son requeridos." });
        }
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            conn.Open();
            string clienteScanId = req.ClienteScanId?.Trim() ?? "";
            if (clienteScanId.Length > 36)
            {
                return BadRequest(new { success = false, message = "clienteScanId no puede superar 36 caracteres." });
            }
            _logger.LogInformation(
                "BARCODE_TRACE REGISTRAR START sesion={IdSesion} scanId={ScanId} " +
                "raw={Raw} rawLen={RawLength}",
                req.IdSesion,
                clienteScanId,
                req.CodigoRaw.Trim().Substring(0, Math.Min(120, req.CodigoRaw.Trim().Length)),
                req.CodigoRaw.Trim().Length);
            using var tran = conn.BeginTransaction();
            try
            {
                // Una lectura puede repetirse si la aplicación perdió la respuesta.
                // Bloquear la sesión serializa el reintento y evita insertar dos veces
                // el mismo clienteScanId sin depender de índices creados en ejecución.
                dynamic sesion = await conn.QueryFirstOrDefaultAsync(
                    "SELECT id_sesion, cref_agrupada, cidcte, cidprov, barcode_long, fecha_fin FROM pmmutileria.revisiones_barcode_sesion WHERE id_sesion = @id LIMIT 1 FOR UPDATE",
                    new { id = req.IdSesion }, tran);
                if ((object)sesion == null)
                {
                    tran.Rollback();
                    return NotFound(new { success = false, message = "Sesión no encontrada." });
                }

                object fechaFin = sesion.fecha_fin;
                if (fechaFin is not null && fechaFin != DBNull.Value)
                {
                    tran.Rollback();
                    return Conflict(new
                    {
                        success = false,
                        message = "La sesión de barcode ya fue finalizada y no acepta nuevas lecturas."
                    });
                }

                _logger.LogInformation(
                    "BARCODE_TRACE REGISTRAR session sesion={IdSesion} cref={Cref} " +
                    "barcodeLong={BarcodeLong} fechaFin={FechaFin}",
                    req.IdSesion,
                    (string)sesion.cref_agrupada,
                    (int)sesion.barcode_long,
                    Convert.ToString((object)sesion.fecha_fin));

                if (clienteScanId.Length > 0)
                {
                    dynamic lecturaExistente = await conn.QueryFirstOrDefaultAsync(
                        "SELECT cidproduct, existe FROM pmmutileria.revisiones_barcode_lectura WHERE id_sesion = @idSesion AND cliente_scan_id = @clienteScanId LIMIT 1",
                        new { idSesion = req.IdSesion, clienteScanId },
                        tran);
                    if (lecturaExistente != null)
                    {
                        tran.Commit();
                        _logger.LogWarning(
                            "BARCODE_TRACE REGISTRAR DUPLICADA sesion={IdSesion} scanId={ScanId} " +
                            "cid={Cidproduct} existe={Existe}",
                            req.IdSesion,
                            clienteScanId,
                            Convert.ToString((object)lecturaExistente.cidproduct),
                            Convert.ToInt32((object)lecturaExistente.existe));
                        return Ok(new
                        {
                            success = true,
                            duplicada = true,
                            cidproduct = (string)lecturaExistente.cidproduct,
                            existe = Convert.ToInt32(lecturaExistente.existe) == 1,
                            descripcion = ""
                        });
                    }
                }

                int len = (int)sesion.barcode_long;
                string raw = req.CodigoRaw.Trim();
                string codigoInterpretado = ((len > 0 && raw.Length >= len) ? raw.Substring(0, len) : raw);
                string descripcionLeida = ((len > 0 && raw.Length > len) ? raw.Substring(len).Trim() : "");

                dynamic prod = await conn.QueryFirstOrDefaultAsync(
"SELECT cidproduct, MAX(cdescripcio) AS descripcion FROM products WHERE cidproduct = @cidproduct AND cclienteid = @cidcte AND cprovid = @cidprov GROUP BY cidproduct LIMIT 1",
new { cidproduct = codigoInterpretado, cidcte = (string)sesion.cidcte, cidprov = (string)sesion.cidprov }, tran);
                bool existe = prod != null;
                string cidproduct = existe ? codigoInterpretado : "";
                string descripcion = existe ? (string)prod.descripcion : null;
                _logger.LogInformation(
                    "BARCODE_TRACE REGISTRAR catalogo sesion={IdSesion} scanId={ScanId} " +
                    "raw={Raw} interpretado={Interpretado} existe={Existe} cid={Cidproduct}",
                    req.IdSesion,
                    clienteScanId,
                    raw,
                    codigoInterpretado,
                    existe,
                    cidproduct);

                await conn.ExecuteAsync(
                    "INSERT INTO pmmutileria.revisiones_barcode_lectura (id_sesion, cliente_scan_id, cref_agrupada, cnofact, codigo_raw, cidproduct, descripcion_leida, existe, eliminado, fecha_lectura) VALUES (@idSesion, @clienteScanId, @cref, @cnofact, @raw, @cidproduct, @descLeida, @existe, 0, NOW())",
                    new { idSesion = req.IdSesion, clienteScanId = clienteScanId.Length == 0 ? null : clienteScanId, cref = (string)sesion.cref_agrupada, cnofact = (req.Cnofact ?? ""), raw = raw, cidproduct = cidproduct, descLeida = descripcionLeida, existe = (existe ? 1 : 0) }, tran);
                long idLectura = await conn.ExecuteScalarAsync<long>(
                    "SELECT LAST_INSERT_ID();",
                    transaction: tran);

                await RecalculateSessionTotalsAsync(conn, tran, req.IdSesion);

                tran.Commit();

                dynamic auditoria = await conn.QueryFirstAsync(
                    "SELECT COUNT(*) AS Filas, COUNT(DISTINCT NULLIF(cliente_scan_id, '')) AS ScanIds, SUM(CASE WHEN NULLIF(cliente_scan_id, '') IS NULL THEN 1 ELSE 0 END) AS SinScanId FROM pmmutileria.revisiones_barcode_lectura WHERE id_sesion = @id AND eliminado = 0",
                    new { id = req.IdSesion });
                _logger.LogInformation(
                    "BARCODE_TRACE REGISTRAR COMMIT sesion={IdSesion} lectura={IdLectura} " +
                    "scanId={ScanId} totalFilas={Filas} scanIds={ScanIds} sinScanId={SinScanId}",
                    req.IdSesion,
                    idLectura,
                    clienteScanId,
                    Convert.ToInt32((object)auditoria.Filas),
                    Convert.ToInt32((object)auditoria.ScanIds),
                    Convert.ToInt32((object)auditoria.SinScanId));

                await _hub.Clients.All.SendAsync("onActividadRecibida", (string)sesion.cref_agrupada, base.User.Identity?.Name ?? "Desconocido", raw, descripcion ?? "NO EN CATALOGO");
                await _activityService.RegistrarAsync(
                    (string)sesion.cref_agrupada,
                    base.User.Identity?.Name ?? "Desconocido",
                    RevisionActivityTypes.LecturaCodigo,
                    detalle: descripcion ?? "Código leído");

                return Ok(new { success = true, duplicada = false, cidproduct = cidproduct, existe = existe, descripcion = (descripcion ?? "") });
            }
            catch
            {
                tran.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "BARCODE_TRACE REGISTRAR ERROR sesion={IdSesion} scanId={ScanId} raw={Raw}",
                req.IdSesion,
                req.ClienteScanId,
                req.CodigoRaw);
            return StatusCode(500, new { success = false, message = "Error registrando lectura: " + ex.Message });
        }
    }

    [HttpPost("restar-lectura")]
    [RequiereModulo(MobileModuleIds.Review, "Borrar")]
    public async Task<IActionResult> RestarLectura([FromBody] RestarLecturaRequest req)
    {
        if (req.IdSesion <= 0 || (string.IsNullOrWhiteSpace(req.Cidproduct) && string.IsNullOrWhiteSpace(req.CodigoRaw)))
        {
            return BadRequest(new
            {
                success = false,
                message = "idSesion y cidproduct o codigoRaw son requeridos."
            });
        }
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            conn.Open();
            using var tran = conn.BeginTransaction();
            dynamic sesion = await conn.QueryFirstOrDefaultAsync(" SELECT id_sesion, fecha_fin FROM pmmutileria.revisiones_barcode_sesion WHERE id_sesion = @id LIMIT 1 FOR UPDATE", new
            {
                id = req.IdSesion
            }, tran);
            if ((object)sesion == null)
            {
                tran.Rollback();
                return NotFound(new
                {
                    success = false,
                    message = "Sesión no encontrada."
                });
            }

            object fechaFin = sesion.fecha_fin;
            if (fechaFin is not null && fechaFin != DBNull.Value)
            {
                tran.Rollback();
                return Conflict(new
                {
                    success = false,
                    message = "La sesión de barcode ya fue finalizada y no permite correcciones."
                });
            }

            dynamic ultima = await conn.QueryFirstOrDefaultAsync(" SELECT id_lectura FROM pmmutileria.revisiones_barcode_lectura WHERE id_sesion  = @idSesion AND eliminado = 0 AND ((@cidproduct <> '' AND cidproduct = @cidproduct) OR (@codigoRaw <> '' AND codigo_raw = @codigoRaw)) ORDER BY id_lectura DESC LIMIT 1", new
            {
                idSesion = req.IdSesion,
                cidproduct = req.Cidproduct ?? "",
                codigoRaw = req.CodigoRaw ?? ""
            }, tran);
            if ((object)ultima == null)
            {
                tran.Rollback();
                return NotFound(new
                {
                    success = false,
                    message = "No hay lecturas de este producto en la sesión."
                });
            }
            await conn.ExecuteAsync("UPDATE pmmutileria.revisiones_barcode_lectura SET eliminado = 1 WHERE id_lectura = @id", new
            {
                id = (int)ultima.id_lectura
            }, tran);
            await RecalculateSessionTotalsAsync(conn, tran, req.IdSesion);
            tran.Commit();

            int restante = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM pmmutileria.revisiones_barcode_lectura WHERE id_sesion = @idSesion AND eliminado = 0 AND ((@cidproduct <> '' AND cidproduct = @cidproduct) OR (@codigoRaw <> '' AND codigo_raw = @codigoRaw))", new
            {
                idSesion = req.IdSesion,
                cidproduct = req.Cidproduct ?? "",
                codigoRaw = req.CodigoRaw ?? ""
            });
            return Ok(new
            {
                success = true,
                restante
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "BarcodeController.RestarLectura");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al restar la lectura."
            });
        }
    }

    [HttpGet("resumen/{idSesion}")]
    public async Task<IActionResult> GetResumen(int idSesion)
    {

        try
        {
            using IDbConnection conn = _db.CreateConnection();
            conn.Open();
            dynamic sesion = await conn.QueryFirstOrDefaultAsync(" SELECT s.id_sesion, s.cref_agrupada, s.cidcte, s.cidprov, s.barcode_long, s.usuario, DATE_FORMAT(s.fecha_inicio, '%Y-%m-%dT%H:%i:%s') AS fecha_inicio, CASE WHEN s.fecha_fin IS NULL THEN NULL ELSE DATE_FORMAT(s.fecha_fin, '%Y-%m-%dT%H:%i:%s') END AS fecha_fin, s.observaciones, s.total_lecturas, s.total_existentes, s.total_inexistentes, s.email_enviado, s.email_destinos, (SELECT cnombre FROM clientes WHERE cidcte = s.cidcte LIMIT 1)   AS nombre_cliente, (SELECT cnombre FROM provs    WHERE cidprov = s.cidprov LIMIT 1)  AS nombre_proveedor, (SELECT GROUP_CONCAT(DISTINCT l.cnofact SEPARATOR ', ')   FROM pmmutileria.revisiones_barcode_lectura l   WHERE l.id_sesion = s.id_sesion AND l.cnofact != '' AND l.eliminado = 0) AS facturas_escaneadas FROM pmmutileria.revisiones_barcode_sesion s WHERE s.id_sesion = @id", new
            {
                id = idSesion
            });
            if ((object)sesion == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Sesión no encontrada."
                });
            }
            string nombreUsuarioCompleto = Convert.ToString(sesion.usuario);
            try
            {
                using IDbConnection connRep = _db.CreateReportesConnection();
                string dbNombre = await connRep.ExecuteScalarAsync<string>("SELECT TRIM(CONCAT_WS(' ', TRIM(apaterno), TRIM(amaterno), TRIM(nombre))) FROM usuarios WHERE claveusuario = @usu LIMIT 1", new
                {
                    usu = (object)Convert.ToString(sesion.usuario)
                });
                if (!string.IsNullOrEmpty(dbNombre))
                {
                    nombreUsuarioCompleto = dbNombre;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("No se pudo obtener nombre completo para {usu}: {Msg}", (object)sesion.usuario, ex.Message);
            }
            List<dynamic> filas = (await conn.QueryAsync(" SELECT CASE WHEN l.existe = 1 THEN l.cidproduct WHEN s.barcode_long > 0 THEN LEFT(l.codigo_raw, s.barcode_long) ELSE l.codigo_raw END AS Cidproduct, MAX(l.codigo_raw) AS CodigoRaw, COUNT(DISTINCT l.id_lectura) AS Cantidad, MAX(l.existe) AS ExisteInt, COALESCE(MAX(p.cdescripcio), '') AS Descripcion FROM pmmutileria.revisiones_barcode_lectura l INNER JOIN pmmutileria.revisiones_barcode_sesion s ON s.id_sesion = l.id_sesion LEFT JOIN products p ON p.cidproduct = l.cidproduct  AND p.cclienteid = @cidcte  AND p.cprovid    = @cidprov WHERE l.id_sesion = @id AND l.eliminado = 0 GROUP BY CASE WHEN l.existe = 1 THEN l.cidproduct WHEN s.barcode_long > 0 THEN LEFT(l.codigo_raw, s.barcode_long) ELSE l.codigo_raw END ORDER BY Cantidad DESC, Cidproduct", new
            {
                id = idSesion,
                cidcte = (object)Convert.ToString(sesion.cidcte),
                cidprov = (object)Convert.ToString(sesion.cidprov)
            })).ToList();
            List<ResumenProducto> productos = filas.Select((dynamic f) => new ResumenProducto
            {
                Cidproduct = Convert.ToString(f.Cidproduct),
                Descripcion = Convert.ToString(f.Descripcion ?? ""),
                Cantidad = Convert.ToInt32(f.Cantidad),
                Existe = (Convert.ToInt32(f.ExisteInt) == 1)
            }).ToList();

            // Bug fix — Totales: calculamos desde los registros reales en lugar de los
            // contadores en la cabecera de sesión, que pueden desincronizarse con borrados.
            List<dynamic> auditoria = (await conn.QueryAsync(@"
                SELECT
                    LEFT(l.codigo_raw, 120) AS CodigoRaw,
                    l.cidproduct AS Cidproduct,
                    l.existe AS Existe,
                    COUNT(*) AS Cantidad,
                    COUNT(DISTINCT NULLIF(l.cliente_scan_id, '')) AS ScanIds,
                    SUM(CASE WHEN NULLIF(l.cliente_scan_id, '') IS NULL THEN 1 ELSE 0 END) AS SinScanId
                FROM pmmutileria.revisiones_barcode_lectura l
                WHERE l.id_sesion = @id AND l.eliminado = 0
                GROUP BY l.codigo_raw, l.cidproduct, l.existe
                ORDER BY Cantidad DESC, CodigoRaw", new
            {
                id = idSesion
            })).ToList();
            string grupos = string.Join(" | ", productos.Select(p =>
                $"cid={p.Cidproduct};n={p.Cantidad};ex={p.Existe}"));
            string auditoriaTexto = string.Join(" | ", auditoria.Select(a =>
                $"raw={Convert.ToString(a.CodigoRaw)};cid={Convert.ToString(a.Cidproduct)};" +
                $"ex={Convert.ToInt32(a.Existe)};n={Convert.ToInt32(a.Cantidad)};" +
                $"ids={Convert.ToInt32(a.ScanIds)};sinId={Convert.ToInt32(a.SinScanId)}"));
            int realTotalLecturas     = productos.Sum(p => p.Cantidad);
            int realTotalExistentes   = productos.Where(p => p.Existe).Sum(p => p.Cantidad);
            int realTotalInexistentes = productos.Where(p => !p.Existe).Sum(p => p.Cantidad);
            _logger.LogInformation(
                "BARCODE_TRACE GET_RESUMEN sesion={IdSesion} cref={Cref} " +
                "cabeceraTotal={CabeceraTotal} totalReal={TotalReal} grupos={Grupos} auditoria={Auditoria}",
                idSesion,
                Convert.ToString((object)sesion.cref_agrupada),
                Convert.ToInt32((object)sesion.total_lecturas),
                realTotalLecturas,
                grupos,
                auditoriaTexto);

            return Ok(new
            {
                success = true,
                sesion = new
                {
                    idSesion = (object)Convert.ToInt32(sesion.id_sesion),
                    crefAgrupada = (object)Convert.ToString(sesion.cref_agrupada),
                    cidcte = (object)Convert.ToString(sesion.cidcte),
                    cidprov = (object)Convert.ToString(sesion.cidprov),
                    nombreCliente = (object)Convert.ToString(sesion.nombre_cliente ?? ""),
                    nombreProveedor = (object)Convert.ToString(sesion.nombre_proveedor ?? ""),
                    usuario = (object)Convert.ToString(sesion.usuario),
                    fechaInicio = (object)Convert.ToString(sesion.fecha_inicio),
                    fechaFin = sesion.fecha_fin == null ? null : Convert.ToString(sesion.fecha_fin),
                    totalLecturas     = (object)realTotalLecturas,
                    totalExistentes   = (object)realTotalExistentes,
                    totalInexistentes = (object)realTotalInexistentes,
                    emailEnviado = (object)(Convert.ToInt32(sesion.email_enviado) == 1),
                    emailDestinos = (object)Convert.ToString(sesion.email_destinos ?? ""),
                    facturasEscaneadas = (object)Convert.ToString(sesion.facturas_escaneadas ?? ""),
                    observaciones = (object)Convert.ToString(sesion.observaciones ?? ""),
                    nombreUsuarioCompleto = nombreUsuarioCompleto
                },
                productos = productos
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "BarcodeController.GetResumen({Id})", idSesion);
            return StatusCode(500, new
            {
                success = false,
                message = "Error obteniendo resumen."
            });
        }
    }

    [HttpPost("finalizar/{idSesion}")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> Finalizar(int idSesion, [FromBody] FinalizarRequest req)
    {
        try
        {
            string usuarioCierre = base.User.Identity?.Name ?? "Desconocido";
            using (IDbConnection conn = _db.CreateConnection())
            {
                conn.Open();
                using var tran = conn.BeginTransaction();
                dynamic sesion = await conn.QueryFirstOrDefaultAsync(
                    "SELECT id_sesion, fecha_fin FROM pmmutileria.revisiones_barcode_sesion WHERE id_sesion = @id LIMIT 1 FOR UPDATE",
                    new { id = idSesion },
                    tran);
                if ((object)sesion == null)
                {
                    tran.Rollback();
                    return NotFound(new
                    {
                        success = false,
                        message = "Sesión no encontrada."
                    });
                }

                object fechaFin = sesion.fecha_fin;
                if (fechaFin is not null && fechaFin != DBNull.Value)
                {
                    tran.Rollback();
                    return Conflict(new
                    {
                        success = false,
                        message = "La sesión de barcode ya fue finalizada."
                    });
                }

                // Bug fix — Usuario: guardamos quien CIERRA la sesión (puede ser distinto
                // de quien la inició si otra persona entró y finalizó la revisión).
                // Bug fix — Totales: recalculamos desde registros reales antes de cerrar,
                // para que la cabecera quede en sincronía aunque haya habido borrados.
                await conn.ExecuteAsync(@"
 UPDATE pmmutileria.revisiones_barcode_sesion s
 SET fecha_fin         = NOW(),
    observaciones      = @obs,
    usuario            = @usuario,
    total_lecturas     = (SELECT COUNT(*)                         FROM pmmutileria.revisiones_barcode_lectura WHERE id_sesion = @id AND eliminado = 0),
    total_existentes   = (SELECT COUNT(*) FROM pmmutileria.revisiones_barcode_lectura WHERE id_sesion = @id AND eliminado = 0 AND existe = 1),
    total_inexistentes = (SELECT COUNT(*) FROM pmmutileria.revisiones_barcode_lectura WHERE id_sesion = @id AND eliminado = 0 AND existe = 0)
 WHERE id_sesion = @id",
                    new { id = idSesion, obs = req.Observaciones ?? "", usuario = usuarioCierre },
                    tran);

                // La revisión por códigos de barras no tiene concepto de "pausar":
                // a diferencia de la revisión normal (TimerController.FinalizarTimer),
                // aquí Finalizar siempre es un cierre real. Antes esto nunca marcaba
                // traf.status/traf3 -- bodega tenía que cerrar "revisada físicamente"
                // a mano cada vez que el revisor usaba este modo en vez del normal.
                string crefAgrupada = (string)sesion.cref_agrupada;
                if (!string.IsNullOrWhiteSpace(crefAgrupada))
                {
                    await _normalCompletionService.SincronizarSistemaLegadoAsync(conn, crefAgrupada, tran);
                }

                tran.Commit();
            }

            ObjectResult resumenResult = (await GetResumen(idSesion)) as ObjectResult;
            if (resumenResult?.Value == null)
            {
                return resumenResult;
            }

            dynamic resumen = resumenResult.Value;
            if (!(bool)resumen.success)
            {
                return resumenResult;
            }

            if (req.EnviarCorreo)
            {
                _ = Task.Run(async () => await EnviarCorreoFinalizacionAsync(idSesion));
            }

            return Ok(new
            {
                success = true,
                resumen = resumen.sesion,
                productos = resumen.productos,
                correo = new
                {
                    intentado = req.EnviarCorreo,
                    enviado = false,
                    enProceso = req.EnviarCorreo
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BarcodeController.Finalizar({Id})", idSesion);
            return StatusCode(500, new
            {
                success = false,
                message = "Error finalizando la sesión."
            });
        }
    }

    private async Task EnviarCorreoFinalizacionAsync(int idSesion)
    {
        try
        {
            bool correoOk = await _reportService.EnviarReporteBarcodeAsync(idSesion);
            using IDbConnection conn = _db.CreateConnection();
            conn.Open();
            if (correoOk)
            {
                await conn.ExecuteAsync(
                    "UPDATE pmmutileria.revisiones_barcode_sesion SET email_enviado = 1 WHERE id_sesion = @id",
                    new { id = idSesion });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EnviarCorreoFinalizacionAsync({Id})", idSesion);
        }
    }

    [HttpGet("estado-correo/{idSesion}")]
    public async Task<IActionResult> GetEstadoCorreo(int idSesion)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            conn.Open();
            dynamic sesion = await conn.QueryFirstOrDefaultAsync(
                "SELECT id_sesion, email_enviado, email_destinos FROM pmmutileria.revisiones_barcode_sesion WHERE id_sesion = @id",
                new { id = idSesion });
            if (sesion == null)
            {
                return NotFound(new { success = false, message = "Sesión no encontrada." });
            }
            int enviado = Convert.ToInt32(sesion.email_enviado);
            string destinos = Convert.ToString(sesion.email_destinos ?? "");
            List<string> destinatarios = string.IsNullOrWhiteSpace(destinos)
                ? new List<string>()
                : destinos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            return Ok(new
            {
                success = true,
                idSesion = (int)sesion.id_sesion,
                emailEnviado = enviado == 1,
                destinatarios
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BarcodeController.GetEstadoCorreo({Id})", idSesion);
            return StatusCode(500, new { success = false, message = "Error consultando estado del correo." });
        }
    }

    [HttpPost("eliminar-producto-completo")]
    [RequiereModulo(MobileModuleIds.Review, "Borrar")]
    public async Task<IActionResult> EliminarProductoCompleto([FromBody] RestarLecturaRequest req)
    {
        if (req.IdSesion <= 0 || (string.IsNullOrWhiteSpace(req.Cidproduct) && string.IsNullOrWhiteSpace(req.CodigoRaw)))
        {
            return BadRequest(new
            {
                success = false,
                message = "idSesion y cidproduct o codigoRaw son requeridos."
            });
        }
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            conn.Open();
            using var tran = conn.BeginTransaction();
            dynamic sesion = await conn.QueryFirstOrDefaultAsync(
                "SELECT id_sesion, fecha_fin FROM pmmutileria.revisiones_barcode_sesion WHERE id_sesion = @id LIMIT 1 FOR UPDATE",
                new { id = req.IdSesion },
                tran);
            if ((object)sesion == null)
            {
                tran.Rollback();
                return NotFound(new
                {
                    success = false,
                    message = "Sesión no encontrada."
                });
            }

            object fechaFin = sesion.fecha_fin;
            if (fechaFin is not null && fechaFin != DBNull.Value)
            {
                tran.Rollback();
                return Conflict(new
                {
                    success = false,
                    message = "La sesión de barcode ya fue finalizada y no permite correcciones."
                });
            }

            dynamic stats = await conn.QueryFirstOrDefaultAsync<object>(" SELECT    COUNT(*) as total,   SUM(IF(existe = 1, 1, 0)) as existentes,   SUM(IF(existe = 0, 1, 0)) as inexistentes FROM pmmutileria.revisiones_barcode_lectura WHERE id_sesion = @idSesion AND eliminado = 0 AND ((@cidproduct <> '' AND cidproduct = @cidproduct) OR (@codigoRaw <> '' AND codigo_raw = @codigoRaw))", new
            {
                idSesion = req.IdSesion,
                cidproduct = req.Cidproduct ?? "",
                codigoRaw = req.CodigoRaw ?? ""
            }, tran);
            if (stats == null || stats.total == 0)
            {
                tran.Rollback();
                return NotFound(new
                {
                    success = false,
                    message = "No se encontraron lecturas para este producto."
                });
            }
            await conn.ExecuteAsync("UPDATE pmmutileria.revisiones_barcode_lectura SET eliminado = 1 WHERE id_sesion = @idSesion AND eliminado = 0 AND ((@cidproduct <> '' AND cidproduct = @cidproduct) OR (@codigoRaw <> '' AND codigo_raw = @codigoRaw))", new
            {
                idSesion = req.IdSesion,
                cidproduct = req.Cidproduct ?? "",
                codigoRaw = req.CodigoRaw ?? ""
            }, tran);
            await RecalculateSessionTotalsAsync(conn, tran, req.IdSesion);
            tran.Commit();
            return Ok(new
            {
                success = true
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "BarcodeController.EliminarProductoCompleto");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al eliminar el producto."
            });
        }
    }
}
