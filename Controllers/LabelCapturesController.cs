using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/Revision/label-captures")]
[RequiereModulo(MobileModuleIds.Review)]
public sealed class LabelCapturesController(LabelCaptureService service) : ControllerBase
{
    [HttpGet("context/{idRevdet:int}")]
    public async Task<IActionResult> Context(int idRevdet, [FromQuery] string crefAgrupada, CancellationToken cancellationToken)
    {
        LabelCaptureResult result = await service.GetContextAsync(idRevdet, crefAgrupada,
            User.Identity!.Name!, cancellationToken);
        return StatusCode(result.StatusCode, result.Body);
    }

    // POST escribe campos y agrega evidencia: requiere ambos permisos.
    [HttpPost("confirm")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    [RequestSizeLimit(24 * 1024 * 1024)]
    public async Task<IActionResult> Confirm([FromBody] ConfirmLabelCaptureRequest request, CancellationToken cancellationToken)
    {
        LabelCaptureResult result = await service.ConfirmAsync(request, User.Identity!.Name!, cancellationToken);
        return StatusCode(result.StatusCode, result.Body);
    }
}
