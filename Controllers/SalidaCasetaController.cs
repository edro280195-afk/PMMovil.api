using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[RequiereModulo(MobileModuleIds.GatePhysicalExit)]
[ApiController]
[Route("api/[controller]")]
public class SalidaCasetaController : ControllerBase
{
    private readonly SalidaCasetaService _service;
    private readonly ILogger<SalidaCasetaController> _logger;

    public SalidaCasetaController(SalidaCasetaService service, ILogger<SalidaCasetaController> logger)
    {
        _service = service;
        _logger = logger;
    }

    private string ObtenerUsuario() => User.Identity?.Name ?? "sistema";
    private string ObtenerPlanta() => User.FindFirst("Planta")?.Value ?? string.Empty;

    [HttpGet("pendientes")]
    public async Task<IActionResult> GetPendientes()
    {
        try
        {
            var pendientes = await _service.GetPendientesAsync(ObtenerPlanta());
            return Ok(new SalidaCajaPendientesResult { Success = true, Pendientes = pendientes });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener cajas pendientes de salida de caseta");
            return Ok(new SalidaCajaPendientesResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpGet("relacion")]
    public async Task<IActionResult> RevisarRelacion([FromQuery] string casetaId)
    {
        try
        {
            var relacion = await _service.RevisarRelacionAsync(casetaId ?? string.Empty);
            return Ok(new RevisarRelacionResult { Success = true, Relacion = relacion });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al revisar relacion de la caja {CasetaId}", casetaId);
            return Ok(new RevisarRelacionResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("registrar")]
    [RequiereModulo(MobileModuleIds.GatePhysicalExit, "Modificar")]
    public async Task<IActionResult> Registrar([FromBody] RegistrarSalidaCajaRequest request)
    {
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.CasetaId))
            {
                return Ok(new RegistrarSalidaCajaResult { Success = false, Message = "Falta el identificador de la caja." });
            }

            var result = await _service.RegistrarSalidaAsync(
                request.CasetaId,
                ObtenerUsuario(),
                request.Observaciones ?? string.Empty,
                ObtenerPlanta());

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al registrar salida de caja de caseta {CasetaId}", request?.CasetaId);
            return Ok(new RegistrarSalidaCajaResult { Success = false, Message = "Error: " + ex.Message });
        }
    }
}
