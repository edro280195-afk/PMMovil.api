using System.Diagnostics;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IModulePermissionService _permissionService;
    private readonly FaceService _faceService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IAuthService authService,
        IModulePermissionService permissionService,
        FaceService faceService,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _permissionService = permissionService;
        _faceService = faceService;
        _logger = logger;
    }

    [EnableRateLimiting("LoginPolicy")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        var username = request?.Username?.Trim() ?? string.Empty;
        var isQr = request?.IsQR ?? false;

        _logger.LogInformation(
            "Login recibido. Username={Username}, UsernameLength={UsernameLength}, IsQR={IsQR}, IP={RemoteIp}, UserAgent={UserAgent}, RequestId={RequestId}",
            username,
            username.Length,
            isQr,
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "N/D",
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier);

        if (!base.ModelState.IsValid)
        {
            _logger.LogWarning(
                "Login rechazado por validación. UsernameLength={UsernameLength}, IsQR={IsQR}, ErrorCount={ErrorCount}, ElapsedMs={ElapsedMs}, RequestId={RequestId}",
                username.Length,
                isQr,
                ModelState.ErrorCount,
                stopwatch.ElapsedMilliseconds,
                HttpContext.TraceIdentifier);
            return BadRequest(base.ModelState);
        }

            LoginResponse response = await _authService.AuthenticateAsync(request!);
        if (!response.Success)
        {
            _logger.LogWarning(
                "Login rechazado por autenticación. Username={Username}, IsQR={IsQR}, Message={Message}, ElapsedMs={ElapsedMs}, RequestId={RequestId}",
                username,
                isQr,
                response.Message,
                stopwatch.ElapsedMilliseconds,
                HttpContext.TraceIdentifier);
            return Unauthorized(response);
        }

        _logger.LogInformation(
            "Login exitoso. Username={Username}, IsQR={IsQR}, ElapsedMs={ElapsedMs}, RequestId={RequestId}",
            username,
            isQr,
            stopwatch.ElapsedMilliseconds,
            HttpContext.TraceIdentifier);
        return Ok(response);
    }

    /// <summary>
    /// Entrar con la cara. Va sin autenticación -- es justamente lo que se
    /// está pidiendo -- y por eso NO recibe ningún nombre de usuario: el
    /// dispositivo manda un vector de 512 bytes y el servidor decide quién es.
    ///
    /// Todos los rechazos contestan lo mismo. Distinguir "no te pareces a
    /// nadie" de "te pareces a dos" le regalaría información a quien esté
    /// probando suerte.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("RostroPolicy")]
    [HttpPost("rostro")]
    public async Task<IActionResult> LoginRostro([FromBody] FaceLoginRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.VectorBase64))
        {
            return BadRequest(new LoginResponse
            {
                Success = false,
                Message = "Falta el vector facial."
            });
        }

        var stopwatch = Stopwatch.StartNew();
        string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "N/D";

        FaceService.FaceLoginOutcome resultado = await _faceService.IdentificarAsync(request, ip);

        if (!resultado.Aceptado)
        {
            _logger.LogWarning(
                "Acceso facial rechazado. Motivo={Motivo}, Intento={Intento}, Planta={Planta}, IP={RemoteIp}, ElapsedMs={ElapsedMs}, RequestId={RequestId}",
                resultado.Motivo,
                request.Intento,
                request.Planta,
                ip,
                stopwatch.ElapsedMilliseconds,
                HttpContext.TraceIdentifier);

            return StatusCode(resultado.HttpStatus, resultado.Login);
        }

        _logger.LogInformation(
            "Acceso facial exitoso. Username={Username}, Intento={Intento}, ElapsedMs={ElapsedMs}, RequestId={RequestId}",
            resultado.Login?.User?.Username,
            request.Intento,
            stopwatch.ElapsedMilliseconds,
            HttpContext.TraceIdentifier);

        return Ok(resultado.Login);
    }

    [Authorize]
    [HttpGet("validate")]
    public IActionResult Validate()
    {
        return Ok(new
        {
            Message = "Token válido",
            Username = base.User.Identity?.Name
        });
    }

    [Authorize]
    [HttpGet("permisos")]
    public async Task<IActionResult> GetPermisos(CancellationToken cancellationToken)
    {
        var username = base.User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username))
        {
            return Unauthorized(new
            {
                message = "Token inválido: no se pudo obtener el usuario."
            });
        }
        return Ok(await _permissionService.GetPermissionsAsync(username, cancellationToken));
    }

    // Payload para asociar un gafete NFC: el mismo par "usuario;pass_qr" que
    // ya viaja en el QR impreso, nunca la contraseña real de acceso. Ver
    // docs/nfc-gafete.md.
    [Authorize]
    [HttpGet("mi-gafete")]
    public async Task<IActionResult> GetMiGafete()
    {
        var username = base.User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username))
        {
            return Unauthorized(new
            {
                success = false,
                message = "Token inválido: no se pudo obtener el usuario."
            });
        }

        string? payload = await _authService.GetGafetePayloadAsync(username);
        if (payload == null)
        {
            return NotFound(new
            {
                success = false,
                message = "Tu cuenta no tiene un gafete QR asignado todavía. Solicítalo a Sistemas antes de asociar un NFC."
            });
        }

        return Ok(new
        {
            success = true,
            username,
            payload
        });
    }
}
