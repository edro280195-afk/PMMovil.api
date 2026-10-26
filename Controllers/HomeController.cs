using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// El resumen del Tablero del inicio: una cifra por estación y su excepción. No lleva <c>RequiereModulo</c> de clase:
/// cada estación se calcula solo si el usuario tiene su módulo (ver <see cref="HomeSummaryRules.ModulesOf"/>), así que
/// quien no tiene ninguno recibe todo en <c>null</c> en vez de un 403 que rompa el inicio.
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class HomeController : ControllerBase
{
    private readonly HomeSummaryService _service;
    private readonly ILogger<HomeController> _logger;

    public HomeController(HomeSummaryService service, ILogger<HomeController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen(CancellationToken ct)
    {
        string username = User.Identity?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username))
            return Unauthorized(new { message = "No se pudo identificar al usuario." });

        try
        {
            string planta = User.FindFirst("Planta")?.Value ?? string.Empty;
            return Ok(await _service.GetAsync(username, planta, ct));
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al armar el resumen del Tablero");
            return StatusCode(500, new HomeSummaryResult { Success = false });
        }
    }
}
