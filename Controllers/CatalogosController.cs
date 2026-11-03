using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Catálogos maestros para guardar en la tableta (ver <see cref="CatalogosService"/>). Cualquier usuario con sesión puede
/// leerlos: son los mismos datos que ya veía al buscar un cliente o una línea en su pantalla.
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class CatalogosController : ControllerBase
{
    private readonly CatalogosService _catalogos;
    private readonly ILogger<CatalogosController> _logger;

    public CatalogosController(CatalogosService catalogos, ILogger<CatalogosController> logger)
    {
        _catalogos = catalogos;
        _logger = logger;
    }

    /// <summary>Nombre -> versión de cada catálogo. Muy ligero: es lo que la tableta pregunta al abrir una pantalla.</summary>
    [HttpGet("versiones")]
    public async Task<IActionResult> Versiones()
    {
        try
        {
            return Ok(new { success = true, versiones = await _catalogos.ObtenerVersionesAsync() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener las versiones de los catalogos");
            return Ok(new { success = false, message = "No se pudieron consultar las versiones de los catálogos." });
        }
    }

    /// <summary>Los catálogos pedidos (separados por coma), completos y con su versión.</summary>
    [HttpGet]
    public async Task<IActionResult> Obtener([FromQuery] string? nombres)
    {
        try
        {
            string[] pedidos = (nombres ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pedidos.Length == 0)
            {
                return Ok(new { success = false, message = "Indica qué catálogos necesitas (nombres=clientes,lineas)." });
            }

            var catalogos = await _catalogos.ObtenerAsync(pedidos);
            return Ok(new
            {
                success = true,
                catalogos = catalogos.Select(c => new
                {
                    nombre = c.Nombre,
                    version = c.Version,
                    items = c.Items.Select(i => new { id = i.Id, nombre = i.Nombre, extra = i.Extra })
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener catalogos {Nombres}", nombres);
            return Ok(new { success = false, message = "No se pudieron consultar los catálogos." });
        }
    }
}
