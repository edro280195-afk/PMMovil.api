-- OPCIONAL. Ejecutar una sola vez en la base traf/traf3 (pmmtraf). El API funciona igual sin este script: lo unico que
-- se pierde es el aviso «faltan N fotos del dano por llegar» del enlace publico de la entrada (mientras la cola de la app
-- sube las fotos). Las fotos de dano NO necesitan nada nuevo: viven en la tabla fotos con ntipodocto = 103, que en
-- tipodocumentos es «DANADA» (formulario PREOP04), el apartado de dano de siempre.
--
-- Agrega cuantas fotos de dano espera el servidor para esa entrada (la app avisa al guardar). NULL = no se sabe / entrada
-- vieja / sin dano declarado.
ALTER TABLE traf3
    ADD COLUMN danada_fotos_esperadas INT NULL AFTER danada_descripcion;
