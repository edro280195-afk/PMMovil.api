-- Migracion para almacenar centralizadamente las plantillas de etiquetas Zebra (Entrada y Caseta)
-- Permite que todos los dispositivos moviles y tabletas compartan la misma plantilla.

CREATE TABLE IF NOT EXISTS `pmmovil_zebra_label_layouts` (
    `id` INT NOT NULL AUTO_INCREMENT,
    `layout_type` VARCHAR(64) NOT NULL,
    `layout_json` LONGTEXT NOT NULL,
    `version` INT NOT NULL DEFAULT 1,
    `updated_by` VARCHAR(80) NOT NULL DEFAULT '',
    `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (`id`),
    UNIQUE KEY `ux_pmmovil_zebra_label_layouts_type` (`layout_type`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
