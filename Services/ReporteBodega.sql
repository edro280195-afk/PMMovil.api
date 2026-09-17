

SELECT
    r.relno,
    e.creferencia,
    r.usucargamcia AS bodeguero,
    r.caja,
    r.bodega,
    r.estatus_carga AS estatus,
    COALESCE(MIN(e.time_scan), r.inicio_carga) AS inicio_carga,
    r.fin_carga,
    r.observa,
    r.sellos2 AS sellos,
    r.placas,
    -- Datos representativos de la primera traf2 de la relacion
    COALESCE(MIN(t2.creferencia), '') AS creferencia,
    COALESCE(MIN(t2.mcargaobserva), '') AS carga_observa,
    COALESCE(MIN(t.cclienteidtraf), '') AS cliente_id,
    MIN(t.dfechaarribo) AS fecha_arribo,
    MIN(t.dfechasalida) AS fecha_salida,
    COALESCE(MIN(t.cnoguia), '') AS no_guia,
    -- Bultos: contamos IDs únicos para no multiplicar
    COALESCE(COUNT(DISTINCT e.id_etiqueta_mcia), 0) AS total_bultos,
    COALESCE(COUNT(DISTINCT CASE WHEN e.estatus = 'CARGADO' THEN e.id_etiqueta_mcia END), 0) AS bultos_cargados
FROM relacion r
LEFT JOIN traf2 t2 ON t2.nrelacion = r.relno
LEFT JOIN traf  t  ON t.creferencia  = t2.creferencia
LEFT JOIN agrupa a ON a.creferencia  = t2.creferencia
LEFT JOIN etiqueta_mcia e ON e.creferencia = a.cref_agrupada
WHERE r.bodega = '01'
  AND r.usucargamcia <> ''
  AND (YEAR(r.inicio_carga) = 0000 OR YEAR(r.inicio_carga) >= 2000)
GROUP BY r.relno
HAVING DATE(COALESCE(MIN(e.time_scan), r.inicio_carga)) = CURDATE()
ORDER BY inicio_carga ASC;
