-- Migra el token de autorización de caseta (salida_carga_qr, usado por
-- SalidaService para autorizar el escaneo de bultos en la caseta de salida)
-- de texto plano a SHA-256, mismo criterio que ya se aplicó a los enlaces
-- públicos de Seguimiento de entrada/revisión y Ver reporte (ver
-- 20260928_hash_tokens_enlaces_publicos.sql) y a monitor_tv_links. Si la BD
-- de pmmtraf alguna vez se lee sin autorización, una autorización de caseta
-- vigente ya no queda usable de inmediato con solo leer la tabla.
--
-- Esta migración NO toca usuarios ni contraseñas de MySQL -- solo agrega una
-- columna y la llena a partir del token que ya existe en cada fila.
--
-- A diferencia de los enlaces públicos, esta tabla vive en pmmtraf, NO en
-- pmmutileria (ver BaseRepository.CreateConnection/SalidaService).
--
-- Ejecutar una sola vez en pmmtraf, ANTES de publicar la versión del API que
-- ya busca por token_hash (SalidaService.ValidateCasetaQrAuthorizationAsync).
-- Las autorizaciones PENDIENTE que sigan vigentes al momento de correr esto
-- se migran solas (se calcula su hash a partir del token existente); con una
-- ventana de vigencia de solo 10 segundos, en la práctica nunca hay ninguna
-- vigente para cuando esto se ejecuta.
--
-- La columna `token` se queda en la tabla (el código nuevo solo escribe ahí
-- los primeros 8 caracteres, como "hint" para soporte -- mismo criterio que
-- entrada_links_compartidos/revision_links_compartidos/
-- reporte_links_temporales/monitor_tv_links) por si hace falta un rollback
-- rápido del código sin perder autorizaciones vigentes.

USE pmmtraf;

ALTER TABLE salida_carga_qr
    ADD COLUMN token_hash CHAR(64) NULL AFTER token;

UPDATE salida_carga_qr
   SET token_hash = SHA2(token, 256)
 WHERE token_hash IS NULL;

ALTER TABLE salida_carga_qr
    MODIFY token_hash CHAR(64) NOT NULL,
    ADD UNIQUE KEY uq_salida_carga_qr_token_hash (token_hash);

-- A diferencia de los enlaces públicos (donde el UNIQUE KEY original sobre
-- `token` se dejó intacto), aquí SÍ se elimina: esta tabla inserta una fila
-- por cada escaneo de QR de caseta -- no una vez por cada clic en
-- "Compartir" -- así que acumula muchas más filas con el tiempo. Si se
-- dejara el UNIQUE KEY sobre un hint de solo 8 caracteres, un choque de
-- cumpleaños entre hints eventualmente tronaría el INSERT (error de llave
-- duplicada) y bloquearía la autorización de caseta en producción.
ALTER TABLE salida_carga_qr
    DROP INDEX uq_salida_carga_qr_token;
