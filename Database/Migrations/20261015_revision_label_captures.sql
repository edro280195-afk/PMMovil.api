-- Captura segura de etiquetas. Preparado para revisión; NO ejecutar automáticamente.
-- Ejecutar en la base de tráfico configurada (pmmtraf), nunca pmmutileria.
-- Requiere traf3.hazmat_grupo CHAR(3), migración 2026-10-14-hazmat-grupo.sql.
-- Es aditiva: no reemplaza fotos ni cambia la semántica legacy del tipo 107.
CREATE TABLE IF NOT EXISTS revision_label_captures (
    capture_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    idrevdet INT NOT NULL,
    cref_agrupada VARCHAR(30) NOT NULL,
    cnofact VARCHAR(60) NOT NULL,
    usuario VARCHAR(100) NOT NULL,
    request_hash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    photo_hash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    idfotos INT NOT NULL,
    request_json LONGTEXT NOT NULL,
    before_json LONGTEXT NOT NULL,
    after_json LONGTEXT NOT NULL,
    response_json LONGTEXT NOT NULL,
    physical_confirmed TINYINT(1) NOT NULL DEFAULT 0,
    marked_reviewed TINYINT(1) NOT NULL DEFAULT 0,
    created_at DATETIME NOT NULL,
    PRIMARY KEY (capture_id),
    UNIQUE KEY ux_revision_label_captures_foto (idfotos),
    KEY ix_revision_label_captures_partida (idrevdet, created_at),
    KEY ix_revision_label_captures_referencia (cref_agrupada, created_at)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4;

-- Verificación (solo lectura):
SELECT table_name, engine FROM information_schema.tables
 WHERE table_schema=DATABASE() AND table_name IN ('revdet','traf3','fotos','revision_label_captures');
-- Los cuatro deben ser InnoDB antes de habilitar confirmación conjunta.
-- Reversa: deshabilitar primero el endpoint; respaldar la tabla y sus fotos.
-- NO borrar la tabla automáticamente: contiene auditoría y claves de idempotencia.
