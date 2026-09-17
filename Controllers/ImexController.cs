using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[RequiereModulo(MobileModuleIds.Imex)]
[ApiController]
[Route("api/imex")]
public class ImexController : ControllerBase
{
    private readonly ImexService _imexService;
    private readonly ILogger<ImexController> _logger;

    public ImexController(ImexService imexService, ILogger<ImexController> logger)
    {
        _imexService = imexService;
        _logger = logger;
    }

    private string ObtenerUsuario() => User.Identity?.Name ?? "sistema";

    [HttpGet("cajas")]
    public async Task<IActionResult> GetCajas()
    {
        try
        {
            var cajas = await _imexService.ObtenerCajasDeCasetaAsync();
            return Ok(new { success = true, data = cajas });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener cajas");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("entradas/{busqueda}")]
    public async Task<IActionResult> BuscarEntradas(string busqueda)
    {
        try
        {
            var entradas = await _imexService.ObtenerEntradasAsync(busqueda);
            if (entradas == null || entradas.Count == 0)
                return Ok(new { success = false, message = "No se encontro informacion o ya esta asignada." });

            return Ok(new { success = true, data = entradas });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al buscar entrada {Busqueda}", busqueda);
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("consecutivo")]
    public async Task<IActionResult> GetConsecutivo()
    {
        try
        {
            var consecutivo = await _imexService.ObtenerConsecutivoRelacionAsync();
            return Ok(new { success = true, data = consecutivo });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener consecutivo");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("relaciones")]
    [RequiereModulo(MobileModuleIds.Imex, "Agregar")]
    public async Task<IActionResult> GuardarRelacion([FromBody] GuardarImexRelacionRequest request)
    {
        try
        {
            var result = await _imexService.GuardarInformacionAsync(request, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al guardar relacion IMEX");
            return Ok(new ImexResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpGet("relaciones/{id}")]
    public async Task<IActionResult> GetRelacion(string id)
    {
        try
        {
            var relacion = await _imexService.ObtenerRelacionAsync(id);
            if (relacion == null)
                return Ok(new { success = false, message = "Relacion no encontrada." });

            return Ok(new { success = true, data = relacion });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener relacion {Id}", id);
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("lineas-transportistas")]
    public async Task<IActionResult> GetLineas()
    {
        try
        {
            var lineas = await _imexService.ObtenerLineasAsync();
            return Ok(new { success = true, data = lineas });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener lineas");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpDelete("entradas/{id}")]
    [RequiereModulo(MobileModuleIds.Imex, "Borrar")]
    public async Task<IActionResult> EliminarEntrada(string id)
    {
        try
        {
            var result = await _imexService.EliminarEntradaAsync(id);
            return Ok(new { success = true, message = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al eliminar entrada {Id}", id);
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpDelete("relaciones/{id}")]
    [RequiereModulo(MobileModuleIds.Imex, "Borrar")]
    public async Task<IActionResult> EliminarRelacion(string id, [FromBody] List<ImexEntrada>? entradas)
    {
        try
        {
            var result = await _imexService.EliminarRelacionAsync(id, entradas ?? []);
            return Ok(new { success = true, message = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al eliminar relacion {Id}", id);
            return Ok(new { success = false, message = ex.Message });
        }
    }
}
