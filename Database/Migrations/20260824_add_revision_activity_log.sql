-- Bitácora persistente de actividad de revisiones.
-- Ejecutar una sola vez en la base pmmutileria antes de publicar las APIs.
-- No guarda valores capturados ni fotografías; solo el hecho operativo,
-- quién lo realizó y la referencia necesaria para auditarlo.

CREATE TABLE IF NOT EXISTS revision_actividad (
    id BIGINT NOT NULL AUTO_INCREMENT,
    creferencia VARCHAR(100) NOT NULL,
    usuario VARCHAR(100) NOT NULL,
    tipo VARCHAR(64) NOT NULL,
    cnofact VARCHAR(100) NULL,
    idrevdet INT NULL,
    detalle VARCHAR(255) NULL,
    fecha DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    origen VARCHAR(24) NOT NULL DEFAULT 'PMMOVIL',
    PRIMARY KEY (id),
    KEY ix_revision_actividad_fecha (fecha, id),
    KEY ix_revision_actividad_referencia_fecha (creferencia, fecha, id),
    KEY ix_revision_actividad_usuario_fecha (usuario, fecha, id)
) ENGINE=InnoDB
  DEFAULT CHARSET=utf8mb4
  COLLATE=utf8mb4_unicode_ci;
