-- ============================================================================
--  FASE 0 -- Elegir el umbral y el margen del acceso facial CON DATOS
--
--  Base: reportes   (donde vive bio_lab_prueba)
--  Requiere MySQL 8 (usa CTE recursivo). Si el servidor fuera 5.7, avisa y
--  cambio el generador de umbrales por una tabla temporal.
--
--  ESTE SCRIPT SOLO LEE. No crea, no altera y no borra nada.
--
--  Qué contesta:
--    1. Qué tan grande y qué tan usable es la muestra que ya se midió.
--    2. Para cada umbral candidato, cuántos genuinos se rechazarían (FRR)
--       y cuántos impostores pasarían (FAR).
--    3. Lo mismo, pero exigiendo además un margen contra el segundo lugar.
--    4. Dónde se cae el motor: por luz, por distancia y por ángulo.
--
--  Definiciones que usa todo el script:
--    - Se descartan los intentos SIN_ROSTRO: son fallas de DETECCIÓN, no de
--      reconocimiento, y meterlas inflaría el FRR con algo que se arregla
--      acercándose a la cámara.
--    - GENUINO  = el motor puso primero a la persona que el operador declaró.
--                 Aceptarlo es correcto; rechazarlo es un falso rechazo.
--    - IMPOSTOR = el motor puso primero a OTRA persona. Aceptarlo es un falso
--                 positivo -- el error que de verdad importa.
-- ============================================================================

USE reportes;


-- ---------------------------------------------------------------------------
--  1. ¿Con cuántos datos estamos decidiendo?
--
--  Si "usables" sale por debajo de ~200, o si "impostores" sale por debajo de
--  ~50, el umbral que salga de aquí es una corazonada con decimales. En ese
--  caso hay que medir más antes de seguir -- sobre todo intentos de impostor,
--  que son los caros de conseguir y los que fijan el techo de seguridad.
-- ---------------------------------------------------------------------------
SELECT
    COUNT(*)                                                          AS intentos_totales,
    SUM(veredicto = 'SIN_ROSTRO')                                     AS sin_rostro,
    SUM(sujeto_esperado_id IS NULL)                                   AS sin_verdad_declarada,
    SUM(veredicto <> 'SIN_ROSTRO'
        AND sujeto_esperado_id IS NOT NULL
        AND sim_top1 IS NOT NULL)                                     AS usables,
    SUM(veredicto <> 'SIN_ROSTRO'
        AND sujeto_esperado_id IS NOT NULL
        AND sim_top1 IS NOT NULL
        AND sujeto_top1_id = sujeto_esperado_id)                      AS genuinos,
    SUM(veredicto <> 'SIN_ROSTRO'
        AND sujeto_esperado_id IS NOT NULL
        AND sim_top1 IS NOT NULL
        AND sujeto_top1_id <> sujeto_esperado_id)                     AS impostores,
    COUNT(DISTINCT sujeto_esperado_id)                                AS personas_distintas,
    MIN(created_at)                                                   AS desde,
    MAX(created_at)                                                   AS hasta
FROM bio_lab_prueba;


-- ---------------------------------------------------------------------------
--  2. Barrido de umbral, sin exigir margen.
--
--  Cómo leerlo: busca la fila donde falsos_positivos sea 0 y quédate con la
--  que tenga el menor umbral de todas ellas -- ese es el punto donde la
--  seguridad ya está y todavía no estás castigando a la gente de más.
--  Después súbele un escalón como colchón, porque la muestra es chica.
-- ---------------------------------------------------------------------------
WITH RECURSIVE pasos (i) AS (
    SELECT 0
    UNION ALL
    SELECT i + 1 FROM pasos WHERE i < 40          -- 0.25 .. 0.65 de 0.01 en 0.01
),
umbrales AS (
    SELECT ROUND(0.25 + i * 0.01, 2) AS umbral FROM pasos
),
base AS (
    SELECT
        sim_top1,
        (sujeto_top1_id = sujeto_esperado_id) AS es_genuino
    FROM bio_lab_prueba
    WHERE veredicto <> 'SIN_ROSTRO'
      AND sujeto_esperado_id IS NOT NULL
      AND sujeto_top1_id     IS NOT NULL
      AND sim_top1           IS NOT NULL
)
SELECT
    u.umbral,
    SUM(b.es_genuino = 1)                                   AS genuinos,
    SUM(b.es_genuino = 1 AND b.sim_top1 <  u.umbral)        AS falsos_rechazos,
    ROUND(100.0 * SUM(b.es_genuino = 1 AND b.sim_top1 < u.umbral)
          / NULLIF(SUM(b.es_genuino = 1), 0), 2)            AS frr_pct,
    SUM(b.es_genuino = 0)                                   AS impostores,
    SUM(b.es_genuino = 0 AND b.sim_top1 >= u.umbral)        AS falsos_positivos,
    ROUND(100.0 * SUM(b.es_genuino = 0 AND b.sim_top1 >= u.umbral)
          / NULLIF(SUM(b.es_genuino = 0), 0), 2)            AS far_pct
FROM umbrales u
CROSS JOIN base b
GROUP BY u.umbral
ORDER BY u.umbral;


-- ---------------------------------------------------------------------------
--  3. Barrido de umbral CON margen contra el segundo lugar.
--
--  Ésta es la tabla que de verdad importa. El margen es la defensa contra el
--  crecimiento del banco: aunque alguien pase el umbral, si el segundo lugar
--  viene pisándole los talones significa que la cara no es distintiva en ese
--  cuadro, y es justo cuando conviene no dejar entrar a nadie.
--
--  Busca la celda con falsos_positivos = 0 y el frr_pct más bajo posible.
--  Si varias empatan, gana la de MENOR umbral y MAYOR margen: rechazar por
--  ambigüedad se siente mejor que rechazar por "no te pareces lo suficiente".
-- ---------------------------------------------------------------------------
WITH RECURSIVE pasos (i) AS (
    SELECT 0 UNION ALL SELECT i + 1 FROM pasos WHERE i < 12   -- 0.30 .. 0.54
),
pasos_m (j) AS (
    SELECT 0 UNION ALL SELECT j + 1 FROM pasos_m WHERE j < 8  -- 0.00 .. 0.16
),
rejilla AS (
    SELECT ROUND(0.30 + i * 0.02, 2) AS umbral,
           ROUND(0.00 + j * 0.02, 2) AS margen_min
    FROM pasos CROSS JOIN pasos_m
),
base AS (
    SELECT
        sim_top1,
        -- El margen guardado puede venir nulo en corridas viejas; se recalcula
        -- cuando hay con qué, y si no hay segundo lugar el margen es total.
        COALESCE(margen, sim_top1 - COALESCE(sim_top2, 0)) AS margen_real,
        (sujeto_top1_id = sujeto_esperado_id)              AS es_genuino
    FROM bio_lab_prueba
    WHERE veredicto <> 'SIN_ROSTRO'
      AND sujeto_esperado_id IS NOT NULL
      AND sujeto_top1_id     IS NOT NULL
      AND sim_top1           IS NOT NULL
)
SELECT
    r.umbral,
    r.margen_min,
    SUM(b.es_genuino = 1 AND (b.sim_top1 < r.umbral OR b.margen_real < r.margen_min))
                                                            AS falsos_rechazos,
    ROUND(100.0 * SUM(b.es_genuino = 1 AND (b.sim_top1 < r.umbral OR b.margen_real < r.margen_min))
          / NULLIF(SUM(b.es_genuino = 1), 0), 2)            AS frr_pct,
    SUM(b.es_genuino = 0 AND b.sim_top1 >= r.umbral AND b.margen_real >= r.margen_min)
                                                            AS falsos_positivos,
    ROUND(100.0 * SUM(b.es_genuino = 0 AND b.sim_top1 >= r.umbral AND b.margen_real >= r.margen_min)
          / NULLIF(SUM(b.es_genuino = 0), 0), 2)            AS far_pct
FROM rejilla r
CROSS JOIN base b
GROUP BY r.umbral, r.margen_min
ORDER BY r.umbral, r.margen_min;


-- ---------------------------------------------------------------------------
--  4. Separación de las dos poblaciones.
--
--  Si el máximo de los impostores queda POR DEBAJO del mínimo de los genuinos,
--  hay un hueco limpio y cualquier umbral en medio funciona. Si se traslapan,
--  no existe umbral perfecto y hay que elegir de qué lado dolerse -- y en un
--  control de acceso el lado correcto es rechazar de más.
-- ---------------------------------------------------------------------------
SELECT
    CASE WHEN sujeto_top1_id = sujeto_esperado_id THEN 'GENUINO' ELSE 'IMPOSTOR' END AS poblacion,
    COUNT(*)              AS n,
    ROUND(MIN(sim_top1), 4)  AS sim_min,
    ROUND(AVG(sim_top1), 4)  AS sim_media,
    ROUND(MAX(sim_top1), 4)  AS sim_max,
    ROUND(AVG(COALESCE(margen, sim_top1 - COALESCE(sim_top2, 0))), 4) AS margen_medio
FROM bio_lab_prueba
WHERE veredicto <> 'SIN_ROSTRO'
  AND sujeto_esperado_id IS NOT NULL
  AND sujeto_top1_id     IS NOT NULL
  AND sim_top1           IS NOT NULL
GROUP BY poblacion;


-- ---------------------------------------------------------------------------
--  5. ¿Dónde se cae? Por luz, distancia y ángulo.
--
--  Esto no fija el umbral: fija la COMPUERTA DE CALIDAD del dispositivo. Si
--  la similitud se desploma por debajo de cierta luma o cierto ancho de rostro,
--  ese es el punto donde la app debe decir "acércate" en vez de mandar el
--  vector al servidor y gastarse un intento.
-- ---------------------------------------------------------------------------
SELECT
    CASE
        WHEN luma IS NULL  THEN 'sin dato'
        WHEN luma <  40    THEN 'a) penumbra   <40'
        WHEN luma <  70    THEN 'b) baja       40-70'
        WHEN luma < 120    THEN 'c) normal     70-120'
        ELSE                    'd) alta       >120'
    END                                                     AS tramo_luz,
    COUNT(*)                                                AS n,
    ROUND(AVG(sim_top1), 4)                                 AS sim_media,
    ROUND(AVG(CASE WHEN sujeto_top1_id = sujeto_esperado_id THEN sim_top1 END), 4)
                                                            AS sim_media_genuinos,
    SUM(sujeto_top1_id <> sujeto_esperado_id)               AS confusiones
FROM bio_lab_prueba
WHERE veredicto <> 'SIN_ROSTRO'
  AND sujeto_esperado_id IS NOT NULL
  AND sim_top1           IS NOT NULL
GROUP BY tramo_luz
ORDER BY tramo_luz;

SELECT
    CASE
        WHEN rostro_px IS NULL THEN 'sin dato'
        WHEN rostro_px < 100   THEN 'a) lejos   <100 px'
        WHEN rostro_px < 160   THEN 'b) medio   100-160 px'
        WHEN rostro_px < 240   THEN 'c) cerca   160-240 px'
        ELSE                        'd) encima  >240 px'
    END                                                     AS tramo_distancia,
    COUNT(*)                                                AS n,
    ROUND(AVG(sim_top1), 4)                                 AS sim_media,
    ROUND(AVG(nitidez), 1)                                  AS nitidez_media,
    SUM(sujeto_top1_id <> sujeto_esperado_id)               AS confusiones
FROM bio_lab_prueba
WHERE veredicto <> 'SIN_ROSTRO'
  AND sujeto_esperado_id IS NOT NULL
  AND sim_top1           IS NOT NULL
GROUP BY tramo_distancia
ORDER BY tramo_distancia;

SELECT
    CASE
        WHEN yaw IS NULL       THEN 'sin dato'
        WHEN ABS(yaw) <= 10    THEN 'a) de frente   <=10 grados'
        WHEN ABS(yaw) <= 20    THEN 'b) ligero      10-20'
        WHEN ABS(yaw) <= 30    THEN 'c) marcado     20-30'
        ELSE                        'd) de perfil   >30'
    END                                                     AS tramo_angulo,
    COUNT(*)                                                AS n,
    ROUND(AVG(sim_top1), 4)                                 AS sim_media,
    SUM(sujeto_top1_id <> sujeto_esperado_id)               AS confusiones
FROM bio_lab_prueba
WHERE veredicto <> 'SIN_ROSTRO'
  AND sujeto_esperado_id IS NOT NULL
  AND sim_top1           IS NOT NULL
GROUP BY tramo_angulo
ORDER BY tramo_angulo;
