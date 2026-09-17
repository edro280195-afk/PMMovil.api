-- =====================================================================
-- Destinos operativos administrables del portal de pre-registro.
-- Base objetivo: pmmutileria / MySQL 5.7.
--
-- La tabla reemplaza gradualmente GoogleRoutes:Bodegas como fuente operativa.
-- La API conserva un fallback temporal a appsettings mientras se ejecuta esta
-- migración, pero las ediciones del panel requieren esta tabla.
-- =====================================================================

USE `pmmutileria`;

CREATE TABLE IF NOT EXISTS `preregistro_bodegas` (
    `id_bodega` VARCHAR(5) NOT NULL,
    `nombre` VARCHAR(100) NOT NULL,
    `direccion` VARCHAR(255) DEFAULT NULL,
    `lat` DECIMAL(9,6) NOT NULL,
    `lon` DECIMAL(9,6) NOT NULL,
    `geocerca_lat` DECIMAL(9,6) DEFAULT NULL,
    `geocerca_lon` DECIMAL(9,6) DEFAULT NULL,
    `geocerca_metros` INT NOT NULL DEFAULT 800,
    `google_place_id` VARCHAR(255) DEFAULT NULL,
    `referencia_entrada` VARCHAR(500) DEFAULT NULL,
    `activo` TINYINT(1) NOT NULL DEFAULT 1,
    `fmodi` DATETIME DEFAULT NULL,
    `usumodi` VARCHAR(30) DEFAULT NULL,
    PRIMARY KEY (`id_bodega`),
    KEY `ix_activo_nombre` (`activo`, `nombre`)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

INSERT IGNORE INTO `preregistro_bodegas`
    (id_bodega, nombre, direccion, lat, lon, geocerca_lat, geocerca_lon,
     geocerca_metros, activo, fmodi, usumodi)
VALUES
    ('01', 'Laredo, TX', NULL, 27.628945, -99.5324954, 27.628945, -99.5324954,
     800, 1, NOW(), 'migracion');

-- Snapshot del destino usado por cada folio. Así una mudanza no modifica la
-- ruta/geocerca de los camiones que ya estaban en tránsito.
SET @existe_destino_nombre = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'pmmutileria' AND TABLE_NAME = 'caseta_preregistro'
      AND COLUMN_NAME = 'destino_nombre'
);
SET @sql_destino_nombre = IF(@existe_destino_nombre = 0,
    'ALTER TABLE caseta_preregistro ADD COLUMN destino_nombre VARCHAR(100) DEFAULT NULL AFTER idbodega',
    'SELECT 1');
PREPARE stmt_destino_nombre FROM @sql_destino_nombre; EXECUTE stmt_destino_nombre; DEALLOCATE PREPARE stmt_destino_nombre;

SET @existe_destino_direccion = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'pmmutileria' AND TABLE_NAME = 'caseta_preregistro'
      AND COLUMN_NAME = 'destino_direccion'
);
SET @sql_destino_direccion = IF(@existe_destino_direccion = 0,
    'ALTER TABLE caseta_preregistro ADD COLUMN destino_direccion VARCHAR(255) DEFAULT NULL AFTER destino_nombre',
    'SELECT 1');
PREPARE stmt_destino_direccion FROM @sql_destino_direccion; EXECUTE stmt_destino_direccion; DEALLOCATE PREPARE stmt_destino_direccion;

SET @existe_destino_lat = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'pmmutileria' AND TABLE_NAME = 'caseta_preregistro'
      AND COLUMN_NAME = 'destino_lat'
);
SET @sql_destino_lat = IF(@existe_destino_lat = 0,
    'ALTER TABLE caseta_preregistro ADD COLUMN destino_lat DECIMAL(9,6) DEFAULT NULL AFTER destino_direccion',
    'SELECT 1');
PREPARE stmt_destino_lat FROM @sql_destino_lat; EXECUTE stmt_destino_lat; DEALLOCATE PREPARE stmt_destino_lat;

SET @existe_destino_lon = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'pmmutileria' AND TABLE_NAME = 'caseta_preregistro'
      AND COLUMN_NAME = 'destino_lon'
);
SET @sql_destino_lon = IF(@existe_destino_lon = 0,
    'ALTER TABLE caseta_preregistro ADD COLUMN destino_lon DECIMAL(9,6) DEFAULT NULL AFTER destino_lat',
    'SELECT 1');
PREPARE stmt_destino_lon FROM @sql_destino_lon; EXECUTE stmt_destino_lon; DEALLOCATE PREPARE stmt_destino_lon;

SET @existe_destino_geolat = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'pmmutileria' AND TABLE_NAME = 'caseta_preregistro'
      AND COLUMN_NAME = 'destino_geocerca_lat'
);
SET @sql_destino_geolat = IF(@existe_destino_geolat = 0,
    'ALTER TABLE caseta_preregistro ADD COLUMN destino_geocerca_lat DECIMAL(9,6) DEFAULT NULL AFTER destino_lon',
    'SELECT 1');
PREPARE stmt_destino_geolat FROM @sql_destino_geolat; EXECUTE stmt_destino_geolat; DEALLOCATE PREPARE stmt_destino_geolat;

SET @existe_destino_geolon = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'pmmutileria' AND TABLE_NAME = 'caseta_preregistro'
      AND COLUMN_NAME = 'destino_geocerca_lon'
);
SET @sql_destino_geolon = IF(@existe_destino_geolon = 0,
    'ALTER TABLE caseta_preregistro ADD COLUMN destino_geocerca_lon DECIMAL(9,6) DEFAULT NULL AFTER destino_geocerca_lat',
    'SELECT 1');
PREPARE stmt_destino_geolon FROM @sql_destino_geolon; EXECUTE stmt_destino_geolon; DEALLOCATE PREPARE stmt_destino_geolon;

SET @existe_destino_radio = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'pmmutileria' AND TABLE_NAME = 'caseta_preregistro'
      AND COLUMN_NAME = 'destino_geocerca_metros'
);
SET @sql_destino_radio = IF(@existe_destino_radio = 0,
    'ALTER TABLE caseta_preregistro ADD COLUMN destino_geocerca_metros INT DEFAULT NULL AFTER destino_geocerca_lon',
    'SELECT 1');
PREPARE stmt_destino_radio FROM @sql_destino_radio; EXECUTE stmt_destino_radio; DEALLOCATE PREPARE stmt_destino_radio;

SET @existe_destino_place = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'pmmutileria' AND TABLE_NAME = 'caseta_preregistro'
      AND COLUMN_NAME = 'destino_google_place_id'
);
SET @sql_destino_place = IF(@existe_destino_place = 0,
    'ALTER TABLE caseta_preregistro ADD COLUMN destino_google_place_id VARCHAR(255) DEFAULT NULL AFTER destino_geocerca_metros',
    'SELECT 1');
PREPARE stmt_destino_place FROM @sql_destino_place; EXECUTE stmt_destino_place; DEALLOCATE PREPARE stmt_destino_place;

SET @existe_destino_ref = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'pmmutileria' AND TABLE_NAME = 'caseta_preregistro'
      AND COLUMN_NAME = 'destino_referencia_entrada'
);
SET @sql_destino_ref = IF(@existe_destino_ref = 0,
    'ALTER TABLE caseta_preregistro ADD COLUMN destino_referencia_entrada VARCHAR(500) DEFAULT NULL AFTER destino_google_place_id',
    'SELECT 1');
PREPARE stmt_destino_ref FROM @sql_destino_ref; EXECUTE stmt_destino_ref; DEALLOCATE PREPARE stmt_destino_ref;
