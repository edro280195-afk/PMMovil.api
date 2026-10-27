-- ============================================================================
--  Grupos de cliente para la revisión por códigos de barras
--  Base: pmmutileria   (la misma donde ya viven revisiones_barcode_sesion y
--                       revisiones_barcode_lectura, del modo por códigos)
--
--  Para qué: decidir, al abrir una entrada, si el modo de lectura de etiquetas
--  se ofrece o no. Hoy se ofrece siempre, y en un cliente que no imprime
--  códigos el revisador abre la cámara para nada.
--
--  Por qué un GRUPO y no el cliente suelto: una misma empresa aparece con
--  varios cclienteid. Freudenberg son cinco (CL7481, CL7480, CL8400, CL8292,
--  CL7482), con el mismo papel pegado a la caja y comportamientos muy
--  distintos: de 2.9 a 306 partidas por entrada. Si la configuración colgara
--  de cclienteid habría que repetirla cinco veces y la primera entrada del id
--  que faltara llegaría sin modo.
--
--  Qué NO guarda esto: cómo se lee cada etiqueta. Eso vive en el código de la
--  app, en el perfil que nombra `clave`. La tabla decide QUIÉNES y SI está
--  encendido; el código sabe CÓMO. Así se agrega un cliente sin publicar la
--  app, y un formato nuevo no pide migración.
--
--  Colación, a propósito: `cidcte` se declara en latin1_swedish_ci porque así
--  están pmmtraf.clientes.cidcte y pmmtraf.revdet.cclienteid. Con utf8mb4
--  cualquier JOIN entre bases moriría con "Illegal mix of collations". Las
--  demás columnas sí van en utf8mb4 para que los nombres lleven acentos.
--
--  Compatible con MySQL 5.7 (servidor 30): las tablas usan utf8mb4_general_ci y
--  NO utf8mb4_0900_ai_ci, que solo existe desde MySQL 8 y en el 30 da el error
--  1273 «Unknown collation». Es la misma que usan las demás tablas que se
--  replicaron al 30 (ver 20260907_api_schema_audit_30_mysql57.sql).
-- ============================================================================

USE pmmutileria;


-- ---------------------------------------------------------------------------
--  El grupo. `clave` es la que amarra con el perfil de lectura del código, así
--  que se escribe en mayúsculas y sin espacios, y no se cambia a la ligera:
--  renombrarla apaga el modo hasta que la app se publique de nuevo.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS etiqueta_grupo_cliente (
    id_grupo     INT           NOT NULL AUTO_INCREMENT PRIMARY KEY,

    clave        VARCHAR(30)   NOT NULL,                  -- 'FREUDENBERG'
    nombre       VARCHAR(120)  NOT NULL,                  -- cómo se ve en el panel
    activo       TINYINT(1)    NOT NULL DEFAULT 1,        -- apagarlo quita el modo sin borrar nada
    notas        VARCHAR(400)      NULL,

    usucrea      VARCHAR(80)       NULL,
    fcrea        DATETIME          NULL,
    usumodi      VARCHAR(80)       NULL,
    fmodi        DATETIME          NULL,

    UNIQUE KEY uq_etiqueta_grupo_clave (clave)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_general_ci;


-- ---------------------------------------------------------------------------
--  Los cclienteid que pertenecen al grupo.
--
--  `cidcte` es único en toda la tabla, no solo dentro del grupo: un cliente
--  pertenece a un grupo y nada más. Si pudiera estar en dos, al abrir una
--  entrada habría que decidir cuál perfil gana, y esa decisión no tiene una
--  respuesta buena.
--
--  Sin llave foránea contra pmmtraf.clientes: son bases distintas y MySQL no
--  admite FK entre esquemas de servidores separados en producción. La validez
--  del cliente la comprueba el servicio al dar de alta.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS etiqueta_grupo_cliente_miembro (
    id_miembro   INT           NOT NULL AUTO_INCREMENT PRIMARY KEY,

    id_grupo     INT           NOT NULL,
    cidcte       VARCHAR(10)   CHARACTER SET latin1 COLLATE latin1_swedish_ci NOT NULL,

    usucrea      VARCHAR(80)       NULL,
    fcrea        DATETIME          NULL,

    UNIQUE KEY uq_etiqueta_miembro_cliente (cidcte),
    KEY ix_etiqueta_miembro_grupo (id_grupo),

    CONSTRAINT fk_etiqueta_miembro_grupo
        FOREIGN KEY (id_grupo) REFERENCES etiqueta_grupo_cliente (id_grupo)
        ON DELETE CASCADE
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_general_ci;


-- ---------------------------------------------------------------------------
--  Semilla: Freudenberg, el único grupo con etiquetas ya estudiadas.
--
--  Los cinco ids salieron de contar las entradas del último año en revdet:
--    CL7481  1 687 entradas   20 271 partidas    12 por entrada
--    CL7480    772 entradas    4 355 partidas   5.6 por entrada
--    CL8400    113 entradas      325 partidas   2.9 por entrada
--    CL8292     59 entradas    9 888 partidas   167 por entrada
--    CL7482     23 entradas    7 045 partidas   306 por entrada
--
--  Se puede correr dos veces sin duplicar.
-- ---------------------------------------------------------------------------
INSERT INTO etiqueta_grupo_cliente (clave, nombre, activo, notas, usucrea, fcrea)
SELECT 'FREUDENBERG',
       'Freudenberg-NOK Sealing Technologies',
       1,
       'Cuatro formatos conocidos: PDF417 de compuesto quimico, caja de Mexico, bolsa de Taiwan y envio VDA de NOK.',
       'sistemas',
       NOW()
WHERE NOT EXISTS (
    SELECT 1 FROM etiqueta_grupo_cliente WHERE clave = 'FREUDENBERG'
);

INSERT INTO etiqueta_grupo_cliente_miembro (id_grupo, cidcte, usucrea, fcrea)
SELECT g.id_grupo, c.cidcte, 'sistemas', NOW()
FROM etiqueta_grupo_cliente g
JOIN (
    SELECT 'CL7481' AS cidcte UNION ALL
    SELECT 'CL7480' UNION ALL
    SELECT 'CL8400' UNION ALL
    SELECT 'CL8292' UNION ALL
    SELECT 'CL7482'
) c
WHERE g.clave = 'FREUDENBERG'
  AND NOT EXISTS (
      SELECT 1 FROM etiqueta_grupo_cliente_miembro m WHERE m.cidcte = c.cidcte
  );


-- ---------------------------------------------------------------------------
--  Comprobación. Debe devolver un grupo con 5 miembros.
-- ---------------------------------------------------------------------------
SELECT g.id_grupo, g.clave, g.nombre, g.activo, COUNT(m.id_miembro) AS miembros
FROM etiqueta_grupo_cliente g
LEFT JOIN etiqueta_grupo_cliente_miembro m ON m.id_grupo = g.id_grupo
GROUP BY g.id_grupo, g.clave, g.nombre, g.activo;
