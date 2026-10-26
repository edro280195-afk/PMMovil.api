-- Enlaces públicos revocables para la pantalla TV del monitor operativo.
-- Ejecutar en la base pmmutileria. El API también usa CREATE TABLE IF NOT EXISTS
-- como protección para instalaciones que aún no hayan corrido este script.
USE pmmutileria;

CREATE TABLE IF NOT EXISTS monitor_tv_links (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    token_hash CHAR(64) NOT NULL,
    token_hint CHAR(8) NOT NULL,
    token_encrypted TEXT NULL,
    nombre VARCHAR(100) NOT NULL,
    tipo_pantalla VARCHAR(30) NOT NULL DEFAULT 'OPERACION_NLD',
    usucrea VARCHAR(100) NOT NULL,
    fcrea DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    fexpira DATETIME NULL,
    revocado TINYINT(1) NOT NULL DEFAULT 0,
    frevoca DATETIME NULL,
    usu_revoca VARCHAR(100) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_monitor_tv_links_hash (token_hash),
    KEY ix_monitor_tv_links_status (revocado, fexpira)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
