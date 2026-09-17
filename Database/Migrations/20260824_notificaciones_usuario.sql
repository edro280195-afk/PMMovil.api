-- Bandeja persistente de notificaciones de PMMovil.
-- El API también ejecuta este CREATE de forma idempotente al primer acceso,
-- pero se deja el script para aplicar el esquema de forma explícita.

CREATE TABLE IF NOT EXISTS pmmutileria.notificaciones_usuario (
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
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
