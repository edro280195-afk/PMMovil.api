-- ============================================================================
-- PMMovil.Api - cierre de auditoria de esquema para localhost y 192.168.10.30
-- Compatibilidad: MySQL 5.7.42 (tambien funciona en MySQL 8.x/9.x)
--
-- Ejecutar conectado a 192.168.10.30 con un usuario que tenga permisos sobre
-- pmmtraf y pmmutileria. No elimina ni renombra tablas y no cambia tablas
-- legacy existentes. CREATE TABLE IF NOT EXISTS permite re-ejecutarlo.
--
-- El API actual usa las tablas de pre-registro, links y notificaciones desde
-- pmmutileria. Las copias preregistro_* que aun existan en pmmtraf son legado;
-- se dejan intactas. Si contienen datos, el bloque de copia los conserva en
-- pmmutileria solo cuando el destino esta vacio.
-- ============================================================================

SET NAMES utf8mb4;

-- ---------------------------------------------------------------------------
-- pmmtraf: los catalogos operativos ya existen en la instalacion objetivo.
-- ProveedorService usa provs y consignaprov; aqui solo se verifican al final.
-- ---------------------------------------------------------------------------
USE `pmmtraf`;

-- Tabla usada por SalidaService para autorizar la carga desde la caseta.
CREATE TABLE IF NOT EXISTS `salida_carga_qr` (
    `id`                    BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `relno`                 VARCHAR(32) NOT NULL,
    `casetaid`              VARCHAR(64) NOT NULL,
    `usuario`               VARCHAR(100) NOT NULL,
    `token`                 CHAR(32) NOT NULL,
    `estado`                VARCHAR(20) NOT NULL DEFAULT 'PENDIENTE',
    `leido_at`              DATETIME NOT NULL,
    `expira_at`             DATETIME NOT NULL,
    `consumido_at`          DATETIME NULL,
    `texto_bulto`           VARCHAR(255) NULL,
    `motivo`                VARCHAR(100) NULL,
    `ip_address`            VARCHAR(45) NOT NULL DEFAULT '',
    PRIMARY KEY (`id`),
    UNIQUE KEY `uq_salida_carga_qr_token` (`token`),
    KEY `idx_salida_carga_qr_pendiente` (`relno`, `usuario`, `estado`, `expira_at`),
    KEY `idx_salida_carga_qr_caseta` (`casetaid`, `leido_at`)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;

-- ---------------------------------------------------------------------------
-- pmmutileria: tablas propias del flujo de pre-registro y GPS
-- ---------------------------------------------------------------------------
USE `pmmutileria`;

CREATE TABLE IF NOT EXISTS `preregistro_cuentas` (
    `id`                 INT NOT NULL AUTO_INCREMENT,
    `usuario`            VARCHAR(30) NOT NULL,
    `password_hash`      VARCHAR(255) NOT NULL,
    `cidtranspor`        VARCHAR(10) NOT NULL,
    `nombre_linea`       VARCHAR(100) NOT NULL,
    `contacto`           VARCHAR(100) NULL,
    `activo`             TINYINT(1) NOT NULL DEFAULT 1,
    `fcrea`              DATETIME NOT NULL,
    `usucrea`            VARCHAR(30) NOT NULL,
    `fmodi`              DATETIME NULL,
    `usumodi`            VARCHAR(30) NULL,
    `fultimo_acceso`     DATETIME NULL,
    `intentos_fallidos`  INT NOT NULL DEFAULT 0,
    `fbloqueo`           DATETIME NULL,
    PRIMARY KEY (`id`),
    UNIQUE KEY `uq_usuario` (`usuario`),
    KEY `ix_transpor` (`cidtranspor`)
) ENGINE=InnoDB DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS `preregistro_auditoria` (
    `id`          BIGINT NOT NULL AUTO_INCREMENT,
    `fecha`       DATETIME NOT NULL,
    `evento`      VARCHAR(30) NOT NULL,
    `id_cuenta`   INT NULL,
    `usuario`     VARCHAR(30) NULL,
    `folio`       VARCHAR(24) NULL,
    `ip`          VARCHAR(45) NULL,
    `detalle`     VARCHAR(255) NULL,
    PRIMARY KEY (`id`),
    KEY `ix_fecha` (`fecha`),
    KEY `ix_cuenta` (`id_cuenta`),
    KEY `ix_evento` (`evento`)
) ENGINE=InnoDB DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS `caseta_preregistro` (
    `id`           INT NOT NULL AUTO_INCREMENT,
    `folio`        VARCHAR(24) NOT NULL,
    `token`        CHAR(32) NOT NULL,
    `id_cuenta`    INT NOT NULL,
    `nunidad`      VARCHAR(30) NOT NULL,
    `idtransporu`  VARCHAR(10) NULL,
    `transporu`    VARCHAR(100) NULL,
    `placas`        VARCHAR(25) NOT NULL,
    `operador`     VARCHAR(100) NOT NULL,
    `idbodega`     VARCHAR(5) NULL,
    `lat`          DECIMAL(9,6) NULL,
    `lon`          DECIMAL(9,6) NULL,
    `fubicacion`   DATETIME NULL,
    `eta_minutos`  INT NULL,
    `eta_km`       DECIMAL(7,2) NULL,
    `feta`         DATETIME NULL,
    `estatus`      VARCHAR(15) NOT NULL,
    `fcrea`        DATETIME NOT NULL,
    `fexpira`      DATETIME NOT NULL,
    `fpurga`       DATETIME NOT NULL,
    `fuso`         DATETIME NULL,
    `usuario_uso`  VARCHAR(30) NULL,
    `casetaid`     VARCHAR(20) NULL,
    `ip_origen`    VARCHAR(45) NULL,
    PRIMARY KEY (`id`),
    UNIQUE KEY `uq_folio` (`folio`),
    UNIQUE KEY `uq_token` (`token`),
    KEY `ix_purga` (`fpurga`),
    KEY `ix_estatus_expira` (`estatus`, `fexpira`),
    KEY `ix_cuenta` (`id_cuenta`)
) ENGINE=InnoDB DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;

-- localhost la tiene en utf8mb4_0900_ai_ci; MySQL 5.7 no reconoce esa
-- collation, por eso la replica compatible usa utf8mb4_general_ci.
CREATE TABLE IF NOT EXISTS `caseta_preregistro_ubicacion` (
    `id`               BIGINT NOT NULL AUTO_INCREMENT,
    `folio`            VARCHAR(24) NOT NULL,
    `lat`              DECIMAL(9,6) NOT NULL,
    `lon`              DECIMAL(9,6) NOT NULL,
    `fecha`            DATETIME NOT NULL,
    `precision_m`      INT NULL,
    `velocidad`        DECIMAL(6,2) NULL,
    `origen`           VARCHAR(12) NOT NULL DEFAULT 'PORTAL',
    `eta_minutos`      INT NULL,
    `eta_km`           DECIMAL(7,2) NULL,
    `distancia_km`     DECIMAL(7,2) NULL,
    `dentro_geocerca`  TINYINT(1) NOT NULL DEFAULT 0,
    `fpurga`           DATETIME NOT NULL,
    PRIMARY KEY (`id`),
    KEY `ix_folio_fecha` (`folio`, `fecha`),
    KEY `ix_purga` (`fpurga`),
    KEY `ix_geocerca` (`folio`, `dentro_geocerca`, `fecha`)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;

-- ---------------------------------------------------------------------------
-- Copia conservadora de datos viejos, si existen. No borra ni renombra la
-- fuente en pmmtraf. Las consultas se preparan dinamicamente para que el
-- script tambien pueda ejecutarse en una instalacion sin esas tablas antiguas.
-- ---------------------------------------------------------------------------
SET @src_cuentas = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'pmmtraf' AND TABLE_NAME = 'preregistro_cuentas');
SET @dst_cuentas = (SELECT COUNT(*) FROM pmmutileria.preregistro_cuentas);
SET @sql_cuentas = IF(@src_cuentas > 0 AND @dst_cuentas = 0,
    'INSERT INTO pmmutileria.preregistro_cuentas (id, usuario, password_hash, cidtranspor, nombre_linea, contacto, activo, fcrea, usucrea, fmodi, usumodi, fultimo_acceso, intentos_fallidos, fbloqueo) SELECT id, usuario, password_hash, cidtranspor, nombre_linea, contacto, activo, fcrea, usucrea, fmodi, usumodi, fultimo_acceso, intentos_fallidos, fbloqueo FROM pmmtraf.preregistro_cuentas',
    'SELECT 1');
PREPARE stmt_cuentas FROM @sql_cuentas; EXECUTE stmt_cuentas; DEALLOCATE PREPARE stmt_cuentas;

SET @src_auditoria = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'pmmtraf' AND TABLE_NAME = 'preregistro_auditoria');
SET @dst_auditoria = (SELECT COUNT(*) FROM pmmutileria.preregistro_auditoria);
SET @sql_auditoria = IF(@src_auditoria > 0 AND @dst_auditoria = 0,
    'INSERT INTO pmmutileria.preregistro_auditoria (id, fecha, evento, id_cuenta, usuario, folio, ip, detalle) SELECT id, fecha, evento, id_cuenta, usuario, folio, ip, detalle FROM pmmtraf.preregistro_auditoria',
    'SELECT 1');
PREPARE stmt_auditoria FROM @sql_auditoria; EXECUTE stmt_auditoria; DEALLOCATE PREPARE stmt_auditoria;

SET @src_caseta = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'pmmtraf' AND TABLE_NAME = 'caseta_preregistro');
SET @dst_caseta = (SELECT COUNT(*) FROM pmmutileria.caseta_preregistro);
SET @sql_caseta = IF(@src_caseta > 0 AND @dst_caseta = 0,
    'INSERT INTO pmmutileria.caseta_preregistro (id, folio, token, id_cuenta, nunidad, idtransporu, transporu, placas, operador, idbodega, lat, lon, fubicacion, eta_minutos, eta_km, feta, estatus, fcrea, fexpira, fpurga, fuso, usuario_uso, casetaid, ip_origen) SELECT id, folio, token, id_cuenta, nunidad, idtransporu, transporu, placas, operador, idbodega, lat, lon, fubicacion, eta_minutos, eta_km, feta, estatus, fcrea, fexpira, fpurga, fuso, usuario_uso, casetaid, ip_origen FROM pmmtraf.caseta_preregistro',
    'SELECT 1');
PREPARE stmt_caseta FROM @sql_caseta; EXECUTE stmt_caseta; DEALLOCATE PREPARE stmt_caseta;

-- Verificacion posterior:
SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_ROWS
FROM information_schema.TABLES
WHERE (TABLE_SCHEMA = 'pmmtraf' AND TABLE_NAME IN ('provs', 'consignaprov', 'salida_carga_qr'))
   OR (TABLE_SCHEMA = 'pmmutileria' AND TABLE_NAME IN ('preregistro_cuentas', 'preregistro_auditoria', 'caseta_preregistro', 'caseta_preregistro_ubicacion'))
ORDER BY TABLE_SCHEMA, TABLE_NAME;
