-- Ejecutar una sola vez en la base traf/traf3 antes de publicar esta version del API.
-- Agrega la descripcion de mercancia danada, capturada junto al toggle "danada" ya
-- existente en traf3. NULL = sin descripcion (entradas viejas o sin danio declarado).
ALTER TABLE traf3
    ADD COLUMN danada_descripcion VARCHAR(500) NULL AFTER danada;
