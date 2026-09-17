using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public interface IRealtimeNotificationService
{
    string GetVapidPublicKey();
    bool IsWebPushConfigured { get; }
    Task<PushSubscriptionResult> SaveSubscriptionAsync(string usuario, PushSubscriptionRequest request);
    Task<PushSubscriptionResult> RemoveSubscriptionAsync(string usuario, string endpoint);
    Task SendSalidaBloqueoAsync(SalidaBloqueoNotification notification, string usuarioOperador);

    // FCM -- un solo canal para Android nativo y navegador (Web Push vía Firebase).
    bool IsFcmConfigured { get; }
    Task<PushSubscriptionResult> SaveFcmTokenAsync(string usuario, FcmTokenRequest request);
    Task<PushSubscriptionResult> RemoveFcmTokenAsync(string token);

    /// <summary>
    /// Manda una notificación a TODOS los dispositivos/navegadores activos de
    /// un usuario (no a todos los usuarios). "data" son pares clave/valor que
    /// la app puede leer para navegar directo a la pantalla correspondiente.
    /// </summary>
    Task SendToUserAsync(string usuario, string title, string body, IDictionary<string, string>? data = null);

    /// <summary>
    /// Entrega una notificación de asignación por push si el usuario tiene una
    /// sesión activa en algún dispositivo; de lo contrario la conserva en la
    /// bandeja persistente para mostrarla en el siguiente inicio de sesión.
    /// </summary>
    Task<NotificationDeliveryResult> SendToUserOrInboxAsync(
        string usuario,
        string title,
        string body,
        IDictionary<string, string>? data = null);

    Task<NotificationInboxResponse> GetInboxAsync(
        string usuario,
        bool onlyUnread,
        int limit);

    Task<bool> MarkInboxReadAsync(string usuario, long id);
    Task<int> MarkAllInboxReadAsync(string usuario);
}
