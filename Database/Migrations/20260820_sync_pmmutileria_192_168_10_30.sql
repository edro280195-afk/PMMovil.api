-- =====================================================================
-- Integración de esquema: pmmutileria en 192.168.10.30 (produccion)
-- Generado: 2026-08-20 -- SOLO AUDITORIA, este archivo NO fue ejecutado.
--
-- `pmmutileria` es una TERCERA base en el mismo servidor .30, distinta de
-- `pmmtraf` y `pmmtools` -- no tiene su propia connection string en
-- appsettings.json, el API le pega via cross-database query dentro de la
-- misma conexion (ej. "FROM pmmutileria.revisiones_barcode_lectura").
-- Referenciada desde: BarcodeController.cs, RevisionReportService.cs,
-- MonitorAnalyticService.cs, CartaPorteService.cs.
--
-- *** CORRECCION sobre la entrega anterior ***: en el primer pase yo
-- habia comparado `revisiones_barcode_sesion`/`revisiones_barcode_lectura`
-- contra `pmmtraf` (porque ahi viven en localhost) y concluí que faltaban
-- por completo en .30 -- estaba mal. El usuario señaló que esas tablas
-- viven en `pmmutileria`, no en `pmmtraf`. Ya audité `pmmutileria` en .30
-- directamente (24 tablas ahi vs 44 en local) y las DOS SI EXISTEN.
-- El unico gap real es una columna faltante en una de ellas -- ver abajo.
-- Ese `CREATE TABLE` que te di antes para esas dos tablas hubiera creado
-- un par de tablas nuevas, vacias y sin usar en la base equivocada -- no
-- lo corras (ya lo quité del script de pmmtraf).
-- =====================================================================


-- ---------------------------------------------------------------------
-- Unico cambio necesario: revisiones_barcode_lectura.cliente_scan_id
-- ---------------------------------------------------------------------

-- Usado para deduplicar lecturas de un mismo dispositivo dentro de una
-- sesion de escaneo (BarcodeController.cs la lee/escribe en varios
-- puntos: alta de lectura, borrado, conteo). Sin esta columna, cualquier
-- endpoint de BarcodeController que la referencie falla en .30 con
-- "Unknown column 'cliente_scan_id'".
ALTER TABLE pmmutileria.revisiones_barcode_lectura
    ADD COLUMN cliente_scan_id VARCHAR(36) NULL AFTER id_sesion;


-- ---------------------------------------------------------------------
-- Verificadas SIN diferencias -- no requieren ningun cambio:
-- ---------------------------------------------------------------------
--   pmmutileria.revisiones_barcode_sesion  -- identica en local y .30
--   pmmutileria.cpt_cve_unidadpeso         -- identica (catalogo Carta Porte)
--   pmmutileria.cpt_embalaje               -- identica (catalogo Carta Porte)
--
-- Nota menor, no accionable: en revisiones_barcode_lectura, la columna
-- `eliminado` es NOT NULL en local y NULLABLE en .30. No rompe nada (el
-- codigo siempre la inserta con 0/1 explicito), se deja tal cual está en
-- .30 para no arriesgar un ALTER innecesario sobre una tabla en uso.


-- =====================================================================
-- NO INCLUIDAS A PROPOSITO -- existen en local `pmmutileria` (44 tablas)
-- pero 0 referencias en el codigo actual de PMMovil.Api. .30 tiene 24
-- tablas en total; estas son las diferencias que se descartaron:
--
--   Solo en local, sin uso por el API:
--     attachments, usersettings, preguntaspendientes, anexo14,
--     chathistorial, chatrooms, chatroomparticipants, messages,
--     messagementions, messagereactions, messagereads, userpresence,
--     hangfire_* (10 tablas -- Hangfire NI SIQUIERA esta referenciado en
--     Program.cs ni en el .csproj de este API; son de otro proyecto/
--     prototipo que comparte la base local, no de PMMovil.Api)
--
--   Solo en .30, sin correspondencia exacta en local (posible diferencia
--   de mayusculas/minusculas del mismo objeto -- MySQL en Windows suele
--   ser case-insensitive para nombres de tabla, asi que probablemente
--   sea el mismo objeto que localhost tiene en minusculas, no uno extra):
--     Anexo14, ChatHistorial, PreguntasPendientes
--
-- *** asignaciones_revision -- YA INVESTIGADA (2026-08-20) ***
-- Esta pmmutileria.asignaciones_revision es una tabla SEÑUELO -- coincide
-- de nombre pero NO es la que usa el API. AsignacionesController.cs,
-- RevisionService.cs y TimerController.cs consultan `asignaciones_revision`
-- SIN calificar de esquema, y BaseRepository.CreateConnection() abre
-- conexion contra PmmtrafDb -- o sea que el objeto real es
-- `pmmtraf.asignaciones_revision`, no este de aqui. Verificado en vivo:
-- `pmmtraf.asignaciones_revision` YA EXISTE tanto en local como en .30,
-- sin diferencias de columnas -- no requiere ningun cambio. Esta copia en
-- pmmutileria (0 filas) no la toca el codigo actual; se deja intacta,
-- no se borra por si otro sistema la usa.
-- =====================================================================
