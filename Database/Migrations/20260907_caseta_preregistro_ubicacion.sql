-- =====================================================================
-- Histórico de recorrido de los camiones pre-registrados ("migas de pan")
-- Base: pmmutileria (misma que caseta_preregistro desde el 05/09/2026)
-- Servidor objetivo: MySQL 5.7
--
-- CONTEXTO:
--   Hasta hoy `caseta_preregistro.lat/lon/fubicacion` se SOBREESCRIBÍA en cada
--   reporte de posición -- el propio esquema lo documentaba: "solo la ultima
--   posicion, se sobrescribe. Sin historico de recorrido". Con una sola
--   posición no hay rastro en el mapa, no hay hora real de llegada al portón,
--   no hay tiempo esperando afuera, y no se puede reconstruir nada cuando un
--   cliente reclama.
--
--   Esta tabla guarda cada señal como un renglón nuevo. Las columnas
--   `lat/lon/fubicacion/eta_*` de `caseta_preregistro` NO se eliminan: se
--   quedan como caché de "última posición conocida" para que el endpoint
--   Caseta/preregistro/en-camino (que ya consume el Android) no cambie de
--   contrato.
--
-- QUÉ HACE:
--   1. Crea `pmmutileria.caseta_preregistro_ubicacion`.
--   2. Nada más. No toca ni borra datos existentes. Correrlo dos veces es
--      inofensivo (IF NOT EXISTS).
--
-- PRIVACIDAD:
--   Se purga junto con su pre-registro. `caseta_preregistro.fpurga` es
--   fcrea + 72 h y PreregistroPurgeWorker ya borra por ahí; este script deja
--   la columna `fpurga` denormalizada en cada renglón para que el borrado sea
--   un DELETE directo por índice, sin JOIN contra una fila que quizá ya no
--   exista.
-- =====================================================================

USE `pmmutileria`;

CREATE TABLE IF NOT EXISTS `caseta_preregistro_ubicacion` (
  `id`         BIGINT       NOT NULL AUTO_INCREMENT,

  -- Referencia lógica -> caseta_preregistro.folio. Sin FK real, igual que el
  -- resto del esquema: `id_cuenta` tampoco la tiene.
  `folio`      VARCHAR(24)  NOT NULL,

  `lat`        DECIMAL(9,6) NOT NULL,
  `lon`        DECIMAL(9,6) NOT NULL,

  -- Momento en que el dispositivo tomó la lectura (no cuando llegó al API):
  -- el portal puede acumular señales sin red y mandarlas juntas después.
  `fecha`      DATETIME     NOT NULL,

  -- Radio de incertidumbre en metros que reporta el GPS. Una señal de ±800 m
  -- (torre de celular, no satélite) no debe pintarse igual que una de ±8 m.
  `precision_m` INT         DEFAULT NULL,

  -- km/h reportados por el dispositivo. NULL cuando el navegador no los da
  -- (pasa seguido en escritorio y en algunos Android).
  `velocidad`  DECIMAL(6,2) DEFAULT NULL,

  -- PORTAL | APP | TELEMATICA. Hoy solo escribe PORTAL; las otras dos quedan
  -- reservadas para la app del chofer y para la telemática de las líneas.
  `origen`     VARCHAR(12)  NOT NULL DEFAULT 'PORTAL',

  -- ETA recalculado en el momento de esta señal, para poder graficar cómo se
  -- movió la estimación durante el viaje (y auditar si el ETA sirve o no).
  `eta_minutos` INT         DEFAULT NULL,
  `eta_km`     DECIMAL(7,2) DEFAULT NULL,

  -- Distancia en línea recta a la bodega destino al momento de la señal.
  -- Se calcula en el API (haversine) para no depender de Google en cada punto.
  `distancia_km` DECIMAL(7,2) DEFAULT NULL,

  -- true cuando esta señal cayó dentro de la geocerca de la planta. Es el
  -- sello de arribo real: la primera señal con dentro_geocerca=1 es la hora
  -- a la que el camión llegó de verdad al portón.
  `dentro_geocerca` TINYINT(1) NOT NULL DEFAULT 0,

  -- Copia de caseta_preregistro.fpurga para borrar por índice sin JOIN.
  `fpurga`     DATETIME     NOT NULL,

  PRIMARY KEY (`id`),
  KEY `ix_folio_fecha` (`folio`, `fecha`),
  KEY `ix_purga` (`fpurga`),
  KEY `ix_geocerca` (`folio`, `dentro_geocerca`, `fecha`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- =====================================================================
-- Verificación (correr a mano después):
--
--   SELECT COUNT(*) FROM pmmutileria.caseta_preregistro_ubicacion;
--   -- 0 la primera vez; sube conforme el portal empiece a mandar señales.
--
--   DESCRIBE pmmutileria.caseta_preregistro_ubicacion;
--   -- 13 columnas, PK id, 3 índices.
-- =====================================================================
