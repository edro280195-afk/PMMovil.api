using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[RequiereModulo(MobileModuleIds.WarehouseOperations)]
[ApiController]
[Route("api/[controller]")]
public class WarehouseLayoutController : ControllerBase
{
    private readonly WarehouseLayoutService _layoutService;
    private readonly ILogger<WarehouseLayoutController> _logger;

    public WarehouseLayoutController(
        WarehouseLayoutService layoutService,
        ILogger<WarehouseLayoutController> logger)
    {
        _layoutService = layoutService;
        _logger = logger;
    }

    [HttpGet("{warehouseId}")]
    public async Task<IActionResult> GetLayout(string warehouseId)
    {
        try
        {
            var result = await _layoutService.GetLayoutAsync(warehouseId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al cargar layout de bodega {WarehouseId}", warehouseId);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al cargar el mapa de bodega."
            });
        }
    }

    [HttpGet("{warehouseId}/ocupacion")]
    public async Task<IActionResult> GetOccupancy(string warehouseId)
    {
        try
        {
            var result = await _layoutService.GetOccupancyAsync(warehouseId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al cargar ocupacion de bodega {WarehouseId}", warehouseId);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al cargar la ocupacion del mapa de bodega."
            });
        }
    }

    [HttpPut("{warehouseId}")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations, "Modificar")]
    public async Task<IActionResult> SaveLayout(
        string warehouseId,
        [FromBody] WarehouseLayoutSaveRequest request)
    {
        try
        {
            string usuarioActual = User.Identity?.Name ?? "sistema";
            var result = await _layoutService.SaveLayoutAsync(warehouseId, request, usuarioActual);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al guardar layout de bodega {WarehouseId}", warehouseId);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al guardar el mapa de bodega."
            });
        }
    }
}
