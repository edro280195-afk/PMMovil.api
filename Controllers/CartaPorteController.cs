using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[RequiereModulo(MobileModuleIds.CartaPorte)]
[ApiController]
[Route("api/[controller]")]
public class CartaPorteController : ControllerBase
{
    private readonly CartaPorteService _cartaPorteService;
    private readonly ILogger<CartaPorteController> _logger;

    public CartaPorteController(CartaPorteService cartaPorteService, ILogger<CartaPorteController> logger)
    {
        _cartaPorteService = cartaPorteService;
        _logger = logger;
    }

    private string ObtenerUsuario() => User.Identity?.Name ?? "sistema";

    [HttpGet]
    public async Task<IActionResult> GetCartaPorte([FromQuery] string trafico, [FromQuery] string origen = "TRAFICO")
    {
        try
        {
            var result = await _cartaPorteService.GetCartaPorteAsync(trafico, origen, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener carta porte {Trafico}", trafico);
            return Ok(new CartaPorteDetalle { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("{trafico}/transferir")]
    public async Task<IActionResult> Transferir(string trafico, [FromQuery] string origen = "TRAFICO")
    {
        try
        {
            var result = await _cartaPorteService.TransferirAsync(trafico, origen, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al transferir carta porte {Trafico}", trafico);
            return Ok(new TransferirResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpGet("{trafico}/excel")]
    public IActionResult DescargarExcel(string trafico)
    {
        try
        {
            string rutaBase = _cartaPorteService.GetExcelPath(trafico);
            if (!System.IO.File.Exists(rutaBase))
                return NotFound(new { message = "Archivo no encontrado" });

            var bytes = System.IO.File.ReadAllBytes(rutaBase);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
           $"CartaPorte_{trafico}.xlsx");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al descargar excel carta porte {Trafico}", trafico);
            return NotFound(new { message = ex.Message });
        }
    }
}
