using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Portal publico de pre-registro para lineas transportistas. Todo lo
/// autenticado aqui usa el esquema JWT "Portal" (audiencia y secreto propios,
/// ver Program.cs) -- un token emitido aqui NUNCA es valido en ningun
/// endpoint interno protegido con el esquema por defecto ([Authorize] solo o
/// con [RequiereModulo]).
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class PreregistroController : ControllerBase
{
    private readonly PreregistroService _service;
    private readonly ILogger<PreregistroController> _logger;

    public PreregistroController(PreregistroService service, ILogger<PreregistroController> logger)
    {
        _service = service;
        _logger = logger;
    }

    private string ObtenerIp() =>
        HttpContext.Connection.RemoteIpAddress?.ToString() ?? "desconocida";

    /// <summary>ID de la cuenta autenticada (claim id_cuenta del JWT "Portal").</summary>
    private int? ObtenerIdCuenta()
    {
        var claim = User.FindFirst("id_cuenta")?.Value;
        return int.TryParse(claim, out int id) ? id : null;
    }

    private string ObtenerCidTranspor() => User.FindFirst("cidtranspor")?.Value ?? string.Empty;
    private string ObtenerNombreLinea() => User.FindFirst("nombre_linea")?.Value ?? string.Empty;

    /// <summary>Catálogo activo de bodegas para el formulario público.</summary>
    [AllowAnonymous]
    [HttpGet("bodegas")]
    public async Task<IActionResult> Bodegas(CancellationToken ct) => Ok(await _service.ListarBodegasAsync(true, ct));

    [AllowAnonymous]
    [EnableRateLimiting("PreregistroLoginPolicy")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] PreregistroLoginRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var response = await _service.LoginAsync(request.Usuario, request.Password, ObtenerIp());
        if (!response.Success)
            return Unauthorized(response);

        return Ok(response);
    }

    [Authorize(AuthenticationSchemes = "Portal")]
    [EnableRateLimiting("PreregistroCrearPolicy")]
    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] PreregistroCrearRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        int? idCuenta = ObtenerIdCuenta();
        if (idCuenta is null)
            return Unauthorized(new { message = "Token invalido." });

        var response = await _service.CrearAsync(idCuenta.Value, ObtenerCidTranspor(), ObtenerNombreLinea(), request, ObtenerIp());
        if (!response.Success)
            return BadRequest(response);

        return Ok(response);
    }

    [Authorize(AuthenticationSchemes = "Portal")]
    [HttpGet("mis-registros")]
    public async Task<IActionResult> MisRegistros()
    {
        int? idCuenta = ObtenerIdCuenta();
        if (idCuenta is null)
            return Unauthorized(new { message = "Token invalido." });

        return Ok(await _service.MisRegistrosAsync(idCuenta.Value));
    }

    [Authorize(AuthenticationSchemes = "Portal")]
    [HttpPost("{folio}/cancelar")]
    public async Task<IActionResult> Cancelar(string folio)
    {
        int? idCuenta = ObtenerIdCuenta();
        if (idCuenta is null)
            return Unauthorized(new { message = "Token invalido." });

        bool ok = await _service.CancelarAsync(idCuenta.Value, folio);
        if (!ok)
            return NotFound(new { message = "No se encontro un pre-registro vigente con ese folio." });

        return Ok(new { success = true, message = "Pre-registro cancelado." });
    }

    /// <summary>
    /// Latido de ubicacion del chofer. Cada llamada agrega una miga al recorrido
    /// y refresca el ETA. El portal lo llama cada pocos minutos o cada pocos
    /// cientos de metros mientras la pantalla esta abierta -- antes solo se
    /// mandaba una posicion al crear el folio y nunca mas, asi que el ETA que
    /// veia la garita podia tener horas.
    /// </summary>
    [Authorize(AuthenticationSchemes = "Portal")]
    [HttpPost("{folio}/ubicacion")]
    public async Task<IActionResult> ActualizarUbicacion(string folio, [FromBody] PreregistroUbicacionRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        int? idCuenta = ObtenerIdCuenta();
        if (idCuenta is null)
            return Unauthorized(new { message = "Token invalido." });

        var response = await _service.ActualizarUbicacionAsync(idCuenta.Value, folio, request);
        if (!response.Success)
            return NotFound(response);

        return Ok(response);
    }

    /// <summary>
    /// Recorrido del folio (migas de pan). La cuenta solo puede consultar folios
    /// suyos: se valida contra mis-registros antes de devolver nada.
    /// </summary>
    [Authorize(AuthenticationSchemes = "Portal")]
    [HttpGet("{folio}/recorrido")]
    public async Task<IActionResult> Recorrido(string folio, [FromQuery] int limite = 500)
    {
        int? idCuenta = ObtenerIdCuenta();
        if (idCuenta is null)
            return Unauthorized(new { message = "Token invalido." });

        var propios = await _service.MisRegistrosAsync(idCuenta.Value);
        if (!propios.Any(p => string.Equals(p.Folio, folio, StringComparison.OrdinalIgnoreCase)))
            return NotFound(new { message = "Pre-registro no encontrado." });

        return Ok(await _service.ObtenerRecorridoAsync(folio, limite));
    }
}
