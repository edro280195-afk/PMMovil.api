using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// La ficha de un código leído: de qué se trata y en qué punto de La Línea (caseta, entrada, revisión, relación,
/// salida) va. Solo consulta.
///
/// Lo puede pedir cualquiera con Lectura en algún módulo operativo; las acciones que ofrece la ficha siguen
/// limitadas por el permiso de cada pantalla a la que llevan.
///
/// El código viaja en el cuerpo, no en la ruta: el folio de caja lleva una diagonal (<c>E-017846/26</c>) y el QR de
/// pre-registro lleva un token, y ninguno debe acabar en los registros de acceso del servidor.
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
[RequiereModulo(
    MobileModuleIds.Gate,
    "Lectura",
    true,
    MobileModuleIds.Review,
    MobileModuleIds.WarehouseOperations,
    MobileModuleIds.WarehouseExit,
    MobileModuleIds.WarehouseRelation,
    MobileModuleIds.GateRelationExit,
    MobileModuleIds.GatePhysicalExit,
    MobileModuleIds.SystemsMonitor)]
public class TrazabilidadController : ControllerBase
{
    private readonly TrazabilidadService _service;
    private readonly ILogger<TrazabilidadController> _logger;

    public TrazabilidadController(TrazabilidadService service, ILogger<TrazabilidadController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// Resuelve un código a su ficha. 400 si el código no es de los que la ficha cubre (incluido el gafete, que
    /// jamás se consulta); 200 con <c>found = false</c> si tiene forma válida pero no existe.
    /// </summary>
    [HttpPost("resolver")]
    public async Task<IActionResult> Resolver([FromBody] TraceRequest? request, CancellationToken ct)
    {
        try
        {
            TraceResult result = await _service.ResolveAsync(request?.Code, ct);
            return result.Success ? Ok(result) : BadRequest(result);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            // No se registra el código: puede ser un QR de pre-registro con su token.
            _logger.LogError(ex, "Error al resolver la ficha de un código");
            return StatusCode(500, new TraceResult
            {
                Success = false,
                Reason = "ERROR_INTERNO"
            });
        }
    }
}
