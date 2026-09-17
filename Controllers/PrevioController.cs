using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[RequiereModulo(MobileModuleIds.PreInspection)]
[ApiController]
[Route("api/[controller]")]
public class PrevioController : ControllerBase
{
    private readonly PrevioService _previoService;
    private readonly ILogger<PrevioController> _logger;

    public PrevioController(PrevioService previoService, ILogger<PrevioController> logger)
    {
        _previoService = previoService;
        _logger = logger;
    }

    private string ObtenerUsuario() => User.Identity?.Name ?? "sistema";

    [HttpGet("trafico/{texto}")]
    public async Task<IActionResult> BuscarTrafico(string texto)
    {
        try
        {
            var result = await _previoService.BuscarTraficoAsync(texto);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al buscar tráfico previo");
            return Ok(new BuscarTraficoResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("enviar")]
    [RequiereModulo(MobileModuleIds.PreInspection, "Modificar")]
    public async Task<IActionResult> EnviarNotificacion([FromBody] EnviarNotificacionRequest request)
    {
        try
        {
            var result = await _previoService.EnviarNotificacionAsync(
           request.Referencia, request.Observaciones, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al enviar notificación previa");
            return Ok(new PrevioResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("aviso")]
    public async Task<IActionResult> CrearAviso([FromBody] NuevoAvisoRequest request)
    {
        try
        {
            var result = await _previoService.EnviarAvisoAsync(request, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al crear aviso previo");
            return Ok(new PrevioResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpGet("clientes-exportacion")]
    public async Task<IActionResult> GetClientesExportacion([FromQuery] string? filtro)
    {
        try
        {
            var clientes = await _previoService.GetClientesExportacionAsync(filtro);
            return Ok(new { success = true, clientes });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener clientes exportación");
            return Ok(new { success = false, message = ex.Message });
        }
    }
}
