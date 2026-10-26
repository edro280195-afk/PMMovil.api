using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Pantalla unificada de salida de caseta. Comparte la ruta api/SalidaCaseta con
/// el controller original, pero NO hereda su [RequiereModulo(166)] de clase: aquí
/// se ve el patio con Lectura en CUALQUIERA de los dos módulos (165 salida con
/// relación, 166 salida manual) y cada acción exige el suyo.
/// </summary>
[Authorize]
[ApiController]
[Route("api/SalidaCaseta")]
public class SalidaPatioController : ControllerBase
{
    private readonly SalidaCasetaService _service;
    private readonly IModulePermissionService _permisos;
    private readonly ILogger<SalidaPatioController> _logger;

    public SalidaPatioController(
        SalidaCasetaService service,
        IModulePermissionService permisos,
        ILogger<SalidaPatioController> logger)
    {
        _service = service;
        _permisos = permisos;
        _logger = logger;
    }

    private string ObtenerUsuario() => User.Identity?.Name ?? string.Empty;
    private string ObtenerPlanta() => User.FindFirst("Planta")?.Value ?? string.Empty;

    [HttpGet("patio")]
    public async Task<IActionResult> GetPatio(CancellationToken ct)
    {
        string usuario = ObtenerUsuario();
        if (string.IsNullOrWhiteSpace(usuario)) return Unauthorized(new { message = "No se pudo identificar al usuario." });

        bool puedeVer =
            await _permisos.HasPermissionAsync(usuario, MobileModuleIds.GateRelationExit, "Lectura", ct)
            || await _permisos.HasPermissionAsync(usuario, MobileModuleIds.GatePhysicalExit, "Lectura", ct);
        if (!puedeVer) return Forbid();

        try
        {
            var resultado = await _service.GetPatioAsync(ObtenerPlanta());
            return Ok(resultado);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener el patio de salida de caseta");
            return Ok(new SalidaPatioResult { Success = false, Message = "No se pudo cargar el patio. Intenta de nuevo." });
        }
    }

    /// <summary>
    /// La relación ya salió y la caja sigue en el patio: cierra solo el registro.
    /// Es una operación de la salida con relación, por eso exige 165 Modificar.
    /// </summary>
    [HttpPost("cerrar-registro")]
    [RequiereModulo(MobileModuleIds.GateRelationExit, "Modificar", true)]
    public async Task<IActionResult> CerrarRegistro([FromBody] CerrarRegistroRequest request)
    {
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Relno))
                return Ok(new RegistrarSalidaCajaResult { Success = false, Message = "Falta el número de relación." });

            var resultado = await _service.CerrarRegistroAsync(request.Relno, ObtenerUsuario(), ObtenerPlanta());
            return Ok(resultado);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al cerrar el registro de patio de la relación {Relno}", request?.Relno);
            return Ok(new RegistrarSalidaCajaResult { Success = false, Message = "No se pudo cerrar el registro. Intenta de nuevo." });
        }
    }
}
