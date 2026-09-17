using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[RequiereModulo(MobileModuleIds.Inventory)]
[ApiController]
[Route("api/inventario")]
public class InventarioController : ControllerBase
{
    private readonly InventarioService _inventarioService;
    private readonly ILogger<InventarioController> _logger;

    public InventarioController(InventarioService inventarioService, ILogger<InventarioController> logger)
    {
        _inventarioService = inventarioService;
        _logger = logger;
    }

    private string ObtenerUsuario() => User.Identity?.Name ?? "sistema";

    [HttpGet("total-auditoria")]
    public async Task<IActionResult> GetTotalAuditoria()
    {
        try
        {
            var total = await _inventarioService.CountInvAudAsync();
            return Ok(new { success = true, data = total });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener total auditoria");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("reiniciar-auditoria")]
    [RequiereModulo(MobileModuleIds.Inventory, "Borrar")]
    public async Task<IActionResult> ReiniciarAuditoria()
    {
        try
        {
            await _inventarioService.ReiniciarInvAsync();
            return Ok(new InventarioResult { Success = true, Message = "Auditoria reiniciada." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al reiniciar auditoria");
            return Ok(new InventarioResult { Success = false, Message = ex.Message });
        }
    }

    [HttpGet("reporte-auditoria")]
    public async Task<IActionResult> GetReporteAuditoria()
    {
        try
        {
            var bultos = await _inventarioService.ObtenerBultosAuditadosAsync();
            return Ok(new { success = true, data = bultos, total = bultos.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al generar reporte auditoria");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("entradas/{busqueda}")]
    public async Task<IActionResult> BuscarEntradas(string busqueda)
    {
        try
        {
            var entradas = await _inventarioService.BuscarEntradaAsync(busqueda);
            return Ok(new { success = true, data = entradas });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al buscar entradas {Busqueda}", busqueda);
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("{entrada}/confirmar-todo-ok")]
    public async Task<IActionResult> ConfirmarTodoOk(string entrada)
    {
        try
        {
            var result = await _inventarioService.InventarioOKAsync(entrada, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al confirmar todo OK para {Entrada}", entrada);
            return Ok(new InventarioResult { Success = false, Message = ex.Message });
        }
    }

    [HttpPost("scan")]
    public async Task<IActionResult> ScanBulto([FromBody] InventarioScanRequest request)
    {
        try
        {
            if (!request.CodigoBarras.Contains("01C") && !request.CodigoBarras.Contains("01D"))
                return Ok(new InventarioResult { Success = false, Message = "Codigo de barras invalido." });

            var result = await _inventarioService.GuardarScanAsync(
           request.CodigoBarras,
           request.Usuario,
           request.Ubicacion);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al escanear bulto");
            return Ok(new InventarioResult { Success = false, Message = ex.Message });
        }
    }

    [HttpPost("guardar-auditoria")]
    public async Task<IActionResult> GuardarAuditoria([FromBody] InventarioScanRequest request)
    {
        try
        {
            var result = await _inventarioService.GuardarScanAsync(
           request.CodigoBarras,
           request.Usuario,
           request.Ubicacion);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al guardar auditoria");
            return Ok(new InventarioResult { Success = false, Message = ex.Message });
        }
    }
}
