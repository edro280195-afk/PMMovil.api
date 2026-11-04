using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Dapper;

using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

using WebPush;

namespace PMMovil.Api.Services;

public class RealtimeNotificationService : IRealtimeNotificationService
{
    private readonly BaseRepository _db;
    private readonly IConfiguration _config;
    private readonly ILogger<RealtimeNotificationService> _logger;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly SemaphoreSlim _inboxSchemaLock = new(1, 1);
    private bool _schemaReady;
    private bool _inboxSchemaReady;

    private static readonly object FirebaseInitLock = new();
    private static FirebaseApp? _firebaseApp;

    public RealtimeNotificationService(
        BaseRepository db,
        IConfiguration config,
        ILogger<RealtimeNotificationService> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    public bool IsFcmConfigured => TryGetFirebaseApp() != null;

    /// <summary>
    /// FirebaseApp debe crearse una sola vez por proceso -- FirebaseApp.Create
    /// truena si ya existe una instancia con el mismo nombre. Perezoso porque
    /// el archivo de credenciales es opcional (entornos sin Firebase configurado
    /// deben seguir funcionando, solo sin este canal).
    /// </summary>
    private FirebaseApp? TryGetFirebaseApp()
    {
        if (_firebaseApp != null) return _firebaseApp;

        lock (FirebaseInitLock)
        {
            if (_firebaseApp != null) return _firebaseApp;
            if (FirebaseApp.DefaultInstance != null)
            {
                _firebaseApp = FirebaseApp.DefaultInstance;
                return _firebaseApp;
            }

            string relativePath = _config["Firebase:ServiceAccountPath"]?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(relativePath)) return null;

            string fullPath = Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.Combine(AppContext.BaseDirectory, relativePath);

            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("Firebase: no se encontró el archivo de credenciales en {Path}.", fullPath);
                return null;
            }

            try
            {
                _firebaseApp = FirebaseApp.Create(new AppOptions
                {
                    Credential = GoogleCredential.FromFile(fullPath)
                });
                _logger.LogInformation("Firebase Admin SDK inicializado desde {Path}.", fullPath);
                return _firebaseApp;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo inicializar Firebase Admin SDK.");
                return null;
            }
        }
    }

    public bool IsWebPushConfigured =>
        !string.IsNullOrWhiteSpace(GetVapidPublicKey()) &&
        !string.IsNullOrWhiteSpace(GetVapidPrivateKey());

    public string GetVapidPublicKey()
    {
        return _config["PushNotifications:Vapid:PublicKey"]?.Trim() ?? string.Empty;
    }

    public async Task<PushSubscriptionResult> SaveSubscriptionAsync(string usuario, PushSubscriptionRequest request)
    {
        if (string.IsNullOrWhiteSpace(usuario))
        {
            return new PushSubscriptionResult { Success = false, Message = "Usuario no valido." };
        }

        if (string.IsNullOrWhiteSpace(request.Endpoint) ||
            string.IsNullOrWhiteSpace(request.Keys.P256dh) ||
            string.IsNullOrWhiteSpace(request.Keys.Auth))
        {
            return new PushSubscriptionResult { Success = false, Message = "Suscripcion push incompleta." };
        }

        await EnsureSchemaAsync();

        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        await conn.ExecuteAsync(@"
INSERT INTO push_subscriptions
    (usuario, endpoint_hash, endpoint, p256dh, auth, user_agent, platform, receive_block_alerts, active)
VALUES
    (@usuario, @endpointHash, @endpoint, @p256dh, @auth, @userAgent, @platform, @receiveBlockAlerts, 1)
ON DUPLICATE KEY UPDATE
    usuario = @usuario,
    endpoint = @endpoint,
    p256dh = @p256dh,
    auth = @auth,
    user_agent = @userAgent,
    platform = @platform,
    receive_block_alerts = @receiveBlockAlerts,
    active = 1,
    updated_at = NOW(),
    last_error = NULL",
            new
            {
                usuario,
                endpointHash = ComputeSha256Hex(request.Endpoint),
                endpoint = request.Endpoint,
                p256dh = request.Keys.P256dh,
                auth = request.Keys.Auth,
                userAgent = TrimTo(request.UserAgent, 255),
                platform = TrimTo(request.Platform, 60),
                receiveBlockAlerts = request.ReceiveBlockAlerts ? 1 : 0
            });

        return new PushSubscriptionResult { Success = true, Message = "Suscripcion registrada." };
    }

    public async Task<PushSubscriptionResult> RemoveSubscriptionAsync(string usuario, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new PushSubscriptionResult { Success = false, Message = "Endpoint no valido." };
        }

        await EnsureSchemaAsync();

        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        await conn.ExecuteAsync(@"
UPDATE push_subscriptions
SET active = 0, updated_at = NOW()
WHERE endpoint_hash = @endpointHash
  AND (@usuario = '' OR usuario = @usuario)",
            new { usuario = usuario ?? string.Empty, endpointHash = ComputeSha256Hex(endpoint) });

        return new PushSubscriptionResult { Success = true, Message = "Suscripcion desactivada." };
    }

    public async Task<PushSubscriptionResult> SaveFcmTokenAsync(string usuario, FcmTokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(usuario))
        {
            return new PushSubscriptionResult { Success = false, Message = "Usuario no valido." };
        }
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return new PushSubscriptionResult { Success = false, Message = "Token FCM vacío." };
        }

        await EnsureSchemaAsync();

        string platform = request.Platform?.Trim().ToLowerInvariant() switch
        {
            "web" => "web",
            _ => "android"
        };

        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        await conn.ExecuteAsync(@"
INSERT INTO fcm_tokens (usuario, token, platform, active, session_active)
VALUES (@usuario, @token, @platform, 1, 1)
ON DUPLICATE KEY UPDATE
    usuario = @usuario,
    platform = @platform,
    active = 1,
    session_active = 1,
    updated_at = NOW(),
    last_error = NULL",
            new { usuario, token = request.Token, platform });

        return new PushSubscriptionResult { Success = true, Message = "Token FCM registrado." };
    }

    public async Task<PushSubscriptionResult> RemoveFcmTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new PushSubscriptionResult { Success = false, Message = "Token no válido." };
        }

        await EnsureSchemaAsync();

        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        await conn.ExecuteAsync(
            "UPDATE fcm_tokens SET active = 0, session_active = 0, updated_at = NOW() WHERE token = @token",
            new { token });

        return new PushSubscriptionResult { Success = true, Message = "Token FCM desactivado." };
    }

    public async Task SendToUserAsync(string usuario, string title, string body, IDictionary<string, string>? data = null)
    {
        await SendFcmToUserAsync(usuario, title, body, data);
    }

    public async Task<NotificationDeliveryResult> SendToUserOrInboxAsync(
        string usuario,
        string title,
        string body,
        IDictionary<string, string>? data = null)
    {
        try
        {
            bool pushSent = await SendFcmToUserAsync(usuario, title, body, data);
            if (pushSent)
            {
                return new NotificationDeliveryResult
                {
                    PushSent = true,
                    Message = "Notificación enviada por push."
                };
            }

            await SaveInboxNotificationAsync(usuario, title, body, data);
            return new NotificationDeliveryResult
            {
                InboxStored = true,
                Message = "Notificación guardada en la bandeja."
            };
        }
        catch (Exception ex)
        {
            // La asignación ya quedó guardada; un fallo del canal de avisos no
            // debe convertir una asignación válida en un error HTTP.
            _logger.LogError(ex, "No se pudo entregar ni guardar notificación para {Usuario}.", usuario);
            return new NotificationDeliveryResult
            {
                Message = "No se pudo entregar la notificación."
            };
        }
    }

    private async Task<bool> SendFcmToUserAsync(
        string usuario,
        string title,
        string body,
        IDictionary<string, string>? data = null)
    {
        if (string.IsNullOrWhiteSpace(usuario)) return false;

        FirebaseApp? app = TryGetFirebaseApp();
        if (app == null)
        {
            _logger.LogInformation("FCM no configurado. Se omitió notificación '{Title}' para {Usuario}.", title, usuario);
            return false;
        }

        await EnsureSchemaAsync();

        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        var tokens = (await conn.QueryAsync<FcmTokenRow>(
            "SELECT id, token FROM fcm_tokens WHERE usuario = @usuario AND active = 1 AND session_active = 1",
            new { usuario })).ToList();

        if (tokens.Count == 0)
        {
            _logger.LogInformation("Sin sesiones FCM activas para {Usuario} ('{Title}').", usuario, title);
            return false;
        }

        var messaging = FirebaseMessaging.GetMessaging(app);
        bool sent = false;

        foreach (var row in tokens)
        {
            var message = new Message
            {
                Token = row.Token,
                Notification = new FirebaseAdmin.Messaging.Notification { Title = title, Body = body },
                Data = data != null ? new Dictionary<string, string>(data) : null,
                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    Notification = new AndroidNotification { Sound = "default" }
                },
                Webpush = new WebpushConfig
                {
                    Notification = new WebpushNotification { Title = title, Body = body }
                }
            };

            try
            {
                await messaging.SendAsync(message);
                sent = true;
                await conn.ExecuteAsync(
                    "UPDATE fcm_tokens SET last_success_at = NOW(), last_error = NULL, updated_at = NOW() WHERE id = @id",
                    new { row.Id });
            }
            catch (FirebaseMessagingException ex) when (
                ex.MessagingErrorCode == MessagingErrorCode.Unregistered ||
                ex.MessagingErrorCode == MessagingErrorCode.InvalidArgument)
            {
                await conn.ExecuteAsync(
                    "UPDATE fcm_tokens SET active = 0, session_active = 0, last_error_at = NOW(), last_error = @error, updated_at = NOW() WHERE id = @id",
                    new { row.Id, error = TrimTo(ex.Message, 500) });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo enviar FCM '{Title}' a {Usuario}.", title, usuario);
                await conn.ExecuteAsync(
                    "UPDATE fcm_tokens SET last_error_at = NOW(), last_error = @error, updated_at = NOW() WHERE id = @id",
                    new { row.Id, error = TrimTo(ex.Message, 500) });
            }
        }

        return sent;
    }

    private sealed class FcmTokenRow
    {
        public long Id { get; set; }
        public string Token { get; set; } = string.Empty;
    }

    public async Task SendSalidaBloqueoAsync(SalidaBloqueoNotification notification, string usuarioOperador)
    {
        if (!IsWebPushConfigured)
        {
            _logger.LogInformation("Web Push no configurado. Se omitio push remoto para bloqueo {Relno}.", notification.Relno);
            return;
        }

        await EnsureSchemaAsync();

        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        var subscriptions = (await conn.QueryAsync<PushSubscriptionRow>(@"
SELECT id, usuario, endpoint, p256dh, auth
FROM push_subscriptions
WHERE active = 1
  AND receive_block_alerts = 1
  AND usuario <> @usuarioOperador",
            new { usuarioOperador })).ToList();

        if (subscriptions.Count == 0)
        {
            _logger.LogInformation("No hay suscripciones push activas para bloqueo {Relno}.", notification.Relno);
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            type = notification.Type,
            title = $"Relacion bloqueada {notification.Relno}",
            body = $"{notification.Usuario}: {notification.Motivo} Caja {EmptyToNd(notification.Caja)}.",
            relno = notification.Relno,
            caja = notification.Caja,
            usuario = notification.Usuario,
            cliente = notification.Cliente,
            motivo = notification.Motivo,
            etiquetasBloqueadas = notification.EtiquetasBloqueadas,
            fecha = notification.Fecha,
            url = notification.Url
        });

        var vapid = new VapidDetails(
            GetVapidSubject(),
            GetVapidPublicKey(),
            GetVapidPrivateKey());

        using var client = new WebPushClient();

        foreach (var row in subscriptions)
        {
            try
            {
                var sub = new PushSubscription(row.Endpoint, row.P256dh, row.Auth);
                await client.SendNotificationAsync(sub, payload, vapid);
                await conn.ExecuteAsync(@"
UPDATE push_subscriptions
SET last_success_at = NOW(), last_error = NULL, updated_at = NOW()
WHERE id = @id", new { row.Id });
            }
            catch (WebPushException ex) when (ex.StatusCode == HttpStatusCode.Gone || ex.StatusCode == HttpStatusCode.NotFound)
            {
                await DisableSubscriptionAsync(conn, row.Id, $"Suscripcion expirada: {(int)ex.StatusCode}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo enviar push de bloqueo {Relno} a {Usuario}.", notification.Relno, row.Usuario);
                await conn.ExecuteAsync(@"
UPDATE push_subscriptions
SET last_error_at = NOW(), last_error = @error, updated_at = NOW()
WHERE id = @id",
                    new { row.Id, error = TrimTo(ex.Message, 500) });
            }
        }
    }

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;

        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaReady) return;

            // push_subscriptions y fcm_tokens viven en pmmutileria, no en pmmtraf.
            using IDbConnection conn = _db.CreatePmmutileriaConnection();
            await conn.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS push_subscriptions (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    usuario VARCHAR(80) NOT NULL,
    endpoint_hash CHAR(64) NOT NULL,
    endpoint TEXT NOT NULL,
    p256dh VARCHAR(255) NOT NULL,
    auth VARCHAR(255) NOT NULL,
    user_agent VARCHAR(255) NULL,
    platform VARCHAR(60) NULL,
    receive_block_alerts TINYINT(1) NOT NULL DEFAULT 0,
    active TINYINT(1) NOT NULL DEFAULT 1,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    last_success_at DATETIME NULL,
    last_error_at DATETIME NULL,
    last_error VARCHAR(500) NULL,
    UNIQUE KEY uq_push_endpoint (endpoint_hash),
    KEY ix_push_user_active (usuario, active),
    KEY ix_push_block_alerts (receive_block_alerts, active)
)");
            await conn.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS fcm_tokens (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    usuario VARCHAR(80) NOT NULL,
    token VARCHAR(255) NOT NULL,
    platform VARCHAR(20) NOT NULL DEFAULT 'android',
    active TINYINT(1) NOT NULL DEFAULT 1,
    session_active TINYINT(1) NOT NULL DEFAULT 0,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    last_success_at DATETIME NULL,
    last_error_at DATETIME NULL,
    last_error VARCHAR(500) NULL,
    UNIQUE KEY uq_fcm_token (token),
    KEY ix_fcm_user_active (usuario, active, session_active)
)");
            int sessionColumnExists = await conn.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'fcm_tokens'
  AND COLUMN_NAME = 'session_active'");
            if (sessionColumnExists == 0)
            {
                await conn.ExecuteAsync(
                    "ALTER TABLE fcm_tokens ADD COLUMN session_active TINYINT(1) NOT NULL DEFAULT 0 AFTER active");
            }
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async Task SaveInboxNotificationAsync(
        string usuario,
        string title,
        string body,
        IDictionary<string, string>? data)
    {
        if (string.IsNullOrWhiteSpace(usuario)) return;

        await EnsureInboxSchemaAsync();

        string type = data != null && data.TryGetValue("type", out string? typeValue)
            ? typeValue ?? "general"
            : "general";
        string? crefAgrupada = data != null && data.TryGetValue("crefAgrupada", out string? crefValue)
            ? crefValue
            : null;
        string? dataJson = data == null ? null : JsonSerializer.Serialize(data);

        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        await conn.ExecuteAsync(@"
INSERT INTO notificaciones_usuario
    (usuario, tipo, titulo, mensaje, cref_agrupada, datos_json)
VALUES
    (@usuario, @type, @title, @body, @crefAgrupada, @dataJson)",
            new
            {
                usuario,
                type = TrimTo(type, 80),
                title = TrimTo(title, 160),
                body = TrimTo(body, 500),
                crefAgrupada = TrimTo(crefAgrupada, 50),
                dataJson
            });

        _logger.LogInformation("Notificación guardada en bandeja para {Usuario}: {Title}.", usuario, title);
    }

    public async Task<NotificationInboxResponse> GetInboxAsync(
        string usuario,
        bool onlyUnread,
        int limit)
    {
        if (string.IsNullOrWhiteSpace(usuario))
        {
            return new NotificationInboxResponse { Success = false };
        }

        await EnsureInboxSchemaAsync();
        limit = Math.Clamp(limit, 1, 100);
        string unreadFilter = onlyUnread ? "AND fecha_leida IS NULL" : string.Empty;

        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        int unreadCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM notificaciones_usuario WHERE usuario = @usuario AND fecha_leida IS NULL",
            new { usuario });
        IReadOnlyList<NotificationInboxItem> items = (await conn.QueryAsync<NotificationInboxItem>($@"
SELECT
    id AS Id,
    tipo AS Type,
    titulo AS Title,
    mensaje AS Body,
    cref_agrupada AS CrefAgrupada,
    fecha_creada AS CreatedAt,
    fecha_leida AS ReadAt
FROM notificaciones_usuario
WHERE usuario = @usuario {unreadFilter}
ORDER BY fecha_creada DESC
LIMIT @limit",
            new { usuario, limit })).AsList();

        return new NotificationInboxResponse
        {
            Success = true,
            UnreadCount = unreadCount,
            Items = items
        };
    }

    public async Task<bool> MarkInboxReadAsync(string usuario, long id)
    {
        if (string.IsNullOrWhiteSpace(usuario)) return false;

        await EnsureInboxSchemaAsync();
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        return await conn.ExecuteAsync(@"
UPDATE notificaciones_usuario
SET fecha_leida = COALESCE(fecha_leida, NOW())
WHERE id = @id AND usuario = @usuario",
            new { id, usuario }) > 0;
    }

    public async Task<int> MarkAllInboxReadAsync(string usuario)
    {
        if (string.IsNullOrWhiteSpace(usuario)) return 0;

        await EnsureInboxSchemaAsync();
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        return await conn.ExecuteAsync(@"
UPDATE notificaciones_usuario
SET fecha_leida = NOW()
WHERE usuario = @usuario AND fecha_leida IS NULL",
            new { usuario });
    }

    private async Task EnsureInboxSchemaAsync()
    {
        if (_inboxSchemaReady) return;

        await _inboxSchemaLock.WaitAsync();
        try
        {
            if (_inboxSchemaReady) return;

            using IDbConnection conn = _db.CreatePmmutileriaConnection();
            await conn.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS notificaciones_usuario (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    usuario VARCHAR(80) NOT NULL,
    tipo VARCHAR(80) NOT NULL,
    titulo VARCHAR(160) NOT NULL,
    mensaje VARCHAR(500) NOT NULL,
    cref_agrupada VARCHAR(50) NULL,
    datos_json TEXT NULL,
    fecha_creada DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    fecha_leida DATETIME NULL,
    KEY ix_notif_usuario_leida_fecha (usuario, fecha_leida, fecha_creada),
    KEY ix_notif_usuario_cref (usuario, cref_agrupada)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci");
            _inboxSchemaReady = true;
        }
        finally
        {
            _inboxSchemaLock.Release();
        }
    }

    private async Task DisableSubscriptionAsync(IDbConnection conn, long id, string error)
    {
        await conn.ExecuteAsync(@"
UPDATE push_subscriptions
SET active = 0, last_error_at = NOW(), last_error = @error, updated_at = NOW()
WHERE id = @id",
            new { id, error = TrimTo(error, 500) });
    }

    private string GetVapidPrivateKey()
    {
        return _config["PushNotifications:Vapid:PrivateKey"]?.Trim() ?? string.Empty;
    }

    private string GetVapidSubject()
    {
        var subject = _config["PushNotifications:Vapid:Subject"]?.Trim();
        return string.IsNullOrWhiteSpace(subject) ? "mailto:desarrollo@grupopmm.com" : subject;
    }

    private static string ComputeSha256Hex(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string TrimTo(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Length <= length ? value : value[..length];
    }

    private static string EmptyToNd(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "N/D" : value;
    }

    private sealed class PushSubscriptionRow
    {
        public long Id { get; set; }
        public string Usuario { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string P256dh { get; set; } = string.Empty;
        public string Auth { get; set; } = string.Empty;
    }
}
