using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[RequiereModulo(MobileModuleIds.Providers)]
[ApiController]
[Route("api/proveedores")]
public class ProveedorController : ControllerBase
{
    private readonly ProveedorService _proveedorService;
    private readonly ILogger<ProveedorController> _logger;

    public ProveedorController(ProveedorService proveedorService, ILogger<ProveedorController> logger)
    {
        _proveedorService = proveedorService;
        _logger = logger;
    }

    private string ObtenerUsuario() => User.Identity?.Name ?? "sistema";

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] string? busqueda, [FromQuery] int pagina = 1, [FromQuery] int pageSize = 20)
    {
        try
        {
            var result = await _proveedorService.ListarAsync(busqueda, pagina, pageSize);
            return this.ApiOk(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al listar proveedores");
            return this.ApiError(ex.Message);
        }
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Obtener(int id)
    {
        try
        {
            var proveedor = await _proveedorService.ObtenerPorIdAsync(id);
            if (proveedor == null)
                return this.ApiNotFound("Proveedor no encontrado");

            return this.ApiOk(proveedor);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener proveedor {Id}", id);
            return this.ApiError(ex.Message);
        }
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] ProveedorUpsertRequest request)
    {
        try
        {
            var id = await _proveedorService.CrearAsync(request, ObtenerUsuario());
            return this.ApiOk(new { id }, "Proveedor creado exitosamente");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al crear proveedor");
            return this.ApiError(ex.Message);
        }
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Actualizar(int id, [FromBody] ProveedorUpsertRequest request)
    {
        try
        {
            var result = await _proveedorService.ActualizarAsync(id, request, ObtenerUsuario());
            if (!result)
                return this.ApiNotFound("Proveedor no encontrado");

            return this.ApiOk("Proveedor actualizado exitosamente");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al actualizar proveedor {Id}", id);
            return this.ApiError(ex.Message);
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Eliminar(int id)
    {
        try
        {
            var result = await _proveedorService.EliminarAsync(id);
            if (!result)
                return this.ApiNotFound("Proveedor no encontrado");

            return this.ApiOk("Proveedor eliminado exitosamente");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al eliminar proveedor {Id}", id);
            return this.ApiError(ex.Message);
        }
    }
}
