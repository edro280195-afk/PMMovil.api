-- Migra los tokens de los enlaces públicos (Seguimiento de entrada,
-- Seguimiento de revisión, Ver reporte) de texto plano a SHA-256, igual que
-- ya se hace en monitor_tv_links (ver 20260918_add_monitor_tv_links.sql). Si
-- la BD de pmmutileria alguna vez se lee sin autorización, un enlace vigente
-- ya no queda usable de inmediato con solo leer la tabla.
--
-- Esta migración NO toca usuarios ni contraseñas de MySQL -- solo agrega una
-- columna y la llena a partir del token que ya existe en cada fila.
--
-- Ejecutar una sola vez en pmmutileria, ANTES de publicar la versión del API
-- que ya busca por token_hash (EntradaShareService/RevisionShareService/
-- ReportShareService). Los enlaces ya generados y aún vigentes se migran
-- solos (se calcula su hash a partir del token existente); nadie tiene que
-- volver a generarlos.
--
-- La columna `token` se queda en la tabla (el código nuevo solo escribe ahí
-- los primeros 8 caracteres, como "hint" para soporte -- mismo criterio que
-- monitor_tv_links/token_hint) por si hace falta un rollback rápido del
-- código sin perder enlaces vigentes. Se puede acortar/renombrar en una
-- migración posterior una vez confirmado que todo quedó funcionando solo con
-- token_hash.

USE pmmutileria;

-- entrada_links_compartidos -------------------------------------------------
ALTER TABLE entrada_links_compartidos
    ADD COLUMN token_hash CHAR(64) NULL AFTER token;

UPDATE entrada_links_compartidos
   SET token_hash = SHA2(token, 256)
 WHERE token_hash IS NULL;

ALTER TABLE entrada_links_compartidos
    MODIFY token_hash CHAR(64) NOT NULL,
    ADD UNIQUE KEY uq_entrada_links_token_hash (token_hash);

-- revision_links_compartidos -------------------------------------------------
ALTER TABLE revision_links_compartidos
    ADD COLUMN token_hash CHAR(64) NULL AFTER token;

UPDATE revision_links_compartidos
   SET token_hash = SHA2(token, 256)
 WHERE token_hash IS NULL;

ALTER TABLE revision_links_compartidos
    MODIFY token_hash CHAR(64) NOT NULL,
    ADD UNIQUE KEY uq_revision_links_token_hash (token_hash);

-- reporte_links_temporales ---------------------------------------------------
ALTER TABLE reporte_links_temporales
    ADD COLUMN token_hash CHAR(64) NULL AFTER token;

UPDATE reporte_links_temporales
   SET token_hash = SHA2(token, 256)
 WHERE token_hash IS NULL;

ALTER TABLE reporte_links_temporales
    MODIFY token_hash CHAR(64) NOT NULL,
    ADD UNIQUE KEY uq_reporte_links_token_hash (token_hash);
