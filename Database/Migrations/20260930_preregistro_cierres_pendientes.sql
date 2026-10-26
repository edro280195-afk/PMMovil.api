-- Cierres pendientes de pre-registro despues de guardar una entrada de caseta.
--
-- Guardar la entrada y marcar el QR son operaciones contra bases distintas.
-- Esta tabla evita perder el cierre si la segunda conexion falla, si la app
-- se queda sin red o si el guardia reintenta la misma operacion.
-- Compatible con MySQL 5.7 y ejecucion repetida.

USE `pmmutileria`;

CREATE TABLE IF NOT EXISTS `preregistro_cierres_pendientes` (
    `id` BIGINT NOT NULL AUTO_INCREMENT,
    `folio` VARCHAR(24) NOT NULL,
    `casetaid` VARCHAR(80) NOT NULL,
    `usuario` VARCHAR(30) NOT NULL,
    `placas_no_coinciden` TINYINT(1) NOT NULL DEFAULT 0,
    `estado` VARCHAR(12) NOT NULL DEFAULT 'PENDIENTE',
    `intentos` INT NOT NULL DEFAULT 0,
    `ultimo_error` VARCHAR(255) DEFAULT NULL,
    `fcrea` DATETIME NOT NULL,
    `fultimo_intento` DATETIME DEFAULT NULL,
    `fresuelto` DATETIME DEFAULT NULL,
    PRIMARY KEY (`id`),
    UNIQUE KEY `uq_preregistro_cierre_folio_caseta` (`folio`, `casetaid`),
    KEY `ix_preregistro_cierre_estado_intento` (`estado`, `fultimo_intento`),
    KEY `ix_preregistro_cierre_folio` (`folio`)
) ENGINE=InnoDB
  DEFAULT CHARACTER SET latin1
  COLLATE latin1_swedish_ci;
