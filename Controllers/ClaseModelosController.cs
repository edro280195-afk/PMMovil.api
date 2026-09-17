using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Catalogo Clase -> Modelo de carga del mapa de bodega.
/// GET es de solo lectura para cualquier usuario autenticado (lo consume tambien
/// el formulario de entrada). PUT requiere permiso de modificar el mapa (modulo 153).
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ClaseModelosController : ControllerBase
{
    private readonly ClaseModelService _service;
    private readonly ILogger<ClaseModelosController> _logger;

    public ClaseModelosController(ClaseModelService service, ILogger<ClaseModelosController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> Get()
    {
        try
        {
            var catalogo = await _service.GetCatalogAsync();
            return Ok(new { success = true, catalogo });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al cargar el catalogo clase-modelos");
            return StatusCode(500, new { success = false, message = "Error al cargar el catalogo de modelos." });
        }
    }

    [HttpPut]
    [RequiereModulo(MobileModuleIds.WarehouseOperations, "Modificar")]
    public async Task<IActionResult> Save([FromBody] ClaseModelCatalog request)
    {
        try
        {
            string usuario = User.Identity?.Name ?? "sistema";
            var catalogo = await _service.SaveCatalogAsync(request, usuario);
            return Ok(new { success = true, catalogo });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al guardar el catalogo clase-modelos");
            return StatusCode(500, new { success = false, message = "Error al guardar el catalogo de modelos." });
        }
    }
}
