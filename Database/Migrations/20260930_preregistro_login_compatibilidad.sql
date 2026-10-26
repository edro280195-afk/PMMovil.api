-- Compatibilidad del login del portal de pre-registro.
--
-- Algunas instalaciones pueden conservar una tabla preregistro_cuentas
-- creada antes de agregar el bloqueo por intentos fallidos. En ese caso el
-- alta de cuentas puede funcionar, pero el SELECT del login falla porque
-- intenta leer intentos_fallidos y fbloqueo.
--
-- El script es idempotente, no elimina datos y funciona en MySQL 5.7.

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

SET @tiene_fultimo_acceso = (
    SELECT COUNT(*)
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'pmmutileria'
      AND TABLE_NAME = 'preregistro_cuentas'
      AND COLUMN_NAME = 'fultimo_acceso'
);
SET @sql_fultimo_acceso = IF(
    @tiene_fultimo_acceso = 0,
    'ALTER TABLE `pmmutileria`.`preregistro_cuentas` ADD COLUMN `fultimo_acceso` DATETIME DEFAULT NULL AFTER `usumodi`',
    'SELECT 1'
);
PREPARE stmt_fultimo_acceso FROM @sql_fultimo_acceso;
EXECUTE stmt_fultimo_acceso;
DEALLOCATE PREPARE stmt_fultimo_acceso;

SET @tiene_intentos = (
    SELECT COUNT(*)
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'pmmutileria'
      AND TABLE_NAME = 'preregistro_cuentas'
      AND COLUMN_NAME = 'intentos_fallidos'
);
SET @sql_intentos = IF(
    @tiene_intentos = 0,
    'ALTER TABLE `pmmutileria`.`preregistro_cuentas` ADD COLUMN `intentos_fallidos` INT NOT NULL DEFAULT 0 AFTER `fultimo_acceso`',
    'SELECT 1'
);
PREPARE stmt_intentos FROM @sql_intentos;
EXECUTE stmt_intentos;
DEALLOCATE PREPARE stmt_intentos;

SET @tiene_bloqueo = (
    SELECT COUNT(*)
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'pmmutileria'
      AND TABLE_NAME = 'preregistro_cuentas'
      AND COLUMN_NAME = 'fbloqueo'
);
SET @sql_bloqueo = IF(
    @tiene_bloqueo = 0,
    'ALTER TABLE `pmmutileria`.`preregistro_cuentas` ADD COLUMN `fbloqueo` DATETIME DEFAULT NULL AFTER `intentos_fallidos`',
    'SELECT 1'
);
PREPARE stmt_bloqueo FROM @sql_bloqueo;
EXECUTE stmt_bloqueo;
DEALLOCATE PREPARE stmt_bloqueo;

SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = 'pmmutileria'
  AND TABLE_NAME = 'preregistro_cuentas'
  AND COLUMN_NAME IN ('fultimo_acceso', 'intentos_fallidos', 'fbloqueo')
ORDER BY ORDINAL_POSITION;
