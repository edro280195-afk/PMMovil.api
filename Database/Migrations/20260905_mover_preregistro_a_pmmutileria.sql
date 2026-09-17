-- =====================================================================
-- Mueve el pre-registro de transportistas de pmmtraf a pmmutileria
-- Servidor objetivo: MySQL 5.7 (correr en cada servidor donde exista
-- pmmtraf.preregistro_cuentas / caseta_preregistro / preregistro_auditoria:
-- local de desarrollo, 192.168.10.30, etc.)
--
-- CONTEXTO:
--   Estas tres tablas se crearon originalmente en pmmtraf (ver
--   20260828_recrear_tablas_pmmovil_backup_safe.sql, ya corregido). Deben
--   vivir en pmmutileria en vez de pmmtraf (la base legacy que sí lee FoxPro)
--   y en vez de reportes -- mismo criterio que ya se usó para fcm_tokens y
--   push_subscriptions: son tablas propias de PMMovil.Api, no del sistema
--   legacy. PreregistroService.cs ya fue actualizado para leer/escribir
--   contra pmmutileria via BaseRepository.CreatePmmutileriaConnection().
--
--   Excepción a propósito: el folio sigue saliendo de `consecutivo`
--   (formulario='CASETAPR'), que es una tabla legacy COMPARTIDA con el resto
--   del sistema y se queda en pmmtraf -- no se toca aquí.
--
-- QUÉ HACE:
--   1. Crea las 3 tablas en pmmutileria (mismo esquema que ya tenían en pmmtraf).
--   2. Copia los datos existentes de pmmtraf -> pmmutileria (solo si
--      pmmutileria todavía está vacía -- correr esto dos veces no duplica).
--   3. NO borra las tablas de pmmtraf (no se usa DROP TABLE) -- las renombra
--      a `<tabla>_migrado_20260905` para que quede claro que ya no son la
--      fuente viva, sin perder el historial por si hay que auditar algo.
--
-- Verificar ANTES de correr en un servidor que tenga datos reales: que
-- pmmutileria exista como base de datos (créala si hace falta) y que el
-- usuario de conexión tenga permisos en ambas bases.
-- =====================================================================

SET NAMES latin1;

SET @OLD_FOREIGN_KEY_CHECKS = @@FOREIGN_KEY_CHECKS;
SET FOREIGN_KEY_CHECKS = 0;

-- ---------------------------------------------------------------------
-- 1. Crear las tablas en pmmutileria (mismo esquema que en pmmtraf)
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
-- 2. Copiar datos de pmmtraf -> pmmutileria (solo si el destino está vacío,
--    para que correr este script dos veces no duplique nada). Ambas bases
--    están en el mismo servidor, así que basta con calificar pmmtraf.<tabla>.
-- ---------------------------------------------------------------------

SET @hay_datos_cuentas = (SELECT COUNT(*) FROM `pmmutileria`.`preregistro_cuentas`);
SET @copiar_cuentas_sql = IF(
    @hay_datos_cuentas = 0,
    'INSERT INTO `pmmutileria`.`preregistro_cuentas` SELECT * FROM `pmmtraf`.`preregistro_cuentas`',
    'SELECT 1'
);
PREPARE stmt_cuentas FROM @copiar_cuentas_sql;
EXECUTE stmt_cuentas;
DEALLOCATE PREPARE stmt_cuentas;

SET @hay_datos_auditoria = (SELECT COUNT(*) FROM `pmmutileria`.`preregistro_auditoria`);
SET @copiar_auditoria_sql = IF(
    @hay_datos_auditoria = 0,
    'INSERT INTO `pmmutileria`.`preregistro_auditoria` SELECT * FROM `pmmtraf`.`preregistro_auditoria`',
    'SELECT 1'
);
PREPARE stmt_auditoria FROM @copiar_auditoria_sql;
EXECUTE stmt_auditoria;
DEALLOCATE PREPARE stmt_auditoria;

SET @hay_datos_caseta = (SELECT COUNT(*) FROM `pmmutileria`.`caseta_preregistro`);
SET @copiar_caseta_sql = IF(
    @hay_datos_caseta = 0,
    'INSERT INTO `pmmutileria`.`caseta_preregistro` SELECT * FROM `pmmtraf`.`caseta_preregistro`',
    'SELECT 1'
);
PREPARE stmt_caseta FROM @copiar_caseta_sql;
EXECUTE stmt_caseta;
DEALLOCATE PREPARE stmt_caseta;

-- ---------------------------------------------------------------------
-- 3. Renombrar (no borrar) las tablas viejas en pmmtraf, solo si todavía
--    existen con su nombre original -- así el script es seguro de re-correr.
-- ---------------------------------------------------------------------

SET @existe_pmmtraf_cuentas = (
    SELECT COUNT(*) FROM information_schema.TABLES
    WHERE TABLE_SCHEMA = 'pmmtraf' AND TABLE_NAME = 'preregistro_cuentas'
);
SET @renombrar_cuentas_sql = IF(
    @existe_pmmtraf_cuentas > 0,
    'RENAME TABLE `pmmtraf`.`preregistro_cuentas` TO `pmmtraf`.`preregistro_cuentas_migrado_20260905`',
    'SELECT 1'
);
PREPARE stmt_ren_cuentas FROM @renombrar_cuentas_sql;
EXECUTE stmt_ren_cuentas;
DEALLOCATE PREPARE stmt_ren_cuentas;

SET @existe_pmmtraf_auditoria = (
    SELECT COUNT(*) FROM information_schema.TABLES
    WHERE TABLE_SCHEMA = 'pmmtraf' AND TABLE_NAME = 'preregistro_auditoria'
);
SET @renombrar_auditoria_sql = IF(
    @existe_pmmtraf_auditoria > 0,
    'RENAME TABLE `pmmtraf`.`preregistro_auditoria` TO `pmmtraf`.`preregistro_auditoria_migrado_20260905`',
    'SELECT 1'
);
PREPARE stmt_ren_auditoria FROM @renombrar_auditoria_sql;
EXECUTE stmt_ren_auditoria;
DEALLOCATE PREPARE stmt_ren_auditoria;

SET @existe_pmmtraf_caseta = (
    SELECT COUNT(*) FROM information_schema.TABLES
    WHERE TABLE_SCHEMA = 'pmmtraf' AND TABLE_NAME = 'caseta_preregistro'
);
SET @renombrar_caseta_sql = IF(
    @existe_pmmtraf_caseta > 0,
    'RENAME TABLE `pmmtraf`.`caseta_preregistro` TO `pmmtraf`.`caseta_preregistro_migrado_20260905`',
    'SELECT 1'
);
PREPARE stmt_ren_caseta FROM @renombrar_caseta_sql;
EXECUTE stmt_ren_caseta;
DEALLOCATE PREPARE stmt_ren_caseta;

SET FOREIGN_KEY_CHECKS = @OLD_FOREIGN_KEY_CHECKS;

-- ---------------------------------------------------------------------
-- Verificación sugerida después de correrlo:
--   SELECT COUNT(*) FROM pmmutileria.preregistro_cuentas;
--   SELECT COUNT(*) FROM pmmtraf.preregistro_cuentas_migrado_20260905;
--   (los dos counts deben coincidir con lo que había antes en pmmtraf)
-- ---------------------------------------------------------------------
