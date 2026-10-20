using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Grupos de cliente para la revisión por códigos de barras.
///
/// El GET de perfil lo consume la app móvil al abrir una entrada, así que pide
/// solo permiso de lectura de Revisión. El alta y la baja piden "Modificar":
/// apagar un grupo deja sin modo de lectura a todos sus clientes.
/// </summary>
[Authorize]
[ApiController]
[Route("api/Revision/etiqueta-grupos")]
[RequiereModulo(MobileModuleIds.Review)]
public sealed class EtiquetaGruposController(
    EtiquetaGrupoClienteService service,
    ILogger<EtiquetaGruposController> logger) : ControllerBase
{
    /// <summary>¿Este cliente tiene etiquetas conocidas? Lo pregunta la app.</summary>
    [HttpGet("perfil/{cidcte}")]
    public async Task<IActionResult> Perfil(string cidcte, CancellationToken cancellationToken)
    {
        EtiquetaPerfilClienteResponse perfil = await service.GetPerfilAsync(cidcte, cancellationToken);
        return Ok(new { success = true, perfil });
    }

    /// <summary>Lo mismo, preguntando por la entrada. Es el que usa la app.</summary>
    [HttpGet("perfil-entrada/{crefAgrupada}")]
    public async Task<IActionResult> PerfilDeEntrada(string crefAgrupada, CancellationToken cancellationToken)
    {
        EtiquetaPerfilClienteResponse perfil =
            await service.GetPerfilPorReferenciaAsync(crefAgrupada, cancellationToken);
        return Ok(new { success = true, perfil });
    }

    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken cancellationToken)
    {
        try
        {
            List<EtiquetaGrupoCliente> grupos = await service.ListarAsync(cancellationToken);
            return Ok(new { success = true, grupos });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error al listar los grupos de etiquetas");
            return StatusCode(500, new { success = false, message = "No se pudieron cargar los grupos." });
        }
    }

    /// <summary>Buscador de clientes del formulario.</summary>
    [HttpGet("clientes")]
    public async Task<IActionResult> Clientes([FromQuery] string? q, CancellationToken cancellationToken)
    {
        try
        {
            List<EtiquetaClienteCatalogoItem> clientes = await service.BuscarClientesAsync(q, cancellationToken);
            return Ok(new { success = true, clientes });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error al buscar clientes para el grupo de etiquetas");
            return StatusCode(500, new { success = false, message = "No se pudo buscar en el catálogo de clientes." });
        }
    }

    [HttpPost]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> Guardar(
        [FromBody] EtiquetaGrupoGuardarRequest request,
        CancellationToken cancellationToken)
    {
        string usuario = User.Identity?.Name ?? "sistema";
        (bool ok, string? error, int idGrupo) = await service.GuardarAsync(request, usuario, cancellationToken);
        return ok
            ? Ok(new { success = true, idGrupo })
            : BadRequest(new { success = false, message = error });
    }

    [HttpDelete("{idGrupo:int}")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> Eliminar(int idGrupo, CancellationToken cancellationToken)
    {
        (bool ok, string? error) = await service.EliminarAsync(idGrupo, cancellationToken);
        return ok
            ? Ok(new { success = true })
            : BadRequest(new { success = false, message = error });
    }
}
