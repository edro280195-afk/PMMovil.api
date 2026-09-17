using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Attributes;
using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Flujo de validación del reporte de revisión física por dirección, tal
/// como quedó diseñado: Generado -> Enviado a validar (aquí se congela el
/// PDF con folio y versión) -> En revisión -> Aprobado/Rechazado con
/// comentario obligatorio si se rechaza. Modelado sobre el mismo patrón que
/// ya usa el flujo de Autorizaciones (solicitar -> autorizar/rechazar), con
/// el mismo WarehouseHub y el mismo SendToUserOrInboxAsync que ya usa
/// Asignaciones/asignar -- ningún canal nuevo que mantener.
///
/// Requiere el módulo 164 (ver MobileModuleIds.ReportValidation), pendiente
/// de alta en pmmtraf.modulos -- scripts/05-registrar-modulo-validacion-reportes-164.sql.
/// </summary>
[Authorize]
[RequiereModulo(MobileModuleIds.ReportValidation)]
[ApiController]
[Route("api/[controller]")]
public class ValidacionReportesController : ControllerBase
{
    private readonly BaseRepository _db;
    private readonly IHubContext<WarehouseHub> _hub;
    private readonly ILogger<ValidacionReportesController> _logger;
    private readonly IRealtimeNotificationService _notifications;
    private readonly RevisionReportPdfService _pdfService;
    private readonly IReportShareService _reportShareService;

    private const string CreateTableSql = @"
        CREATE TABLE IF NOT EXISTS pmmutileria.revision_reportes_validacion (
            id INT AUTO_INCREMENT PRIMARY KEY,
            cref_agrupada VARCHAR(50) NOT NULL,
            version INT NOT NULL DEFAULT 1,
            folio VARCHAR(60) NOT NULL,
            estatus TINYINT NOT NULL DEFAULT 1,
            pdf LONGBLOB NOT NULL,
            usuario_genera VARCHAR(50) NOT NULL,
            fecha_generado DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            usuario_valida VARCHAR(50) NULL,
            fecha_validacion DATETIME NULL,
            comentario VARCHAR(1000) NULL,
            INDEX idx_cref (cref_agrupada),
            INDEX idx_estatus (estatus)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

    public ValidacionReportesController(
        BaseRepository db,
        IHubContext<WarehouseHub> hub,
        ILogger<ValidacionReportesController> logger,
        IRealtimeNotificationService notifications,
        RevisionReportPdfService pdfService,
        IReportShareService reportShareService)
    {
        _db = db;
        _hub = hub;
        _logger = logger;
        _notifications = notifications;
        _pdfService = pdfService;
        _reportShareService = reportShareService;
    }

    // Estatus: 1 = en validación, 2 = aprobado, 3 = rechazado.

    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    [HttpPost("enviar/{creferencia}")]
    public async Task<IActionResult> EnviarAValidar(string creferencia)
    {
        try
        {
            string usuario = base.User.Identity?.Name ?? "Supervisor";
            using IDbConnection conn = _db.CreateConnection();
            await conn.ExecuteAsync(CreateTableSql);

            (byte[]? pdf, string? error) = await _pdfService.GenerarPdfAsync(creferencia);
            if (pdf == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = error ?? "No se pudo generar el reporte."
                });
            }

            int version = 1 + await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM pmmutileria.revision_reportes_validacion WHERE cref_agrupada = @cref",
                new { cref = creferencia });
            string folio = $"RV-{creferencia}-v{version}";

            int id = await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO pmmutileria.revision_reportes_validacion
                    (cref_agrupada, version, folio, estatus, pdf, usuario_genera)
                VALUES (@cref, @version, @folio, 1, @pdf, @usuario);
                SELECT LAST_INSERT_ID();",
                new { cref = creferencia, version, folio, pdf, usuario });

            List<string> validadores = (await conn.QueryAsync<string>(@"
                SELECT DISTINCT TRIM(u.claveusuario)
                FROM usuarios u
                JOIN accesos a ON a.Fk_UsuarioID = u.usuarioid
                WHERE a.Fk_ModuloID = @modulo AND COALESCE(a.Lectura, 0) = 1 AND u.inactivo = 0",
                new { modulo = MobileModuleIds.ReportValidation })).ToList();

            await _hub.Clients.All.SendAsync("onReporteEnviadoValidar", new
            {
                id,
                creferencia,
                folio,
                version,
                usuarioGenera = usuario,
                fecha = DateTime.Now.ToString("o")
            });

            foreach (string validador in validadores)
            {
                _ = _notifications.SendToUserOrInboxAsync(
                    validador,
                    "Reporte pendiente de validar",
                    $"{usuario} mandó a validar el reporte de {creferencia} ({folio}).",
                    new Dictionary<string, string>
                    {
                        ["type"] = "reporte_pendiente_validar",
                        ["idValidacion"] = id.ToString()
                    });
            }

            if (validadores.Count == 0)
            {
                _logger.LogWarning(
                    "EnviarAValidar: nadie tiene el módulo {Modulo} con lectura -- el reporte {Folio} quedó sin nadie a quien avisar.",
                    MobileModuleIds.ReportValidation, folio);
            }

            return Ok(new
            {
                success = true,
                id,
                folio,
                version
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en EnviarAValidar para {cref}", creferencia);
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo mandar el reporte a validar."
            });
        }
    }

    [HttpGet("pendientes")]
    public async Task<IActionResult> GetPendientes()
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            await conn.ExecuteAsync(CreateTableSql);
            var items = await conn.QueryAsync<object>(@"
                SELECT id, cref_agrupada AS creferencia, version, folio,
                       usuario_genera AS usuarioGenera,
                       DATE_FORMAT(fecha_generado, '%Y-%m-%dT%H:%i:%s') AS fechaGenerado
                FROM pmmutileria.revision_reportes_validacion
                WHERE estatus = 1
                ORDER BY fecha_generado ASC");
            return Ok(new
            {
                success = true,
                items
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetPendientes");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudieron consultar los reportes pendientes de validar."
            });
        }
    }

    [HttpGet("historial")]
    public async Task<IActionResult> GetHistorial([FromQuery] int pagina = 1, [FromQuery] int pageSize = 20)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            await conn.ExecuteAsync(CreateTableSql);
            pagina = Math.Max(1, pagina);
            pageSize = Math.Clamp(pageSize, 1, 100);
            int offset = (pagina - 1) * pageSize;

            long total = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM pmmutileria.revision_reportes_validacion WHERE estatus IN (2, 3)");

            var items = await conn.QueryAsync<object>(@"
                SELECT id, cref_agrupada AS creferencia, version, folio, estatus,
                       usuario_genera AS usuarioGenera,
                       usuario_valida AS usuarioValida,
                       comentario,
                       DATE_FORMAT(fecha_generado, '%Y-%m-%dT%H:%i:%s') AS fechaGenerado,
                       DATE_FORMAT(fecha_validacion, '%Y-%m-%dT%H:%i:%s') AS fechaValidacion
                FROM pmmutileria.revision_reportes_validacion
                WHERE estatus IN (2, 3)
                ORDER BY fecha_validacion DESC
                LIMIT @pageSize OFFSET @offset",
                new { pageSize, offset });

            return Ok(new
            {
                success = true,
                items,
                total,
                pagina,
                pageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetHistorial");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo consultar el historial de validaciones."
            });
        }
    }

    // Mismo arreglo que Revision/reporte/{cref}/link -- window.open() directo
    // a un endpoint [Authorize] nunca llevaba el token.
    [HttpPost("{id}/pdf/link")]
    public async Task<IActionResult> GenerarLinkPdf(int id)
    {
        try
        {
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            (string token, DateTime expira) = await _reportShareService.GenerarLinkCongeladoAsync(id, usuario);
            string url = $"{Request.Scheme}://{Request.Host}/api/publico/reporte/{token}.pdf";
            return Ok(new PMMovil.Api.Models.ReportShareLinkResponse { Success = true, Url = url, Expira = expira });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generando link de reporte congelado para {id}", id);
            return StatusCode(500, new PMMovil.Api.Models.ReportShareLinkResponse { Success = false, Message = "No se pudo generar el enlace" });
        }
    }

    [HttpGet("{id}/pdf")]
    public async Task<IActionResult> GetPdf(int id)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            dynamic? row = await conn.QueryFirstOrDefaultAsync<object>(
                "SELECT pdf AS Pdf, folio AS Folio FROM pmmutileria.revision_reportes_validacion WHERE id = @id",
                new { id });
            if (row == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "No se encontró ese reporte."
                });
            }
            byte[] pdfBytes = (byte[])row.Pdf;
            string folioNombre = (string)row.Folio;
            return File(pdfBytes, "application/pdf", $"{folioNombre}.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetPdf para {id}", id);
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo obtener el PDF."
            });
        }
    }

    [RequiereModulo(MobileModuleIds.ReportValidation, "Modificar")]
    [HttpPost("{id}/aprobar")]
    public async Task<IActionResult> Aprobar(int id)
    {
        return await Decidir(id, aprobar: true, comentario: null);
    }

    [RequiereModulo(MobileModuleIds.ReportValidation, "Modificar")]
    [HttpPost("{id}/rechazar")]
    public async Task<IActionResult> Rechazar(int id, [FromBody] RechazarReporteRequest request)
    {
        string comentario = request?.Comentario?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(comentario))
        {
            return BadRequest(new
            {
                success = false,
                message = "El comentario es obligatorio para rechazar un reporte."
            });
        }
        return await Decidir(id, aprobar: false, comentario: comentario);
    }

    private async Task<IActionResult> Decidir(int id, bool aprobar, string? comentario)
    {
        try
        {
            string validador = base.User.Identity?.Name ?? "Dirección";
            using IDbConnection conn = _db.CreateConnection();
            await conn.ExecuteAsync(CreateTableSql);

            dynamic? row = await conn.QueryFirstOrDefaultAsync<object>(
                "SELECT cref_agrupada AS Cref, folio AS Folio, usuario_genera AS UsuarioGenera, estatus AS Estatus FROM pmmutileria.revision_reportes_validacion WHERE id = @id",
                new { id });
            if (row == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "No se encontró ese reporte."
                });
            }
            if ((int)row.Estatus != 1)
            {
                return Conflict(new
                {
                    success = false,
                    message = "Ese reporte ya fue decidido por otro usuario."
                });
            }

            int nuevoEstatus = aprobar ? 2 : 3;
            int rows = await conn.ExecuteAsync(@"
                UPDATE pmmutileria.revision_reportes_validacion
                SET estatus = @estatus, usuario_valida = @validador, fecha_validacion = NOW(), comentario = @comentario
                WHERE id = @id AND estatus = 1",
                new { estatus = nuevoEstatus, validador, comentario, id });

            if (rows == 0)
            {
                return Conflict(new
                {
                    success = false,
                    message = "Ese reporte ya fue decidido por otro usuario."
                });
            }

            string cref = (string)row.Cref;
            string folio = (string)row.Folio;
            string usuarioGenera = (string)row.UsuarioGenera;

            await _hub.Clients.All.SendAsync("onReporteDecidido", new
            {
                id,
                creferencia = cref,
                folio,
                aprobado = aprobar,
                validador,
                comentario,
                fecha = DateTime.Now.ToString("o")
            });

            _ = _notifications.SendToUserOrInboxAsync(
                usuarioGenera,
                aprobar ? "Reporte aprobado" : "Reporte rechazado",
                aprobar
                    ? $"{validador} aprobó el reporte de {cref} ({folio})."
                    : $"{validador} rechazó el reporte de {cref} ({folio}): {comentario}",
                new Dictionary<string, string>
                {
                    ["type"] = "reporte_decidido",
                    ["idValidacion"] = id.ToString()
                });

            return Ok(new
            {
                success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error decidiendo el reporte {id}", id);
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo registrar la decisión."
            });
        }
    }
}

public class RechazarReporteRequest
{
    public string? Comentario { get; set; }
}
