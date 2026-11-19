-- Enlaces de las ENTRADAS DE BODEGA que otro sistema (PMM / Visual FoxPro) toma de aquí para mandarlos.
--
-- Qué resuelve: al registrar una entrada en PMMovil el API crea, una sola vez por entrada, el enlace público de
-- «Seguimiento de entrada» (fotos y datos, sin sesión) con vencimiento a 3 MESES y lo deja en esta tabla, ya armado.
-- Quien manda la notificación al cliente (el sistema PMM) solo lee la fila de su referencia: no necesita hablar con el API.
--
-- Por qué una tabla aparte y no entrada_links_compartidos: esa guarda únicamente el HASH del token (a propósito, ver
-- 20260928_hash_tokens_enlaces_publicos.sql), así que de ahí NO se puede armar la URL. Aquí va el token en claro y la URL
-- completa; la página pública sigue validando contra entrada_links_compartidos (id_link apunta a esa fila), de modo que
-- el vencimiento y el candado «revocado» siguen mandando allá.
--
-- Cómo lo usa el otro sistema (una fila por entrada, la llave es creferencia = traf.creferencia):
--   SELECT url, fexpira FROM pmmutileria.entrada_enlaces_envio
--    WHERE creferencia = '<referencia>' AND fexpira > NOW();
--   -- (si prefiere armarlo: CONCAT('<base del API>/api/publico/entrada/', token))
--   -- al mandarlo, para llevar la cuenta:
--   UPDATE pmmutileria.entrada_enlaces_envio
--      SET enviado = 1, fenvio = NOW(), usuenvio = '<quien>' WHERE creferencia = '<referencia>';
--
-- Para anular un enlace antes de tiempo (la página lo deja de abrir al instante):
--   UPDATE pmmutileria.entrada_links_compartidos SET revocado = 1
--    WHERE id_link = (SELECT id_link FROM pmmutileria.entrada_enlaces_envio WHERE creferencia = '<referencia>');
--
-- Solo entradas registradas DESPUÉS de publicar el API con este cambio tienen fila: las anteriores no se rellenan.
--
-- Compatible con MySQL 5.7 (servidor 30) y 9.x (local): sin sintaxis nueva. Se declara latin1 como traf/notificacionespmm
-- para que el JOIN por referencia desde el sistema PMM no choque de colación (todo lo que se guarda aquí es ASCII).
--
-- Ejecutar una sola vez en pmmutileria (servidor 30), ANTES de publicar el API. Si el API se publica primero, el registro
-- de entradas sigue funcionando (el enlace es un extra y no tumba el guardado), pero no se crea el enlace hasta que exista
-- la tabla; en el registro del API queda un aviso que lo dice.

USE pmmutileria;

CREATE TABLE IF NOT EXISTS entrada_enlaces_envio (
    id_envio    INT          NOT NULL AUTO_INCREMENT,
    creferencia VARCHAR(30)  NOT NULL COMMENT 'traf.creferencia de la entrada',
    id_link     INT          NOT NULL COMMENT 'entrada_links_compartidos.id_link: el token que valida la página pública',
    token       CHAR(32)     NOT NULL COMMENT 'token EN CLARO: sirve para armar la URL',
    url         VARCHAR(300) NOT NULL COMMENT 'URL completa del enlace, lista para mandar',
    fcrea       DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    usucrea     VARCHAR(30)  NOT NULL COMMENT 'usuario de PMMovil que registró la entrada',
    fexpira     DATETIME     NOT NULL COMMENT 'vencimiento: 3 meses después de fcrea',
    enviado     TINYINT(1)   NOT NULL DEFAULT 0 COMMENT 'lo marca el otro sistema al mandarlo',
    fenvio      DATETIME     NULL,
    usuenvio    VARCHAR(30)  NULL,
    PRIMARY KEY (id_envio),
    UNIQUE KEY uq_entrada_envio_cref (creferencia),
    KEY ix_entrada_envio_pendiente (enviado, fcrea),
    KEY ix_entrada_envio_expira (fexpira)
) ENGINE=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

-- Comprobación: debe listar las 11 columnas.
SELECT column_name, column_type, is_nullable, column_key
  FROM information_schema.columns
 WHERE table_schema = 'pmmutileria' AND table_name = 'entrada_enlaces_envio'
 ORDER BY ordinal_position;
