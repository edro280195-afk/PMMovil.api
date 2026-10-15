using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Censo de racks por QR: escanear ubicación → barrer etiquetas → conciliar.
/// Vive bajo el módulo de Inventario (158), separado del inventario legacy de
/// `api/inventario`, que es global y sin sesión.
/// </summary>
[Authorize]
[ApiController]
[Route("api/Inventario/rack-census")]
[RequiereModulo(MobileModuleIds.Inventory)]
public sealed class RackCensusController(RackCensusService service) : ControllerBase
{
    /// <summary>Abre la sesión con la instantánea del inventario esperado.</summary>
    [HttpPost("sessions")]
    [RequiereModulo(MobileModuleIds.Inventory, "Agregar")]
    public async Task<IActionResult> Open([FromBody] RackCensusOpenRequest request, CancellationToken cancellationToken)
    {
        RackCensusResult result = await service.OpenAsync(request, User.Identity!.Name!, cancellationToken);
        return StatusCode(result.StatusCode, result.Body);
    }

    /// <summary>Recupera el recorrido para continuarlo en otro equipo o después de reinstalar.</summary>
    [HttpGet("sessions/{sessionId:guid}")]
    public async Task<IActionResult> Get(Guid sessionId, CancellationToken cancellationToken)
    {
        RackCensusResult result = await service.GetSessionAsync(sessionId, User.Identity!.Name!, cancellationToken);
        return StatusCode(result.StatusCode, result.Body);
    }

    /// <summary>Registra lecturas del barrido. Idempotente por identificador de lectura.</summary>
    [HttpPost("sessions/{sessionId:guid}/observations")]
    [RequiereModulo(MobileModuleIds.Inventory, "Agregar")]
    public async Task<IActionResult> Observe(
        Guid sessionId, [FromBody] RackCensusObserveRequest request, CancellationToken cancellationToken)
    {
        RackCensusResult result = await service.ObserveAsync(sessionId, request, User.Identity!.Name!, cancellationToken);
        return StatusCode(result.StatusCode, result.Body);
    }

    /// <summary>Movimientos ocurridos después del snapshot, para revisarlos antes de conciliar.</summary>
    [HttpGet("sessions/{sessionId:guid}/movements")]
    public async Task<IActionResult> Movements(Guid sessionId, CancellationToken cancellationToken)
    {
        RackCensusResult result = await service.GetMovementsAsync(sessionId, User.Identity!.Name!, cancellationToken);
        return StatusCode(result.StatusCode, result.Body);
    }

    /// <summary>
    /// Cierra el censo. Escribe la última observación física y los hallazgos;
    /// requiere modificar porque toca `etiqueta_mcia.time_inv`.
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/reconcile")]
    [RequiereModulo(MobileModuleIds.Inventory, "Modificar")]
    public async Task<IActionResult> Reconcile(
        Guid sessionId, [FromBody] RackCensusReconcileRequest request, CancellationToken cancellationToken)
    {
        RackCensusResult result = await service.ReconcileAsync(sessionId, request, User.Identity!.Name!, cancellationToken);
        return StatusCode(result.StatusCode, result.Body);
    }
}
