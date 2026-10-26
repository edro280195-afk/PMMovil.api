using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Historial y auditoría del panel (/panel/monitor?vista=historial): la
/// actividad de los revisadores por día y la historia completa de una
/// referencia. Las fechas viajan como yyyy-MM-dd, ambos extremos incluidos.
/// </summary>
[Authorize]
[ApiController]
[Route("api/Monitor/auditoria")]
public class AuditoriaRevisionController : ControllerBase
{
    private readonly RevisionAuditService _audit;
    private readonly ILogger<AuditoriaRevisionController> _logger;

    public AuditoriaRevisionController(RevisionAuditService audit, ILogger<AuditoriaRevisionController> logger)
    {
        _audit = audit;
        _logger = logger;
    }

    [HttpGet("revisores")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetRevisores()
    {
        try
        {
            IReadOnlyList<AuditReviewerDto> items = await _audit.GetReviewersAsync();
            return Ok(new { success = true, items });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetRevisores (auditoría)");
            return StatusCode(500, new { success = false, message = "No se pudo consultar la lista de revisores." });
        }
    }

    [HttpGet("resumen")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetResumen(
        [FromQuery] string? desde = null,
        [FromQuery] string? hasta = null,
        [FromQuery] string? usuario = null)
    {
        if (!TryReadRange(desde, hasta, out (DateTime Start, DateTime EndExclusive) range, out IActionResult? invalid))
        {
            return invalid!;
        }

        if (!string.IsNullOrWhiteSpace(usuario) && !ReviewerRoster.Contains(usuario))
        {
            return BadRequest(new { success = false, message = "El usuario indicado no es revisor." });
        }

        try
        {
            AuditSummaryDto summary = await _audit.GetSummaryAsync(range.Start, range.EndExclusive, usuario);
            return Ok(summary);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetResumen (auditoría)");
            return StatusCode(500, new { success = false, message = "No se pudo consultar la actividad de los revisores." });
        }
    }

    [HttpGet("entradas")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetEntradas(
        [FromQuery] string? usuario = null,
        [FromQuery] string? desde = null,
        [FromQuery] string? hasta = null)
    {
        if (string.IsNullOrWhiteSpace(usuario) || !ReviewerRoster.Contains(usuario))
        {
            return BadRequest(new { success = false, message = "Indica un revisor de la lista." });
        }

        if (!TryReadRange(desde, hasta, out (DateTime Start, DateTime EndExclusive) range, out IActionResult? invalid))
        {
            return invalid!;
        }

        try
        {
            IReadOnlyList<AuditEntryDto> items = await _audit.GetEntriesAsync(usuario, range.Start, range.EndExclusive);
            return Ok(new AuditEntriesDto { Items = items });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetEntradas (auditoría)");
            return StatusCode(500, new { success = false, message = "No se pudieron consultar las entradas del revisor." });
        }
    }

    [HttpGet("movimientos")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetMovimientos(
        [FromQuery] string? creferencia = null,
        [FromQuery] string? usuario = null,
        [FromQuery] string? desde = null,
        [FromQuery] string? hasta = null,
        [FromQuery] bool detalle = false,
        [FromQuery] long? despuesDe = null,
        [FromQuery] int limite = 50)
    {
        bool hasReference = !string.IsNullOrWhiteSpace(creferencia);
        if (!hasReference && string.IsNullOrWhiteSpace(usuario))
        {
            return BadRequest(new { success = false, message = "Indica una referencia o un usuario." });
        }

        DateTime? start = null;
        DateTime? endExclusive = null;

        // Una referencia se puede leer completa; sin ella, el rango es obligatorio
        // (por defecto hoy) para no recorrer toda la bitácora.
        if (!hasReference || !string.IsNullOrWhiteSpace(desde) || !string.IsNullOrWhiteSpace(hasta))
        {
            if (!TryReadRange(desde, hasta, out (DateTime Start, DateTime EndExclusive) range, out IActionResult? invalid))
            {
                return invalid!;
            }

            start = range.Start;
            endExclusive = range.EndExclusive;
        }

        try
        {
            AuditMovementsDto page = await _audit.GetMovementsAsync(new AuditMovementsQuery(
                creferencia,
                usuario,
                start,
                endExclusive,
                detalle,
                despuesDe,
                limite));
            return Ok(page);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetMovimientos (auditoría)");
            return StatusCode(500, new { success = false, message = "No se pudieron consultar los movimientos." });
        }
    }

    [HttpGet("referencia/{creferencia}")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetReferencia(string creferencia)
    {
        if (string.IsNullOrWhiteSpace(creferencia))
        {
            return BadRequest(new { success = false, message = "Indica una referencia." });
        }

        try
        {
            AuditReferenceDto? reference = await _audit.GetReferenceAsync(creferencia);
            return reference is null
                ? NotFound(new { success = false, message = "No encontramos esa entrada." })
                : Ok(reference);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetReferencia (auditoría) {Creferencia}", creferencia);
            return StatusCode(500, new { success = false, message = "No se pudo consultar la historia de la entrada." });
        }
    }

    private static bool TryReadRange(
        string? desde,
        string? hasta,
        out (DateTime Start, DateTime EndExclusive) range,
        out IActionResult? invalid)
    {
        range = default;
        invalid = null;

        if (!TryReadDay(desde, out DateOnly? from) || !TryReadDay(hasta, out DateOnly? to))
        {
            invalid = new BadRequestObjectResult(new { success = false, message = "Las fechas deben tener el formato aaaa-mm-dd." });
            return false;
        }

        try
        {
            range = RevisionAuditRules.NormalizeRange(from, to, DateOnly.FromDateTime(DateTime.Now));
            return true;
        }
        catch (ArgumentException exception)
        {
            invalid = new BadRequestObjectResult(new { success = false, message = exception.Message });
            return false;
        }
    }

    // Vacío significa "sin indicar"; un texto que no es fecha es un error.
    private static bool TryReadDay(string? value, out DateOnly? day)
    {
        day = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
        {
            return false;
        }

        day = parsed;
        return true;
    }
}
