using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[RequiereModulo(MobileModuleIds.Reports, "Lectura")]
[ApiController]
[Route("api/reportes")]
public class ReportController : ControllerBase
{
    private readonly ReportService _reportService;
    private readonly ILogger<ReportController> _logger;

    public ReportController(ReportService reportService, ILogger<ReportController> logger)
    {
        _reportService = reportService;
        _logger = logger;
    }

    private string ObtenerUsuario() => User.Identity?.Name ?? "sistema";

    [HttpGet]
    public async Task<IActionResult> GetCatalogo()
    {
        try
        {
            var catalogo = await _reportService.ObtenerCatalogoAsync(ObtenerUsuario());
            return Ok(new { success = true, data = catalogo, total = catalogo.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener catálogo de reportes");
            return Ok(new { success = false, message = "Error al obtener catálogo: " + ex.Message });
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetInfo(string id)
    {
        try
        {
            var info = await _reportService.ObtenerInfoAsync(id, ObtenerUsuario());
            if (info == null)
                return Ok(new { success = false, message = "Reporte no encontrado." });

            return Ok(new { success = true, data = info });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener info del reporte {Id}", id);
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("filtros/clientes")]
    public async Task<IActionResult> GetClientes()
    {
        try
        {
            var clientes = await _reportService.ObtenerClientesAsync();
            return Ok(new { success = true, data = clientes });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener clientes para reporte");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("filtros/bodegas")]
    public async Task<IActionResult> GetBodegas()
    {
        try
        {
            var bodegas = await _reportService.ObtenerBodegasAsync();
            return Ok(new { success = true, data = bodegas });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener bodegas para reporte");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("generar")]
    public async Task<IActionResult> Generar([FromBody] GenerarReporteRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.IdReporte))
                return Ok(new { success = false, message = "IdReporte es requerido." });

            var result = await _reportService.GenerarAsync(request, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al generar reporte {Id}", request.IdReporte);
            return Ok(new ReporteResult
            {
                Success = false,
                IdReporte = request.IdReporte,
                Message = "Error al generar reporte: " + ex.Message
            });
        }
    }

    [HttpPost("excel")]
    public async Task<IActionResult> ExportarExcel([FromBody] GenerarReporteRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.IdReporte))
                return BadRequest(new { success = false, message = "IdReporte es requerido." });

            var bytes = await _reportService.ExportarExcelAsync(request, ObtenerUsuario());

            var nombre = $"Reporte_{request.IdReporte}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
            return File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                nombre);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al exportar Excel del reporte {Id}", request.IdReporte);
            return Ok(new { success = false, message = "Error al exportar: " + ex.Message });
        }
    }
}
