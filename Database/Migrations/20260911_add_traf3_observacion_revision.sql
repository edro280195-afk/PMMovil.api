-- Ejecutar una sola vez en la base traf/traf3 antes de publicar esta version del API.
-- Observacion general de la revision fisica completa (NO por factura -- para
-- eso ya existe fact.obsrev/fact.obsrev2). Una sola nota por entrada, igual
-- que danada_descripcion. NULL = sin observacion.
ALTER TABLE traf3
    ADD COLUMN observacion_revision VARCHAR(500) NULL AFTER danada_descripcion;
