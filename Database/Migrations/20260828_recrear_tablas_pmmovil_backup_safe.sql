-- =====================================================================
-- Esquema requerido por PMMovil.Api
-- Servidor objetivo: 192.168.10.30 / MySQL 5.7.42
--
-- IMPORTANTE:
--   - preregistro_* y caseta_preregistro viven en pmmutileria (movidas el
--     2026-09-05, ver 20260905_mover_preregistro_a_pmmutileria.sql -- antes
--     vivían por error en pmmtraf).
--   - fcm_tokens y push_subscriptions viven en pmmutileria.
--   - No se usa DROP TABLE para no borrar datos.
--   - Se usa latin1 para mantener compatibilidad con el esquema histórico
--     de la oficina y con los respaldos existentes.
--   - No se usan fechas cero como DEFAULT; esto evita fallos al restaurar
--     en servidores con modo SQL estricto.
--   - fcm_tokens incluye session_active porque RealtimeNotificationService
--     la consulta y actualiza.
-- =====================================================================

SET NAMES latin1;

SET @OLD_FOREIGN_KEY_CHECKS = @@FOREIGN_KEY_CHECKS;
SET FOREIGN_KEY_CHECKS = 0;

-- ---------------------------------------------------------------------
-- pmmutileria: preregistro de transportistas (movido desde pmmtraf el
-- 2026-09-05, ver 20260905_mover_preregistro_a_pmmutileria.sql)
-- ---------------------------------------------------------------------

USE `pmmutileria`;

CREATE TABLE IF NOT EXISTS `preregistro_cuentas` (
    `id` INT NOT NULL AUTO_INCREMENT,
    `usuario` VARCHAR(30) NOT NULL,
    `password_hash` VARCHAR(255) NOT NULL,
    `cidtranspor` VARCHAR(10) NOT NULL,
    `nombre_linea` VARCHAR(100) NOT NULL,
    `contacto` VARCHAR(100) DEFAULT NULL,
    `activo` TINYINT(1) NOT NULL DEFAULT 1,
    `fcrea` DATETIME NOT NULL,
    `usucrea` VARCHAR(30) NOT NULL,
    `fmodi` DATETIME DEFAULT NULL,
    `usumodi` VARCHAR(30) DEFAULT NULL,
    `fultimo_acceso` DATETIME DEFAULT NULL,
    `intentos_fallidos` INT NOT NULL DEFAULT 0,
    `fbloqueo` DATETIME DEFAULT NULL,
    PRIMARY KEY (`id`),
    UNIQUE KEY `uq_usuario` (`usuario`),
    KEY `ix_transpor` (`cidtranspor`)
) ENGINE=InnoDB
  DEFAULT CHARACTER SET latin1
  COLLATE latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS `preregistro_auditoria` (
    `id` BIGINT NOT NULL AUTO_INCREMENT,
    `fecha` DATETIME NOT NULL,
    `evento` VARCHAR(30) NOT NULL,
    `id_cuenta` INT DEFAULT NULL,
    `usuario` VARCHAR(30) DEFAULT NULL,
    `folio` VARCHAR(24) DEFAULT NULL,
    `ip` VARCHAR(45) DEFAULT NULL,
    `detalle` VARCHAR(255) DEFAULT NULL,
    PRIMARY KEY (`id`),
    KEY `ix_fecha` (`fecha`),
    KEY `ix_cuenta` (`id_cuenta`),
    KEY `ix_evento` (`evento`)
) ENGINE=InnoDB
  DEFAULT CHARACTER SET latin1
  COLLATE latin1_swedish_ci;

-- caseta_preregistro es usado por PreregistroService. Se incluye para que
-- una restauración limpia no dependa de que la tabla exista previamente.
CREATE TABLE IF NOT EXISTS `caseta_preregistro` (
    `id` INT NOT NULL AUTO_INCREMENT,
    `folio` VARCHAR(24) NOT NULL,
    `token` CHAR(32) NOT NULL,
    `id_cuenta` INT NOT NULL,
    `nunidad` VARCHAR(30) NOT NULL,
    `idtransporu` VARCHAR(10) DEFAULT NULL,
    `transporu` VARCHAR(100) DEFAULT NULL,
    `placas` VARCHAR(25) NOT NULL,
    `operador` VARCHAR(100) NOT NULL,
    `idbodega` VARCHAR(5) DEFAULT NULL,
    `lat` DECIMAL(9,6) DEFAULT NULL,
    `lon` DECIMAL(9,6) DEFAULT NULL,
    `fubicacion` DATETIME DEFAULT NULL,
    `eta_minutos` INT DEFAULT NULL,
    `eta_km` DECIMAL(7,2) DEFAULT NULL,
    `feta` DATETIME DEFAULT NULL,
    `estatus` VARCHAR(15) NOT NULL,
    `fcrea` DATETIME NOT NULL,
    `fexpira` DATETIME NOT NULL,
    `fpurga` DATETIME NOT NULL,
    `fuso` DATETIME DEFAULT NULL,
    `usuario_uso` VARCHAR(30) DEFAULT NULL,
    `casetaid` VARCHAR(20) DEFAULT NULL,
    `ip_origen` VARCHAR(45) DEFAULT NULL,
    PRIMARY KEY (`id`),
    UNIQUE KEY `uq_folio` (`folio`),
    UNIQUE KEY `uq_token` (`token`),
    KEY `ix_purga` (`fpurga`),
    KEY `ix_estatus_expira` (`estatus`, `fexpira`),
    KEY `ix_cuenta` (`id_cuenta`)
) ENGINE=InnoDB
  DEFAULT CHARACTER SET latin1
  COLLATE latin1_swedish_ci;

-- ---------------------------------------------------------------------
-- pmmutileria: notificaciones de PMMovil.Api
-- ---------------------------------------------------------------------

USE `pmmutileria`;

CREATE TABLE IF NOT EXISTS `fcm_tokens` (
    `id` BIGINT NOT NULL AUTO_INCREMENT,
    `usuario` VARCHAR(80) NOT NULL,
    -- Los tokens FCM son ASCII y se comparan de forma sensible a mayúsculas.
    -- Esto mantiene el índice por debajo del límite antiguo de InnoDB.
    `token` VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    `platform` VARCHAR(20) NOT NULL DEFAULT 'android',
    `active` TINYINT(1) NOT NULL DEFAULT 1,
    `session_active` TINYINT(1) NOT NULL DEFAULT 0,
    `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
        ON UPDATE CURRENT_TIMESTAMP,
    `last_success_at` DATETIME DEFAULT NULL,
    `last_error_at` DATETIME DEFAULT NULL,
    `last_error` VARCHAR(500) DEFAULT NULL,
    PRIMARY KEY (`id`),
    UNIQUE KEY `uq_fcm_token` (`token`),
    KEY `ix_fcm_user_active` (`usuario`, `active`, `session_active`)
) ENGINE=InnoDB
  DEFAULT CHARACTER SET latin1
  COLLATE latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS `push_subscriptions` (
    `id` BIGINT NOT NULL AUTO_INCREMENT,
    `usuario` VARCHAR(80) NOT NULL,
    `endpoint_hash` CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    `endpoint` TEXT NOT NULL,
    `p256dh` VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    `auth` VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    `user_agent` VARCHAR(255) DEFAULT NULL,
    `platform` VARCHAR(60) DEFAULT NULL,
    `receive_block_alerts` TINYINT(1) NOT NULL DEFAULT 0,
    `active` TINYINT(1) NOT NULL DEFAULT 1,
    `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
        ON UPDATE CURRENT_TIMESTAMP,
    `last_success_at` DATETIME DEFAULT NULL,
    `last_error_at` DATETIME DEFAULT NULL,
    `last_error` VARCHAR(500) DEFAULT NULL,
    PRIMARY KEY (`id`),
    UNIQUE KEY `uq_push_endpoint` (`endpoint_hash`),
    KEY `ix_push_user_active` (`usuario`, `active`),
    KEY `ix_push_block_alerts` (`receive_block_alerts`, `active`)
) ENGINE=InnoDB
  DEFAULT CHARACTER SET latin1
  COLLATE latin1_swedish_ci;

-- ---------------------------------------------------------------------
-- Normalización idempotente para instalaciones donde las tablas ya existen
-- con el esquema anterior y defaults 0000-00-00.
-- ---------------------------------------------------------------------

-- MySQL 5.7 no soporta ADD COLUMN IF NOT EXISTS. Esta comprobación permite
-- reparar una fcm_tokens antigua que aún no tenga session_active.
SET @session_active_exists = (
    SELECT COUNT(*)
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'fcm_tokens'
      AND COLUMN_NAME = 'session_active'
);
SET @add_session_active_sql = IF(
    @session_active_exists = 0,
    'ALTER TABLE `fcm_tokens` ADD COLUMN `session_active` TINYINT(1) NOT NULL DEFAULT 0 AFTER `active`',
    'SELECT 1'
);
PREPARE add_session_active_stmt FROM @add_session_active_sql;
EXECUTE add_session_active_stmt;
DEALLOCATE PREPARE add_session_active_stmt;

ALTER TABLE `fcm_tokens`
    MODIFY COLUMN `token` VARCHAR(255)
        CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    MODIFY COLUMN `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    MODIFY COLUMN `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
        ON UPDATE CURRENT_TIMESTAMP,
    DROP INDEX `ix_fcm_user_active`,
    ADD KEY `ix_fcm_user_active` (`usuario`, `active`, `session_active`);

ALTER TABLE `push_subscriptions`
    MODIFY COLUMN `endpoint_hash` CHAR(64)
        CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    MODIFY COLUMN `p256dh` VARCHAR(255)
        CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    MODIFY COLUMN `auth` VARCHAR(255)
        CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    MODIFY COLUMN `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    MODIFY COLUMN `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
        ON UPDATE CURRENT_TIMESTAMP;

-- La instalación actual de .30 tiene algunos registros de fcm_tokens con
-- created_at='0000-00-00 00:00:00' y updated_at válido. Se conserva la fecha
-- disponible de actualización para que el INSERT del respaldo no falle en
-- un destino con NO_ZERO_DATE. No modifica registros con fechas válidas.
UPDATE `fcm_tokens`
SET `created_at` = `updated_at`
WHERE `created_at` = '0000-00-00 00:00:00'
  AND `updated_at` <> '0000-00-00 00:00:00';

-- La tabla existente en .30 fue creada con defaults de fecha cero, aunque el
-- API siempre manda fcrea/fexpira/fpurga explícitamente.
USE `pmmutileria`;
ALTER TABLE `caseta_preregistro`
    MODIFY COLUMN `fcrea` DATETIME NOT NULL,
    MODIFY COLUMN `fexpira` DATETIME NOT NULL,
    MODIFY COLUMN `fpurga` DATETIME NOT NULL;

SET FOREIGN_KEY_CHECKS = @OLD_FOREIGN_KEY_CHECKS;
