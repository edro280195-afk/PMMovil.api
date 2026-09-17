using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class NotificationsController : ControllerBase
{
    private readonly IRealtimeNotificationService _notifications;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        IRealtimeNotificationService notifications,
        ILogger<NotificationsController> logger)
    {
        _notifications = notifications;
        _logger = logger;
    }

    private string ObtenerUsuario() => User.Identity?.Name ?? string.Empty;

    [HttpGet("vapid-public-key")]
    public IActionResult GetVapidPublicKey()
    {
        var publicKey = _notifications.GetVapidPublicKey();
        return Ok(new PushPublicKeyResponse
        {
            Success = !string.IsNullOrWhiteSpace(publicKey),
            PublicKey = publicKey,
            Message = string.IsNullOrWhiteSpace(publicKey)
                ? "VAPID no esta configurado en el API."
                : "OK"
        });
    }

    [HttpPost("push/subscribe")]
    public async Task<IActionResult> Subscribe([FromBody] PushSubscriptionRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.UserAgent))
            {
                request.UserAgent = Request.Headers.UserAgent.ToString();
            }

            var result = await _notifications.SaveSubscriptionAsync(ObtenerUsuario(), request);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al registrar suscripcion push");
            return Ok(new PushSubscriptionResult
            {
                Success = false,
                Message = "No se pudo registrar el dispositivo."
            });
        }
    }

    [HttpPost("push/unsubscribe")]
    public async Task<IActionResult> Unsubscribe([FromBody] PushUnsubscribeRequest request)
    {
        try
        {
            var result = await _notifications.RemoveSubscriptionAsync(ObtenerUsuario(), request.Endpoint);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al retirar suscripcion push");
            return Ok(new PushSubscriptionResult
            {
                Success = false,
                Message = "No se pudo retirar el dispositivo."
            });
        }
    }

    [HttpGet("fcm-configured")]
    public IActionResult GetFcmConfigured()
    {
        return Ok(new { success = true, configured = _notifications.IsFcmConfigured });
    }

    [HttpPost("fcm/register")]
    public async Task<IActionResult> RegisterFcmToken([FromBody] FcmTokenRequest request)
    {
        try
        {
            var result = await _notifications.SaveFcmTokenAsync(ObtenerUsuario(), request);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al registrar token FCM");
            return Ok(new PushSubscriptionResult
            {
                Success = false,
                Message = "No se pudo registrar el dispositivo."
            });
        }
    }

    [HttpPost("fcm/unregister")]
    public async Task<IActionResult> UnregisterFcmToken([FromBody] FcmUnregisterRequest request)
    {
        try
        {
            var result = await _notifications.RemoveFcmTokenAsync(request.Token);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al retirar token FCM");
            return Ok(new PushSubscriptionResult
            {
                Success = false,
                Message = "No se pudo retirar el dispositivo."
            });
        }
    }

    [HttpGet("inbox")]
    public async Task<IActionResult> GetInbox(
        [FromQuery] bool soloNoLeidas = false,
        [FromQuery] int limite = 50)
    {
        try
        {
            var result = await _notifications.GetInboxAsync(
                ObtenerUsuario(),
                soloNoLeidas,
                limite);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar bandeja de notificaciones");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo consultar la bandeja de notificaciones."
            });
        }
    }

    [HttpPost("inbox/{id:long}/read")]
    public async Task<IActionResult> MarkInboxRead(long id)
    {
        try
        {
            bool updated = await _notifications.MarkInboxReadAsync(ObtenerUsuario(), id);
            return Ok(new { success = updated });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al marcar notificación {Id} como leída", id);
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo marcar la notificación."
            });
        }
    }

    [HttpPost("inbox/read-all")]
    public async Task<IActionResult> MarkAllInboxRead()
    {
        try
        {
            int updated = await _notifications.MarkAllInboxReadAsync(ObtenerUsuario());
            return Ok(new { success = true, actualizadas = updated });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al marcar la bandeja como leída");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo marcar la bandeja."
            });
        }
    }
}
