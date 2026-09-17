-- Ejecutar una sola vez en pmmtools antes de publicar esta version del API.
-- El perfil conserva los ultimos ajustes confirmados en la Zebra; no son defaults
-- que la app aplique automaticamente al seleccionarla.
ALTER TABLE pmmovil_zebra_printers
    ADD COLUMN print_darkness DECIMAL(4,1) NULL AFTER write_timeout_ms,
    ADD COLUMN print_speed_ips DECIMAL(3,1) NULL AFTER print_darkness,
    ADD COLUMN print_method VARCHAR(32) NULL AFTER print_speed_ips,
    ADD COLUMN media_type VARCHAR(32) NULL AFTER print_method,
    ADD COLUMN label_top SMALLINT NULL AFTER media_type,
    ADD COLUMN left_position INT NULL AFTER label_top;
