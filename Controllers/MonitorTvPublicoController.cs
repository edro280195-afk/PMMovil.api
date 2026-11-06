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

using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
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
    private readonly BaseRepository _db;
    private readonly ILogger<MonitorTvPublicoController> _logger;

    public MonitorTvPublicoController(
        IMonitorTvLinkService tvLinkService,
        MetricasRevisionService metricasService,
        IRevisionShareService revisionShareService,
        BaseRepository db,
        ILogger<MonitorTvPublicoController> logger)
    {
        _tvLinkService = tvLinkService;
        _metricasService = metricasService;
        _revisionShareService = revisionShareService;
        _db = db;
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
        List<MonitorTvRevisionRow> items = new();
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
            List<EvidenciaRowDto> activeReviews = evidencia.Items
                .Where(IsReviewInProgress)
                .ToList();
            HashSet<string> references = activeReviews
                .Select(row => row.Creferencia)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, MonitorTvProgressRow> progressByReference = new(StringComparer.OrdinalIgnoreCase);
            if (references.Count > 0)
            {
                using IDbConnection conn = _db.CreateConnection();
                List<MonitorTvProgressRow> progressRows = (await conn.QueryAsync<MonitorTvProgressRow>(
                    new CommandDefinition(@"
                        SELECT
                            f.cref_agrupada AS Creferencia,
                            COUNT(DISTINCT f.cnofact) AS TotalFacturas,
                            COUNT(DISTINCT CASE WHEN COALESCE(f.status_rev, 0) = 1 THEN f.cnofact END) AS FacturasFinalizadas,
                            COUNT(DISTINCT rd.idrevdet) AS TotalPartidas,
                            COUNT(DISTINCT CASE WHEN COALESCE(rd.revisada, 0) = 1 THEN rd.idrevdet END) AS PartidasFinalizadas
                        FROM fact f
                        LEFT JOIN revdet rd ON rd.cnofact = f.cnofact
                        WHERE f.cref_agrupada IN @references
                        GROUP BY f.cref_agrupada",
                        new { references },
                        cancellationToken: cancellationToken))).ToList();

                progressByReference = progressRows.ToDictionary(row => row.Creferencia, StringComparer.OrdinalIgnoreCase);
            }

            Dictionary<string, string> currentInvoices = WarehouseHub.GetActivePresenceSnapshot()
                .Where(presence => references.Contains(presence.Entry)
                    && !string.IsNullOrWhiteSpace(presence.Invoice))
                .GroupBy(presence => presence.Entry, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(presence => presence.LastSeen)
                        .Select(presence => presence.Invoice)
                        .First(),
                    StringComparer.OrdinalIgnoreCase);

            items = activeReviews.Select(row =>
            {
                progressByReference.TryGetValue(row.Creferencia, out MonitorTvProgressRow? progress);
                currentInvoices.TryGetValue(row.Creferencia, out string? currentInvoice);

                return new MonitorTvRevisionRow
                {
                    Creferencia = row.Creferencia,
                    Cliente = row.Cliente,
                    Arribo = row.Arribo,
                    FechaFactura = row.FechaFactura,
                    Revisor = row.Revisor,
                    FechaInicioRevision = row.FechaInicioRevision,
                    FechaFinRevision = row.FechaFinRevision,
                    TotalPartidas = progress?.TotalPartidas ?? 0,
                    PartidasFinalizadas = progress?.PartidasFinalizadas ?? 0,
                    TotalFacturas = progress?.TotalFacturas ?? 0,
                    FacturasFinalizadas = progress?.FacturasFinalizadas ?? 0,
                    Discrepancias = row.Discrepancias,
                    FacturaEnRevision = currentInvoice,
                };
            }).ToList();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "No se pudieron cargar las revisiones para Monitor TV.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { success = false });
        }

        List<MonitorTvRevisionRow> orderedItems = items
            .OrderBy(row => row.FechaInicioRevision ?? DateTime.MaxValue)
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

    private static bool IsReviewInProgress(EvidenciaRowDto row)
        => row.FechaInicioRevision.HasValue && !row.FechaFinRevision.HasValue;

    private void SetPublicHeaders()
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private sealed class MonitorTvProgressRow
    {
        public MonitorTvProgressRow() { }

        public string Creferencia { get; set; } = string.Empty;
        public int TotalPartidas { get; set; }
        public int PartidasFinalizadas { get; set; }
        public int TotalFacturas { get; set; }
        public int FacturasFinalizadas { get; set; }
    }
}
