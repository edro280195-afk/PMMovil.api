-- Alertas operativas persistentes del centro de control de arribos.
--
-- La clave es determinista por tipo y folio para que una misma situación no
-- genere una tarjeta nueva cada vez que llega una señal o se abre el panel.
-- La migración es idempotente y compatible con MySQL 5.7.

USE `pmmutileria`;

CREATE TABLE IF NOT EXISTS `preregistro_alertas` (
    `id` BIGINT NOT NULL AUTO_INCREMENT,
    `clave` VARCHAR(80) NOT NULL,
    `tipo` VARCHAR(30) NOT NULL,
    `prioridad` VARCHAR(10) NOT NULL,
    `folio` VARCHAR(24) NOT NULL,
    `id_cuenta` INT DEFAULT NULL,
    `id_bodega` VARCHAR(5) DEFAULT NULL,
    `titulo` VARCHAR(120) NOT NULL,
    `detalle` VARCHAR(255) NOT NULL,
    `estado` VARCHAR(12) NOT NULL DEFAULT 'PENDIENTE',
    `fecha_crea` DATETIME NOT NULL,
    `fecha_condicion` DATETIME DEFAULT NULL,
    `fecha_atiende` DATETIME DEFAULT NULL,
    `usuario_atiende` VARCHAR(30) DEFAULT NULL,
    PRIMARY KEY (`id`),
    UNIQUE KEY `uq_preregistro_alerta_clave` (`clave`),
    KEY `ix_preregistro_alerta_estado_fecha` (`estado`, `fecha_crea`),
    KEY `ix_preregistro_alerta_folio` (`folio`),
    KEY `ix_preregistro_alerta_folio_tipo_estado` (`folio`, `tipo`, `estado`)
) ENGINE=InnoDB
  DEFAULT CHARACTER SET latin1
  COLLATE latin1_swedish_ci;

-- Compatibilidad con la tabla creada por la primera version de la alerta.
SET @tiene_fecha_condicion = (
    SELECT COUNT(*)
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'pmmutileria'
      AND TABLE_NAME = 'preregistro_alertas'
      AND COLUMN_NAME = 'fecha_condicion'
);
SET @sql_fecha_condicion = IF(
    @tiene_fecha_condicion = 0,
    'ALTER TABLE `pmmutileria`.`preregistro_alertas` ADD COLUMN `fecha_condicion` DATETIME DEFAULT NULL AFTER `fecha_crea`',
    'SELECT 1'
);
PREPARE stmt_fecha_condicion FROM @sql_fecha_condicion;
EXECUTE stmt_fecha_condicion;
DEALLOCATE PREPARE stmt_fecha_condicion;
