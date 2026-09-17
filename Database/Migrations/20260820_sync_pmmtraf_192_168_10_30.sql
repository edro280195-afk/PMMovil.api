
ALTER TABLE caseta
    ADD COLUMN tiporegistro VARCHAR(15) NOT NULL DEFAULT 'CAJA' AFTER tipomov;

-- 1.2) traf3.danada_descripcion -- feature de Mercancia Dañada en
-- Entrada Bodega. 
ALTER TABLE traf3
    ADD COLUMN danada_descripcion VARCHAR(500) NULL AFTER danada;


-- ---------------------------------------------------------------------
-- PARTE 2 de 2: tablas que NO existen en .30 y el API SI usa
-- ---------------------------------------------------------------------

-- 2.1) caseta_preregistro -- pre-registro de arribo (folio/token/GPS/ETA)
-- que el chofer completa desde el portal antes de llegar a caseta.
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
  KEY `ix_estatus_expira` (`estatus`,`fexpira`),
  KEY `ix_cuenta` (`id_cuenta`)
) ENGINE=INNODB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- 2.2) fcm_tokens -- tokens de Firebase Cloud Messaging por usuario/
-- dispositivo, usados para las notificaciones push
CREATE TABLE IF NOT EXISTS `fcm_tokens` (
  `id` BIGINT NOT NULL AUTO_INCREMENT,
  `usuario` VARCHAR(80) NOT NULL,
  `token` VARCHAR(255) NOT NULL,
  `platform` VARCHAR(20) NOT NULL DEFAULT 'android',
  `active` TINYINT(1) NOT NULL DEFAULT '1',
  `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `last_success_at` DATETIME DEFAULT NULL,
  `last_error_at` DATETIME DEFAULT NULL,
  `last_error` VARCHAR(500) DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_fcm_token` (`token`),
  KEY `ix_fcm_user_active` (`usuario`,`active`)
) ENGINE=INNODB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- 2.3) preregistro_auditoria -- bitacora de eventos del flujo de
-- pre-registro (creado, usado, expirado, purgado, etc.).
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
) ENGINE=INNODB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- 2.4) preregistro_cuentas -- cuentas de las lineas de transporte que
-- pueden generar pre-registros desde el portal (login propio, con
-- bloqueo por intentos fallidos vía intentos_fallidos/fbloqueo).
CREATE TABLE IF NOT EXISTS `preregistro_cuentas` (
  `id` INT NOT NULL AUTO_INCREMENT,
  `usuario` VARCHAR(30) NOT NULL,
  `password_hash` VARCHAR(255) NOT NULL,
  `cidtranspor` VARCHAR(10) NOT NULL,
  `nombre_linea` VARCHAR(100) NOT NULL,
  `contacto` VARCHAR(100) DEFAULT NULL,
  `activo` TINYINT NOT NULL DEFAULT '1',
  `fcrea` DATETIME NOT NULL,
  `usucrea` VARCHAR(30) NOT NULL,
  `fmodi` DATETIME DEFAULT NULL,
  `usumodi` VARCHAR(30) DEFAULT NULL,
  `fultimo_acceso` DATETIME DEFAULT NULL,
  `intentos_fallidos` INT NOT NULL DEFAULT '0',
  `fbloqueo` DATETIME DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_usuario` (`usuario`),
  KEY `ix_transpor` (`cidtranspor`)
) ENGINE=INNODB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;


-- 2.5) salida_carga_qr -- QR de confirmacion de carga en la caja de salida (bitacora y conteo a cuando carguen la caja)
CREATE TABLE IF NOT EXISTS `salida_carga_qr` (
  `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `relno` VARCHAR(32) NOT NULL,
  `casetaid` VARCHAR(64) NOT NULL,
  `usuario` VARCHAR(100) NOT NULL,
  `token` CHAR(32) NOT NULL,
  `estado` VARCHAR(20) NOT NULL DEFAULT 'PENDIENTE',
  `leido_at` DATETIME NOT NULL,
  `expira_at` DATETIME NOT NULL,
  `consumido_at` DATETIME DEFAULT NULL,
  `texto_bulto` VARCHAR(255) DEFAULT NULL,
  `motivo` VARCHAR(100) DEFAULT NULL,
  `ip_address` VARCHAR(45) NOT NULL DEFAULT '',
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_salida_carga_qr_token` (`token`),
  KEY `idx_salida_carga_qr_pendiente` (`relno`,`usuario`,`estado`,`expira_at`),
  KEY `idx_salida_carga_qr_caseta` (`casetaid`,`leido_at`)
) ENGINE=INNODB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;


-- =====================================================================
-- OPCIONAL -- NO forma parte de lo que el API necesita hoy, se deja
-- comentado a proposito. Depende de que la PARTE 1.1 ya se haya
-- corrido (usa caseta.tiporegistro).
--
-- view_caseta_tractocamiones_disponibles: vista para que FoxPro busque
-- tractocamiones disponibles. Segun la memoria del proyecto, el
-- contrato con el compañero de FoxPro aun no esta confirmado (puede que
-- prefiera un endpoint REST en vez de una vista) -- no la actives sin
-- hablarlo primero. Se quito el DEFINER=`root`@`localhost` que trae el
-- local porque ese usuario no existe en .30 (usa admin); sin DEFINER
-- explicito, MySQL usa el usuario que ejecuta el CREATE VIEW.
-- ---------------------------------------------------------------------
-- CREATE VIEW `view_caseta_tractocamiones_disponibles` AS
-- SELECT
--     caseta.casetaid, caseta.nunidad, caseta.idtransporu, caseta.transporu,
--     caseta.operador, caseta.placas, caseta.tipoop, caseta.tipomov,
--     caseta.planta, caseta.farribo
-- FROM caseta
-- WHERE caseta.tiporegistro = 'TRACTOCAMION'
--   AND (caseta.relacion IS NULL OR caseta.relacion = '')
--   AND caseta.estatus NOT IN ('SALIDA', 'CANCELADO', 'CANCELADA')
-- ORDER BY caseta.farribo DESC;


-- =====================================================================
-- NO INCLUIDAS A PROPOSITO -- existen en local pero 0 referencias en el
-- codigo actual de PMMovil.Api (Services/Controllers), asi que no se
-- generó DDL para ellas. Si alguna sí se necesita, dilo y la agrego:
--
--   pmmtraf:  caja_trazabilidad, oficinas, rev_permisos, usuariooficina
--
--   pmmtools: chatrooms, chatroomparticipants, messages,
--             messagementions, messagereactions, userpresence,
--             pendiente_seguimiento
--             *** OJO: .30 ya tiene ChatRooms, ChatRoomParticipants,
--             Messages, MessageMentions, MessageReactions, UserPresence
--             en PascalCase -- probablemente de otro sistema/prototipo
--             distinto a estas de local en minusculas. Ninguna de las
--             dos variantes la usa el API hoy. Si mas adelante se activa
--             un feature de chat, hay que decidir con cual de las dos
--             convenciones seguir para no duplicar -- no generes un
--             CREATE TABLE a ciegas aqui.
--
-- pmmtools no necesita ningun cambio: se comparó contra local y ya está
-- sincronizada (incluida la migracion 20260717_add_zebra_print_
-- preferences.sql, que ya está aplicada en .30).
-- =====================================================================
