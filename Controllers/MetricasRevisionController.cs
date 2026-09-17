using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Attributes;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Métricas y Estadísticas de Revisión — pestaña nueva de /panel/monitor.
/// Un solo endpoint de resumen calcula KPIs/embudo/tendencia/adopción/
/// antigüedad/revisores bajo el MISMO filtro (rango, planta, cliente,
/// revisor, origen y el cross-filter opcional que dispara un clic en
/// cualquier gráfica) para que nunca queden desincronizados entre sí —
/// pedido explícito de Eduardo, estilo Power BI.
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class MetricasRevisionController : ControllerBase
{
    private readonly MetricasRevisionService _service;
    private readonly ILogger<MetricasRevisionController> _logger;

    public MetricasRevisionController(MetricasRevisionService service, ILogger<MetricasRevisionController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet("resumen")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetResumen(
        [FromQuery] string? fechaInicio = null,
        [FromQuery] string? fechaFin = null,
        [FromQuery] string? planta = null,
        [FromQuery] string? cliente = null,
        [FromQuery] string? revisor = null,
        [FromQuery] string? origen = null,
        [FromQuery] string? crossFilter = null)
    {
        try
        {
            DateTime desde = string.IsNullOrEmpty(fechaInicio) ? DateTime.Today : DateTime.Parse(fechaInicio);
            DateTime hasta = string.IsNullOrEmpty(fechaFin) ? DateTime.Today : DateTime.Parse(fechaFin);
            var resultado = await _service.ObtenerResumenAsync(desde, hasta, planta, cliente, revisor, origen, crossFilter);
            return Ok(resultado);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetResumen (MetricasRevision)");
            return StatusCode(500, new { success = false, message = "No se pudo calcular el resumen de métricas de revisión." });
        }
    }

    [HttpGet("evidencia")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetEvidencia(
        [FromQuery] string? fechaInicio = null,
        [FromQuery] string? fechaFin = null,
        [FromQuery] string? planta = null,
        [FromQuery] string? cliente = null,
        [FromQuery] string? revisor = null,
        [FromQuery] string? origen = null,
        [FromQuery] string? crossFilter = null)
    {
        try
        {
            DateTime desde = string.IsNullOrEmpty(fechaInicio) ? DateTime.Today : DateTime.Parse(fechaInicio);
            DateTime hasta = string.IsNullOrEmpty(fechaFin) ? DateTime.Today : DateTime.Parse(fechaFin);
            var resultado = await _service.ObtenerEvidenciaAsync(desde, hasta, planta, cliente, revisor, origen, crossFilter);
            return Ok(resultado);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetEvidencia (MetricasRevision)");
            return StatusCode(500, new { success = false, message = "No se pudo consultar la evidencia." });
        }
    }

    [HttpGet("exportar")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> Exportar(
        [FromQuery] string? fechaInicio = null,
        [FromQuery] string? fechaFin = null,
        [FromQuery] string? planta = null,
        [FromQuery] string? cliente = null,
        [FromQuery] string? revisor = null,
        [FromQuery] string? origen = null,
        [FromQuery] string? crossFilter = null)
    {
        try
        {
            DateTime desde = string.IsNullOrEmpty(fechaInicio) ? DateTime.Today : DateTime.Parse(fechaInicio);
            DateTime hasta = string.IsNullOrEmpty(fechaFin) ? DateTime.Today : DateTime.Parse(fechaFin);
            string csv = await _service.ExportarCsvAsync(desde, hasta, planta, cliente, revisor, origen, crossFilter);
            byte[] bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
            string nombre = $"metricas-revision_{desde:yyyyMMdd}_{hasta:yyyyMMdd}.csv";
            return File(bytes, "text/csv", nombre);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en Exportar (MetricasRevision)");
            return StatusCode(500, new { success = false, message = "No se pudo generar el export." });
        }
    }

    [HttpGet("exportar-excel")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> ExportarExcel(
        [FromQuery] string? fechaInicio = null,
        [FromQuery] string? fechaFin = null,
        [FromQuery] string? planta = null,
        [FromQuery] string? cliente = null,
        [FromQuery] string? revisor = null,
        [FromQuery] string? origen = null,
        [FromQuery] string? crossFilter = null)
    {
        try
        {
            DateTime desde = string.IsNullOrEmpty(fechaInicio) ? DateTime.Today : DateTime.Parse(fechaInicio);
            DateTime hasta = string.IsNullOrEmpty(fechaFin) ? DateTime.Today : DateTime.Parse(fechaFin);
            byte[] bytes = await _service.ExportarXlsxAsync(desde, hasta, planta, cliente, revisor, origen, crossFilter);
            string nombre = $"metricas-revision_{desde:yyyyMMdd}_{hasta:yyyyMMdd}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", nombre);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en ExportarExcel (MetricasRevision)");
            return StatusCode(500, new { success = false, message = "No se pudo generar el export de Excel." });
        }
    }
}
