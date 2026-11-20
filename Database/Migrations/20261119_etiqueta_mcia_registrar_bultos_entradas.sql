-- ============================================================================================
-- Registra en etiqueta_mcia los bultos de las entradas que la app guardo SIN etiquetas
-- ============================================================================================
-- Contexto (19-nov-2026): al generar una entrada, el API guardaba traf, traf3 y trafdet pero nunca las
-- filas de etiqueta_mcia (una por bulto). Sin esas filas "Cargar mercancia" no encuentra los bultos y la
-- relacion no se puede cargar. El API ya las registra al guardar las entradas NUEVAS
-- (EntradaEtiquetas.RegistrarAsync, dentro de la misma transaccion del guardado); este script cubre las
-- que se guardaron ANTES de publicar ese cambio.
--
-- Como las escribe el legacy (medido con SELECT en pmmtraf el 19-nov-2026) y como las escribe este script:
--   creferencia      la referencia de la entrada
--   nlineno          numero del bulto, de 1 a total (NO es la linea de trafdet: 3+4+5+4 = 16 filas, del 1 al 16)
--   total            bultos de la entrada = SUM(trafdet.ncantidad) redondeado, minimo 1 (igual que la app al imprimir)
--   textoetiqueta    referencia + ' ' + n + '-' + total   (ej. '01C263619200000 1-21'; es el texto del QR de cada etiqueta)
--   usucrea/usumodi  usuario que registro la entrada (traf.usucreatrafico, maximo 15 caracteres)
--   fcrea/fmodi      el momento en que se corre el script
--   relacion, estatus, historial, usucarga, usu_inv, desblq_etiq = ''      reimpresa = 0
--   time_scan, time_inv   no se nombran: quedan en su valor por omision (0000-00-00 00:00:00), como las del legacy
-- La etiqueta de CARPETA (el QR de la referencia sola) no lleva fila.
--
-- NO se registran etiquetas en los manejos PIPA y EXPO, porque el legacy no lo hace: PIPA 0 de 126 entradas con
-- detalle del 15-sep al 18-nov; EXPO 1 de 38 (la 01C263619590000 paso a EXPO el 19-nov a las 18:01 y su etiqueta
-- ya no existe). El script las salta solo, aunque esten en @refs: el PREVIEW A las marca como OMITE.
--
-- Es IDEMPOTENTE: solo inserta los bultos que no tengan fila (correrlo dos veces no duplica nada).
-- Compatible con MySQL 5.7 (servidor 30): sin CTE ni funciones de ventana.
--
-- Entradas de @refs (estado al 19-nov, 18:15):
--   YA CORRIDAS a las 17:53 (si se vuelve a correr no agregan nada; siguen en la lista para verlas en los previews):
--     01C263619290000     1 bulto    (gsolis)
--     01C263619200000    21 bultos   (hlanderos)
--     01C263619300000    20 bultos   (hlanderos)
--     01C263619580000     7 bultos   (jmedina; era manejo D: el legacy la creo con UNA sola fila, '1-7', y el script agrego 2-7 a 7-7;
--                                     a las 18:06 Gladys la paso a EXPO: conserva sus 7 filas y por eso el PREVIEW A la marca OMITE)
--   NUEVAS, sin ninguna fila en etiqueta_mcia => 54 filas en total (33 + 12 + 3 + 2 + 1 + 1 + 1 + 1):
--     01C263619510000    33 bultos   (gsolis)
--     01C263619540000    12 bultos   (hsalazar)
--     01C263619500000     3 bultos   (memo; manejo P)
--     01C263619530000     2 bultos   (bhsanchez)
--     01C263619490000     1 bulto    (hlanderos)
--     01C263619520000     1 bulto    (gsolis; manejo C)
--     01C263619550000     1 bulto    (rnino)
--     01C263619560000     1 bulto    (frodriguez)
--   OMITIDAS por regla (no se les registra nada):
--     01C263619590000     1 bulto    (jmedina; ahora manejo EXPO y su etiqueta '1-1' ya no existe)
--     01C263619070000     1 bulto    (gladys; manejo PIPA)
--     01C263619080000     1 bulto    (gladys; manejo PIPA)
--
-- Para agregar otra entrada basta anadir su referencia a @refs (separadas por coma, sin espacios).

SET @refs = '01C263619290000,01C263619200000,01C263619300000,01C263619580000,01C263619510000,01C263619540000,01C263619500000,01C263619530000,01C263619490000,01C263619520000,01C263619550000,01C263619560000,01C263619590000,01C263619070000,01C263619080000';

-- ============================================================
-- 1) PREVIEW A -- una fila por entrada de @refs (deben salir 15: 8 REGISTRA, 3 YA COMPLETA y 4 OMITE)
-- ============================================================
SELECT r.creferencia,
       r.manejo,
       r.usuario,
       r.bultos,
       r.ya_registradas,
       CASE WHEN r.manejo IN ('PIPA', 'EXPO') THEN 'OMITE (el legacy no etiqueta PIPA ni EXPO)'
            WHEN r.ya_registradas >= r.bultos THEN 'YA COMPLETA'
            ELSE 'REGISTRA' END AS accion
FROM (
    SELECT e.creferencia,
           e.manejo,
           e.usuario,
           e.total AS bultos,
           (SELECT COUNT(*) FROM etiqueta_mcia x WHERE x.creferencia = e.creferencia) AS ya_registradas
    FROM (
        SELECT t.creferencia,
               COALESCE(t.cidmanejo, '')                               AS manejo,
               LEFT(t.usucreatrafico, 15)                              AS usuario,
               GREATEST(1, CAST(ROUND(SUM(d.ncantidad)) AS UNSIGNED))  AS total
        FROM traf t
        JOIN trafdet d ON d.creferencia = t.creferencia
        WHERE FIND_IN_SET(t.creferencia, @refs) > 0
        GROUP BY t.creferencia, t.cidmanejo, t.usucreatrafico
    ) AS e
) AS r
ORDER BY r.creferencia;

-- ============================================================
-- 2) PREVIEW B -- las filas EXACTAS que se van a insertar (deben ser 54)
-- ============================================================
SELECT e.creferencia,
       n.n                                              AS nlineno,
       e.total,
       CONCAT(e.creferencia, ' ', n.n, '-', e.total)    AS textoetiqueta,
       e.usuario                                        AS usucrea
FROM (
    SELECT t.creferencia,
           LEFT(t.usucreatrafico, 15)                              AS usuario,
           GREATEST(1, CAST(ROUND(SUM(d.ncantidad)) AS UNSIGNED))  AS total
    FROM traf t
    JOIN trafdet d ON d.creferencia = t.creferencia
    WHERE FIND_IN_SET(t.creferencia, @refs) > 0
      AND COALESCE(t.cidmanejo, '') NOT IN ('PIPA', 'EXPO')
    GROUP BY t.creferencia, t.usucreatrafico
) AS e
JOIN (
    SELECT a.d + 10 * b.d + 100 * c.d + 1000 * f.d + 1 AS n
    FROM (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) AS a
    CROSS JOIN (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) AS b
    CROSS JOIN (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) AS c
    CROSS JOIN (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) AS f
) AS n ON n.n <= e.total
LEFT JOIN etiqueta_mcia ya
       ON ya.creferencia = e.creferencia
      AND ya.textoetiqueta = CONCAT(e.creferencia, ' ', n.n, '-', e.total)
WHERE ya.id_etiqueta_mcia IS NULL
ORDER BY e.creferencia, n.n;

-- ============================================================
-- 3) INSERT -- correr solo despues de revisar los dos previews de arriba
-- ============================================================
INSERT INTO etiqueta_mcia
    (creferencia, nlineno, total, textoetiqueta, usucrea, fcrea, usumodi, fmodi,
     relacion, estatus, historial, usucarga, usu_inv, reimpresa, desblq_etiq)
SELECT e.creferencia,
       n.n,
       e.total,
       CONCAT(e.creferencia, ' ', n.n, '-', e.total),
       e.usuario, NOW(), e.usuario, NOW(),
       '', '', '', '', '', 0, ''
FROM (
    SELECT t.creferencia,
           LEFT(t.usucreatrafico, 15)                              AS usuario,
           GREATEST(1, CAST(ROUND(SUM(d.ncantidad)) AS UNSIGNED))  AS total
    FROM traf t
    JOIN trafdet d ON d.creferencia = t.creferencia
    WHERE FIND_IN_SET(t.creferencia, @refs) > 0
      AND COALESCE(t.cidmanejo, '') NOT IN ('PIPA', 'EXPO')
    GROUP BY t.creferencia, t.usucreatrafico
) AS e
JOIN (
    SELECT a.d + 10 * b.d + 100 * c.d + 1000 * f.d + 1 AS n
    FROM (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) AS a
    CROSS JOIN (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) AS b
    CROSS JOIN (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) AS c
    CROSS JOIN (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) AS f
) AS n ON n.n <= e.total
LEFT JOIN etiqueta_mcia ya
       ON ya.creferencia = e.creferencia
      AND ya.textoetiqueta = CONCAT(e.creferencia, ' ', n.n, '-', e.total)
WHERE ya.id_etiqueta_mcia IS NULL
ORDER BY e.creferencia, n.n;

-- ============================================================
-- 4) VERIFICACION -- correr despues del INSERT (distintas = filas: ningun texto repetido)
--    Esperado (12 filas): 01C263619200000 -> 21, 01C263619290000 -> 1, 01C263619300000 -> 20, 01C263619580000 -> 7,
--    01C263619510000 -> 33, 01C263619540000 -> 12, 01C263619500000 -> 3, 01C263619530000 -> 2,
--    01C263619490000 -> 1, 01C263619520000 -> 1, 01C263619550000 -> 1, 01C263619560000 -> 1.
--    Las 3 omitidas (01C263619590000, 01C263619070000, 01C263619080000) no deben aparecer aqui.
-- ============================================================
SELECT x.creferencia,
       COUNT(*)                         AS filas,
       COUNT(DISTINCT x.textoetiqueta)  AS distintas,
       MIN(x.nlineno)                   AS desde,
       MAX(x.nlineno)                   AS hasta,
       MAX(x.total)                     AS total,
       MAX(x.usucrea)                   AS usucrea,
       MAX(x.fcrea)                     AS fcrea
FROM etiqueta_mcia x
WHERE FIND_IN_SET(x.creferencia, @refs) > 0
GROUP BY x.creferencia
ORDER BY x.creferencia;
