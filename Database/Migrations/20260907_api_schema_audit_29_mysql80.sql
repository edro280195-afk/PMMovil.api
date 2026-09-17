-- ============================================================================
-- PMMovil.Api - cierre de auditoria de esquema para 192.168.10.29
-- Compatibilidad: MySQL 8.0.18. Conserva utf8mb4_0900_ai_ci, que es la
-- collation de reportes en este servidor y no existe en MySQL 5.7.
--
-- Ejecutar conectado a 192.168.10.29 sobre la base reportes. No elimina ni
-- renombra datos. Las comprobaciones de information_schema sustituyen a
-- ADD COLUMN IF NOT EXISTS, que no existe en MySQL 5.7.
-- ============================================================================

SET NAMES utf8mb4;
USE `reportes`;

-- FaceService usa estas columnas al enrolar, listar apariencias y guardar la
-- firma del consentimiento. En .29 faltaban las tres.
SET @existe_etiqueta = (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'face_enrolamiento' AND COLUMN_NAME = 'etiqueta');
SET @sql_etiqueta = IF(@existe_etiqueta = 0, 'ALTER TABLE face_enrolamiento ADD COLUMN etiqueta VARCHAR(40) NULL AFTER nombre', 'SELECT 1');
PREPARE stmt_etiqueta FROM @sql_etiqueta; EXECUTE stmt_etiqueta; DEALLOCATE PREPARE stmt_etiqueta;

SET @existe_firma_png = (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'face_enrolamiento' AND COLUMN_NAME = 'firma_png');
SET @sql_firma_png = IF(@existe_firma_png = 0, 'ALTER TABLE face_enrolamiento ADD COLUMN firma_png MEDIUMBLOB NULL AFTER aceptacion_texto', 'SELECT 1');
PREPARE stmt_firma_png FROM @sql_firma_png; EXECUTE stmt_firma_png; DEALLOCATE PREPARE stmt_firma_png;

SET @existe_firma_at = (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'face_enrolamiento' AND COLUMN_NAME = 'firma_at');
SET @sql_firma_at = IF(@existe_firma_at = 0, 'ALTER TABLE face_enrolamiento ADD COLUMN firma_at DATETIME NULL AFTER firma_png', 'SELECT 1');
PREPARE stmt_firma_at FROM @sql_firma_at; EXECUTE stmt_firma_at; DEALLOCATE PREPARE stmt_firma_at;

UPDATE face_enrolamiento
SET etiqueta = 'Principal'
WHERE etiqueta IS NULL;

SET @existe_idx_apariencias = (SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'face_enrolamiento' AND INDEX_NAME = 'ix_face_enrol_activas');
SET @sql_idx_apariencias = IF(@existe_idx_apariencias = 0, 'ALTER TABLE face_enrolamiento ADD KEY ix_face_enrol_activas (claveusuario, estado, created_at)', 'SELECT 1');
PREPARE stmt_idx_apariencias FROM @sql_idx_apariencias; EXECUTE stmt_idx_apariencias; DEALLOCATE PREPARE stmt_idx_apariencias;

-- El API usa 3 como valor por defecto, pero se deja persistido para que el
-- comportamiento sea igual al de localhost y se pueda ajustar sin desplegar.
INSERT INTO face_config (clave, valor, nota)
VALUES ('max_apariencias', '3', 'Enrolamientos ACTIVO simultaneos por persona.')
ON DUPLICATE KEY UPDATE valor = VALUES(valor), nota = VALUES(nota);

-- ---------------------------------------------------------------------------
-- Laboratorio biometrico. BiometriaLabController esta incluido en el API y
-- consulta estas tablas. Son independientes de las tablas face_* productivas.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `bio_lab_sujeto` (
    `id`           INT NOT NULL AUTO_INCREMENT,
    `claveusuario` VARCHAR(80) NOT NULL,
    `nombre`       VARCHAR(160) NOT NULL,
    `notas`        VARCHAR(255) NULL,
    `activo`       TINYINT(1) NOT NULL DEFAULT 1,
    `created_at`   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (`id`),
    UNIQUE KEY `uq_bio_lab_sujeto` (`claveusuario`)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `bio_lab_plantilla` (
    `id`              BIGINT NOT NULL AUTO_INCREMENT,
    `sujeto_id`       INT NOT NULL,
    `modelo`          VARCHAR(40) NOT NULL DEFAULT 'sface-2021dec',
    `dims`            SMALLINT NOT NULL DEFAULT 128,
    `vector`          BLOB NOT NULL,
    `luz_lux`         FLOAT NULL,
    `luma`            FLOAT NULL,
    `nitidez`         FLOAT NULL,
    `rostro_px`       INT NULL,
    `score_deteccion` FLOAT NULL,
    `yaw`             FLOAT NULL,
    `roll`            FLOAT NULL,
    `dispositivo`     VARCHAR(120) NULL,
    `camara`          VARCHAR(20) NULL,
    `created_at`      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (`id`),
    KEY `ix_bio_lab_plantilla_sujeto` (`sujeto_id`),
    CONSTRAINT `fk_bio_lab_plantilla_sujeto` FOREIGN KEY (`sujeto_id`) REFERENCES `bio_lab_sujeto` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `bio_lab_prueba` (
    `id`                  BIGINT NOT NULL AUTO_INCREMENT,
    `sujeto_esperado_id`  INT NULL,
    `sujeto_top1_id`      INT NULL,
    `sim_top1`            FLOAT NULL,
    `sujeto_top2_id`      INT NULL,
    `sim_top2`            FLOAT NULL,
    `margen`              FLOAT NULL,
    `umbral`              FLOAT NULL,
    `veredicto`           VARCHAR(24) NULL,
    `luz_lux`             FLOAT NULL,
    `luma`                FLOAT NULL,
    `nitidez`             FLOAT NULL,
    `rostro_px`           INT NULL,
    `score_deteccion`     FLOAT NULL,
    `yaw`                 FLOAT NULL,
    `roll`                FLOAT NULL,
    `ms_deteccion`        INT NULL,
    `ms_embedding`        INT NULL,
    `ms_total`            INT NULL,
    `escenario`           VARCHAR(60) NULL,
    `pantalla_flash`      TINYINT(1) NOT NULL DEFAULT 0,
    `exposicion`          INT NULL,
    `dispositivo`         VARCHAR(120) NULL,
    `camara`              VARCHAR(20) NULL,
    `created_at`          DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (`id`),
    KEY `ix_bio_lab_prueba_fecha` (`created_at`),
    KEY `ix_bio_lab_prueba_escenario` (`escenario`)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_ROWS
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = 'reportes'
  AND TABLE_NAME IN ('face_enrolamiento', 'face_plantilla', 'face_intento', 'face_config', 'bio_lab_sujeto', 'bio_lab_plantilla', 'bio_lab_prueba')
ORDER BY TABLE_NAME;
