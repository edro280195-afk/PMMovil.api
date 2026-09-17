-- fcm_tokens y push_subscriptions pasan de pmmtraf a pmmutileria (el API ya
-- cambió sus conexiones, ver RealtimeNotificationService.cs). El API también
-- ejecuta estos CREATE de forma idempotente al primer acceso, pero se deja el
-- script para aplicar el esquema de forma explícita y, sobre todo, para migrar
-- lo que ya esté registrado hoy en pmmtraf antes de que quede huérfano.

CREATE TABLE IF NOT EXISTS pmmutileria.fcm_tokens (
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
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS pmmutileria.push_subscriptions (
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
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Copia lo que ya exista en pmmtraf (si esas tablas existen ahí) sin duplicar
-- si el script se corre más de una vez.
INSERT INTO pmmutileria.fcm_tokens
    (usuario, token, platform, active, session_active, created_at, updated_at,
     last_success_at, last_error_at, last_error)
SELECT
    old.usuario, old.token, old.platform, old.active, old.session_active,
    old.created_at, old.updated_at, old.last_success_at, old.last_error_at, old.last_error
FROM pmmtraf.fcm_tokens old
ON DUPLICATE KEY UPDATE
    usuario = old.usuario,
    platform = old.platform,
    active = old.active,
    session_active = old.session_active,
    updated_at = old.updated_at,
    last_success_at = old.last_success_at,
    last_error_at = old.last_error_at,
    last_error = old.last_error;

INSERT INTO pmmutileria.push_subscriptions
    (usuario, endpoint_hash, endpoint, p256dh, auth, user_agent, platform,
     receive_block_alerts, active, created_at, updated_at,
     last_success_at, last_error_at, last_error)
SELECT
    old.usuario, old.endpoint_hash, old.endpoint, old.p256dh, old.auth, old.user_agent, old.platform,
    old.receive_block_alerts, old.active, old.created_at, old.updated_at,
    old.last_success_at, old.last_error_at, old.last_error
FROM pmmtraf.push_subscriptions old
ON DUPLICATE KEY UPDATE
    usuario = old.usuario,
    endpoint = old.endpoint,
    p256dh = old.p256dh,
    auth = old.auth,
    user_agent = old.user_agent,
    platform = old.platform,
    receive_block_alerts = old.receive_block_alerts,
    active = old.active,
    updated_at = old.updated_at,
    last_success_at = old.last_success_at,
    last_error_at = old.last_error_at,
    last_error = old.last_error;

-- Una vez confirmado que la migración de datos quedó bien, borrar las tablas
-- viejas a mano (no se automatiza aquí a propósito):
-- DROP TABLE pmmtraf.fcm_tokens;
-- DROP TABLE pmmtraf.push_subscriptions;
