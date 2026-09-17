using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Alta y baja del rostro de la persona que ya inició sesión.
///
/// TODO aquí exige token, y no es un detalle: el enrolamiento sólo puede
/// salir de una sesión ya autenticada. Es la misma regla de iOS -- para dar
/// de alta una cara hay que saber el código -- y es lo que impide que alguien
/// registre su rostro sobre la cuenta de otra persona.
///
/// El login por rostro NO vive aquí: vive en AuthController porque es
/// anónimo por definición.
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class FaceController : ControllerBase
{
    private readonly FaceService _faceService;
    private readonly ILogger<FaceController> _logger;

    public FaceController(FaceService faceService, ILogger<FaceController> logger)
    {
        _faceService = faceService;
        _logger = logger;
    }

    private string? UsuarioActual => User.Identity?.Name;

    /// <summary>
    /// ¿Ya tengo el rostro dado de alta? También dice si el acceso facial
    /// está encendido en el servidor, para que la app no ofrezca una función
    /// que hoy contestaría 503.
    /// </summary>
    [HttpGet("estado")]
    public async Task<IActionResult> GetEstado()
    {
        string? usuario = UsuarioActual;
        if (string.IsNullOrWhiteSpace(usuario))
        {
            return Unauthorized(new FaceEstadoResponse
            {
                Success = false,
                Message = "Token inválido: no se pudo obtener el usuario."
            });
        }

        return Ok(await _faceService.GetEstadoAsync(usuario));
    }

    /// <summary>
    /// Guarda el alta completa en un solo viaje.
    ///
    /// El dispositivo hace las dos vueltas del anillo por su cuenta: tiene los
    /// modelos y está comparando sus propias capturas, así que no necesita al
    /// servidor para eso y no hay nada que ganar partiéndolo en llamadas.
    ///
    /// Cada alta es una APARIENCIA con nombre ("Con lentes", "Sin barba") y
    /// por defecto se SUMA a las que ya había: el login compara contra todas y
    /// se queda con la que mejor coincida. Para borrar lo anterior hay que
    /// pedirlo con `Reemplazar`.
    /// </summary>
    [HttpPost("enrolamiento")]
    public async Task<IActionResult> Enrolar([FromBody] FaceEnrolamientoRequest request)
    {
        string? usuario = UsuarioActual;
        if (string.IsNullOrWhiteSpace(usuario))
        {
            return Unauthorized(new FaceResult
            {
                Success = false,
                Message = "Token inválido: no se pudo obtener el usuario."
            });
        }

        if (request == null)
        {
            return BadRequest(new FaceResult { Success = false, Message = "Petición vacía." });
        }

        FaceResult resultado = await _faceService.GuardarEnrolamientoAsync(usuario, request);
        if (!resultado.Success)
        {
            _logger.LogWarning(
                "Enrolamiento facial rechazado. Usuario={Usuario}, Message={Message}",
                usuario, resultado.Message);
            return BadRequest(resultado);
        }

        return Ok(resultado);
    }

    /// <summary>
    /// Borrar UNA apariencia (la de los lentes, por ejemplo) y conservar las
    /// demás. El servicio exige que el id sea de quien pide, así que mandar el
    /// de otra persona no borra nada.
    /// </summary>
    [HttpDelete("apariencia/{id:long}")]
    public async Task<IActionResult> RevocarApariencia(long id)
    {
        string? usuario = UsuarioActual;
        if (string.IsNullOrWhiteSpace(usuario))
        {
            return Unauthorized(new FaceResult
            {
                Success = false,
                Message = "Token inválido: no se pudo obtener el usuario."
            });
        }

        FaceResult resultado = await _faceService.RevocarAparienciaAsync(usuario, id);
        if (!resultado.Success)
        {
            return NotFound(resultado);
        }

        return Ok(resultado);
    }

    /// <summary>
    /// Borrar mi rostro COMPLETO: todas las apariencias de una vez. No hay
    /// confirmación en el servidor a propósito: la pantalla ya la pidió, y
    /// aquí lo importante es que quitarlo sea tan fácil como ponerlo.
    /// </summary>
    [HttpDelete("enrolamiento")]
    public async Task<IActionResult> Revocar()
    {
        string? usuario = UsuarioActual;
        if (string.IsNullOrWhiteSpace(usuario))
        {
            return Unauthorized(new FaceResult
            {
                Success = false,
                Message = "Token inválido: no se pudo obtener el usuario."
            });
        }

        return Ok(await _faceService.RevocarAsync(usuario));
    }
}
