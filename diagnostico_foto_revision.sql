-- Diagnóstico: por qué la foto 01C253502340000-10701.jpg (evidencia normal de
-- revisión, ntipodocto=107) no se ve en la app aunque el archivo SÍ existe en
-- \\192.168.10.23\Programas\Trafico\Fotos\1423EX521A\01C253502340000-10701.jpg
--
-- FotoController.VerFoto (GET Foto/ver/{id}) reconstruye la carpeta así:
--   cidcte = COALESCE(traf.cclienteidtraf, fact.cclienteid)  -- por creferencia
--   carpeta = COALESCE(clientes.carpeta, cidcte)
-- Si ese cidcte NO corresponde al cliente cuya carpeta es "1423EX521A", la
-- API busca el archivo en la carpeta equivocada y responde 404 aunque el
-- archivo exista -- eso deja el recuadro azul vacío en la app (ya no hay
-- fallo silencioso: con el fix aplicado, además, un cextension mal mapeado
-- ya no puede colar un nombre de archivo incorrecto).
--
-- Corre esto en pmmtraf (solo SELECT, de solo lectura):

SELECT
    f.idfotos,
    f.creferencia,
    f.nlineno,
    f.ntipodocto,
    f.cextension,
    fa.cclienteidtraf                                            AS cidcte_via_traf,
    ff.cclienteid                                                AS cidcte_via_fact,
    COALESCE(NULLIF(fa.cclienteidtraf, ''), NULLIF(ff.cclienteid, '')) AS cidcte_resuelto,
    cl.carpeta                                                   AS carpeta_del_cliente_resuelto
FROM fotos f
LEFT JOIN traf  fa ON fa.creferencia = f.creferencia
LEFT JOIN fact  ff ON ff.cnofact = f.creferencia
                   OR ff.cref_agrupada = f.creferencia
                   OR ff.creferencia = f.creferencia
LEFT JOIN clientes cl ON cl.cidcte = COALESCE(NULLIF(fa.cclienteidtraf, ''), NULLIF(ff.cclienteid, ''))
WHERE f.creferencia = '01C253502340000'
  AND f.ntipodocto IN (107, 103);

-- Qué revisar en el resultado:
--   1) Si "carpeta_del_cliente_resuelto" (o, en su defecto, "cidcte_resuelto")
--      NO es "1423EX521A", ahí está el bug: la carpeta que reconstruye
--      VerFoto no es la carpeta real donde se guardó el archivo.
--   2) Si "cidcte_via_traf" y "cidcte_via_fact" son distintos entre sí, hay
--      que decidir cuál debe ganar (hoy gana traf primero).
--   3) Si ambos vienen NULL, ninguno de los JOIN está encontrando la fila
--      (por ejemplo si "01C253502340000" no es el valor real de
--      fact.cref_agrupada para esta factura).

-- Confirmación cruzada: qué cliente tiene realmente la carpeta "1423EX521A".
SELECT cidcte, cnombre, carpeta
FROM clientes
WHERE carpeta = '1423EX521A';
