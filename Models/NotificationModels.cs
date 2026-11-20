namespace PMMovil.Api.Models;

public class PushPublicKeyResponse
{
    public bool Success { get; set; }
    public string PublicKey { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class PushSubscriptionRequest
{
    public string Endpoint { get; set; } = string.Empty;
    public double? ExpirationTime { get; set; }
    public PushSubscriptionKeys Keys { get; set; } = new();
    public string UserAgent { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public bool ReceiveBlockAlerts { get; set; }
}

public class PushSubscriptionKeys
{
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
}

public class PushUnsubscribeRequest
{
    public string Endpoint { get; set; } = string.Empty;
}

public class PushSubscriptionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Registro de un token FCM -- mismo mecanismo para Android nativo (app-level
/// FirebaseMessagingService) y navegador (Firebase Messaging JS SDK). El
/// backend manda por FirebaseMessaging.SendAsync a cualquiera de los dos con
/// la misma llamada; solo cambia el "platform" para poder filtrar/depurar.
/// </summary>
public class FcmTokenRequest
{
    public string Token { get; set; } = string.Empty;
    /// <summary>"android" o "web".</summary>
    public string Platform { get; set; } = string.Empty;
}

public class FcmUnregisterRequest
{
    public string Token { get; set; } = string.Empty;
}

public sealed class NotificationDeliveryResult
{
    public bool PushSent { get; init; }
    public bool InboxStored { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class NotificationInboxItem
{
    public long Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? CrefAgrupada { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string> Data { get; set; } = new Dictionary<string, string>();
}

public sealed class NotificationInboxResponse
{
    public bool Success { get; init; }
    public int UnreadCount { get; init; }
    public IReadOnlyList<NotificationInboxItem> Items { get; init; } = Array.Empty<NotificationInboxItem>();
}

public class SalidaBloqueoNotification
{
    public string Type { get; set; } = "salida-bloqueo";
    public string EventoId { get; set; } = string.Empty;
    public string Relno { get; set; } = string.Empty;
    public string Caja { get; set; } = string.Empty;
    public string Usuario { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string Motivo { get; set; } = string.Empty;
    public int EtiquetasBloqueadas { get; set; }
    public DateTime Fecha { get; set; }
    public string Url { get; set; } = string.Empty;
}

public class SalidaDesbloqueoNotification
{
    public string Type { get; set; } = "salida-desbloqueo";
    public string Relno { get; set; } = string.Empty;
    public string Supervisor { get; set; } = string.Empty;
    public string UsuarioCarga { get; set; } = string.Empty;
    public DateTime Fecha { get; set; }
    public string Url { get; set; } = string.Empty;
}

public class PreregistroCreadoNotification
{
    public string Type { get; set; } = "preregistro-creado";
    public string Folio { get; set; } = string.Empty;
    public string Nunidad { get; set; } = string.Empty;
    public string Placas { get; set; } = string.Empty;
    public string Operador { get; set; } = string.Empty;
    public string NombreLinea { get; set; } = string.Empty;
    public string IdBodega { get; set; } = string.Empty;
    public DateTime FechaCrea { get; set; }
    public DateTime FechaExpira { get; set; }
    public int? EtaMinutos { get; set; }
    public double? EtaKm { get; set; }
}
