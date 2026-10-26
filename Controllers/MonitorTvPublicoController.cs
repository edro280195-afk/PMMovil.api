using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/publico/monitor-tv")]
public sealed class MonitorTvPublicoController : ControllerBase
{
    private const int RowsLimitPerDay = 5000;

    private readonly IMonitorTvLinkService _tvLinkService;
    private readonly MetricasRevisionService _metricasService;
    private readonly IRevisionShareService _revisionShareService;
    private readonly ILogger<MonitorTvPublicoController> _logger;

    public MonitorTvPublicoController(
        IMonitorTvLinkService tvLinkService,
        MetricasRevisionService metricasService,
        IRevisionShareService revisionShareService,
        ILogger<MonitorTvPublicoController> logger)
    {
        _tvLinkService = tvLinkService;
        _metricasService = metricasService;
        _revisionShareService = revisionShareService;
        _logger = logger;
    }

    [HttpGet("revisiones")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<IActionResult> GetRevisiones(
        [FromHeader(Name = "X-Monitor-TV-Token")] string? token,
        CancellationToken cancellationToken)
    {
        SetPublicHeaders();

        if (!TokenValido(token))
        {
            return Unauthorized(new { success = false });
        }

        MonitorTvLinkAccess? access = await _tvLinkService.ResolverAsync(token!, cancellationToken);
        if (access == null || access.TipoPantalla != "OPERACION_NLD")
        {
            return Unauthorized(new { success = false });
        }

        DateTime hoy = GetMatamorosNow().Date;
        DateTime ayer = hoy.AddDays(-1);
        var items = new List<MonitorTvRevisionRow>();
        bool truncado = false;

        try
        {
            EvidenciaResponse evidencia = await _metricasService.ObtenerEvidenciaAsync(
                ayer,
                hoy,
                planta: null,
                cliente: null,
                revisor: null,
                origen: null,
                crossFilter: null,
                limite: RowsLimitPerDay * 2,
                includeActiveReviews: true,
                cancellationToken: cancellationToken);

            truncado = evidencia.Truncado;
            items.AddRange(evidencia.Items
                .Where(row => !row.TieneFactura || row.FacturasPendientesRevision > 0)
                .Select(row => new MonitorTvRevisionRow
            {
                Creferencia = row.Creferencia,
                Cliente = row.Cliente,
                Arribo = row.Arribo,
                FechaFactura = row.FechaFactura,
                TieneFactura = row.TieneFactura,
                Manejo = row.Manejo,
                Estatus = row.Estatus,
                Origen = row.Origen,
                Revisor = row.Revisor,
                FechaInicioRevision = row.FechaInicioRevision,
                FechaFinRevision = row.FechaFinRevision,
            }));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "No se pudieron cargar las revisiones para Monitor TV.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { success = false });
        }

        List<MonitorTvRevisionRow> orderedItems = items
            .Where(EsPendienteRevisionFisica)
            .OrderBy(PrioridadMonitorNld)
            .ThenBy(OrdenSecundarioMonitorNld)
            .ThenBy(row => row.Creferencia, StringComparer.Ordinal)
            .ToList();

        return Ok(new MonitorTvRevisionesResponse
        {
            Success = true,
            Total = orderedItems.Count,
            Truncado = truncado,
            Items = orderedItems,
        });
    }

    [HttpGet("imex/revisiones")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<IActionResult> GetRevisionesImex(
        [FromHeader(Name = "X-Monitor-TV-Token")] string? token,
        CancellationToken cancellationToken)
    {
        SetPublicHeaders();
        if (!TokenValido(token)) return Unauthorized(new { success = false });

        MonitorTvLinkAccess? access = await _tvLinkService.ResolverAsync(token!, cancellationToken);
        if (access == null || access.TipoPantalla != "DESEMPENO_IMEX")
        {
            return Unauthorized(new { success = false });
        }

        DateTime hoy = GetMatamorosNow().Date;
        DateTime ayer = hoy.AddDays(-1);
        try
        {
            ImexMonitorResponse response = await _metricasService.GetImexMonitorDataAsync(
                ayer,
                hoy,
                RowsLimitPerDay,
                cancellationToken);
            return Ok(response);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "No se pudieron cargar las revisiones y el marcador del turno para el monitor de IMEX.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { success = false });
        }
    }

    [HttpGet("eventos")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<IActionResult> GetEventos(
        [FromHeader(Name = "X-Monitor-TV-Token")] string? token,
        [FromQuery] long afterId = 0,
        CancellationToken cancellationToken = default)
    {
        SetPublicHeaders();
        if (!TokenValido(token) || afterId < 0) return Unauthorized(new { success = false });

        MonitorTvLinkAccess? access = await _tvLinkService.ResolverAsync(token!, cancellationToken);
        if (access == null || access.TipoPantalla is not ("OPERACION_NLD" or "DESEMPENO_IMEX"))
        {
            return Unauthorized(new { success = false });
        }

        try
        {
            DateTime hoy = GetMatamorosNow().Date;
            MonitorTvActivityReplayResponse response = await _metricasService.ObtenerEventosMonitorAsync(
                afterId,
                hoy,
                cancellationToken: cancellationToken);
            return Ok(response);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "No se pudieron recuperar eventos para el monitor de revisión.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { success = false });
        }
    }

    [HttpGet("eventos/{eventId:long}/foto")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<IActionResult> GetFotoDeEvento(
        long eventId,
        [FromHeader(Name = "X-Monitor-TV-Token")] string? token,
        CancellationToken cancellationToken)
    {
        SetPublicHeaders();
        if (!TokenValido(token) || eventId <= 0) return Unauthorized();

        MonitorTvLinkAccess? access = await _tvLinkService.ResolverAsync(token!, cancellationToken);
        if (access == null || access.TipoPantalla is not ("OPERACION_NLD" or "DESEMPENO_IMEX"))
        {
            return Unauthorized();
        }

        try
        {
            (string FilePath, string Extension)? photo = await _revisionShareService.ObtenerFotoDeEventoMonitorAsync(
                eventId,
                GetMatamorosNow().Date);
            if (!photo.HasValue) return NotFound();
            string contentType = photo.Value.Extension.Equals("png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg";
            return PhysicalFile(photo.Value.FilePath, contentType);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No se pudo servir evidencia de revisión para el evento {EventId}.", eventId);
            return NotFound();
        }
    }

    [HttpPost("ticket")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<IActionResult> CrearTicketTiempoReal(
        [FromHeader(Name = "X-Monitor-TV-Token")] string? token,
        CancellationToken cancellationToken)
    {
        SetPublicHeaders();
        if (!TokenValido(token)) return Unauthorized(new { success = false });

        MonitorTvLinkAccess? access = await _tvLinkService.ResolverAsync(token!, cancellationToken);
        if (access == null) return Unauthorized(new { success = false });

        try
        {
            string ticket = await _tvLinkService.EmitirTicketTiempoRealAsync(access.Id, cancellationToken);
            return Ok(new { success = true, ticket });
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new { success = false });
        }
    }

    private static bool TokenValido(string? token)
        => !string.IsNullOrWhiteSpace(token)
           && token.Length is >= 32 and <= 200
           && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static DateTime GetMatamorosNow()
    {
        string zoneId = OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Matamoros";
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
    }

    private static bool EsPendienteRevisionFisica(MonitorTvRevisionRow row)
    {
        string status = row.Estatus.Trim();
        return status.Equals("PENDIENTE REVISION FISICA", StringComparison.OrdinalIgnoreCase)
            || status.Equals("PENDIENTE REVISIÓN FÍSICA", StringComparison.OrdinalIgnoreCase);
    }

    private static int PrioridadMonitorNld(MonitorTvRevisionRow row)
    {
        if (!row.TieneFactura) return 0;
        return row.Estatus.StartsWith("PENDIENTE", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static long OrdenSecundarioMonitorNld(MonitorTvRevisionRow row)
    {
        // Dentro de cada prioridad, primero las entradas que llevan más tiempo esperando.
        return row.Arribo.Ticks;
    }

    private void SetPublicHeaders()
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
