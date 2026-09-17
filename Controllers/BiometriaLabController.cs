using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Laboratorio biometrico facial -- SOLO PRUEBAS.
///
/// Existe para contestar con datos si el reconocimiento facial aguanta las
/// condiciones reales de bodega (oscuridad, distancia, angulo) antes de
/// comprometer un diseno definitivo. NO es un modulo de produccion.
///
/// Se apaga solo fuera de Development: cualquier llamada responde 404 a menos
/// que se encienda a proposito con BiometriaLab:Habilitado = true. Asi, si este
/// codigo llegara a publicarse por accidente, no queda un endpoint abierto que
/// acepte plantillas biometricas.
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class BiometriaLabController : ControllerBase
{
    private readonly BiometriaLabService _service;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BiometriaLabController> _logger;

    public BiometriaLabController(
        BiometriaLabService service,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<BiometriaLabController> logger)
    {
        _service = service;
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
    }

    private bool LaboratorioHabilitado =>
        _environment.IsDevelopment()
        || _configuration.GetValue("BiometriaLab:Habilitado", defaultValue: false);

    /// <summary>404 si el laboratorio esta apagado, null si puede continuar.</summary>
    private IActionResult? GuardarEntorno()
    {
        if (LaboratorioHabilitado)
        {
            return null;
        }

        _logger.LogWarning(
            "Intento de acceso al laboratorio biometrico con el entorno {Entorno}. Usuario={Usuario}",
            _environment.EnvironmentName,
            User.Identity?.Name ?? "N/D");
        return NotFound();
    }

    [HttpGet("sujetos")]
    public async Task<IActionResult> GetSujetos()
    {
        if (GuardarEntorno() is IActionResult bloqueo) return bloqueo;

        try
        {
            return Ok(new { success = true, sujetos = await _service.GetSujetosAsync() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al listar sujetos del laboratorio biometrico");
            return StatusCode(500, new { success = false, message = MensajeDeError(ex) });
        }
    }

    [HttpPost("sujetos")]
    public async Task<IActionResult> UpsertSujeto([FromBody] BioLabSujetoRequest request)
    {
        if (GuardarEntorno() is IActionResult bloqueo) return bloqueo;

        try
        {
            return Ok(await _service.UpsertSujetoAsync(request));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al dar de alta un sujeto de prueba");
            return StatusCode(500, new BioLabResult { Success = false, Message = MensajeDeError(ex) });
        }
    }

    [HttpPost("plantillas")]
    public async Task<IActionResult> AddPlantilla([FromBody] BioLabPlantillaRequest request)
    {
        if (GuardarEntorno() is IActionResult bloqueo) return bloqueo;

        try
        {
            return Ok(await _service.AddPlantillaAsync(request));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al guardar plantilla de prueba");
            return StatusCode(500, new BioLabResult { Success = false, Message = MensajeDeError(ex) });
        }
    }

    /// <summary>
    /// Banco completo de plantillas. La app lo descarga y compara en el propio
    /// dispositivo -- el servidor nunca hace matching.
    /// </summary>
    [HttpGet("banco")]
    public async Task<IActionResult> GetBanco()
    {
        if (GuardarEntorno() is IActionResult bloqueo) return bloqueo;

        try
        {
            return Ok(await _service.GetBancoAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al descargar el banco de plantillas");
            return StatusCode(500, new BioLabBanco { Success = false, Message = MensajeDeError(ex) });
        }
    }

    [HttpPost("pruebas")]
    public async Task<IActionResult> AddPrueba([FromBody] BioLabPruebaRequest request)
    {
        if (GuardarEntorno() is IActionResult bloqueo) return bloqueo;

        try
        {
            return Ok(await _service.AddPruebaAsync(request));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al registrar prueba de reconocimiento");
            return StatusCode(500, new BioLabResult { Success = false, Message = MensajeDeError(ex) });
        }
    }

    [HttpGet("resumen")]
    public async Task<IActionResult> GetResumen()
    {
        if (GuardarEntorno() is IActionResult bloqueo) return bloqueo;

        try
        {
            return Ok(await _service.GetResumenAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al calcular el resumen del laboratorio");
            return StatusCode(500, new BioLabResumen { Success = false });
        }
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset([FromQuery] bool incluirSujetos = false)
    {
        if (GuardarEntorno() is IActionResult bloqueo) return bloqueo;

        try
        {
            return Ok(await _service.ResetAsync(incluirSujetos));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al reiniciar el laboratorio biometrico");
            return StatusCode(500, new BioLabResult { Success = false, Message = MensajeDeError(ex) });
        }
    }

    /// <summary>
    /// En Development se devuelve el detalle real: casi siempre es "la tabla no
    /// existe" porque todavia no se corrio Scripts/bio_lab_schema.sql, y decirlo
    /// ahorra media hora de adivinanzas desde el telefono.
    /// </summary>
    private string MensajeDeError(Exception ex) =>
        _environment.IsDevelopment()
            ? ex.Message
            : "Error en el laboratorio biometrico.";
}
