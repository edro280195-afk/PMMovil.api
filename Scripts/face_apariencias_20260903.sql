-- ============================================================================
-- Acceso facial - apariencias multiples y firma del consentimiento
--
-- DOS CAMBIOS EN UNO, porque los dos tocan face_enrolamiento:
--
-- 1) APARIENCIAS. Hasta hoy, re-enrolarse REEMPLAZABA: el alta nueva revocaba
--    la anterior. Eso obligaba a elegir entre "con lentes" y "sin lentes", y
--    dejaba fuera a quien se rasura la barba hasta que volviera a enrolarse.
--
--    A partir de aqui cada enrolamiento es una APARIENCIA con nombre, y una
--    persona puede tener varias activas a la vez. El login no cambia ni una
--    linea: la galeria ya trae todas las plantillas de todos los enrolamientos
--    ACTIVO, y se queda con el mejor coseno por persona.
--
--    El tope vive en face_config (max_apariencias) y no en el codigo, por la
--    misma razon que el umbral: cada plantilla extra es un vector mas contra el
--    que comparar en 1:N, asi que si la bitacora muestra que sube el ruido, se
--    baja el tope sin volver a desplegar.
--
-- 2) FIRMA. El consentimiento ya se sellaba con el texto aceptado y la fecha,
--    sobre una sesion autenticada. Se agrega el trazo de la firma porque es
--    dato biometrico de empleados y es la evidencia que RH espera ver.
--    El PNG se guarda en la misma fila que el aviso que firma: separarlos
--    permitiria que uno exista sin el otro, que es justo lo que no queremos.
--
-- Ejecutar una sola vez. MySQL no acepta ADD COLUMN IF NOT EXISTS, asi que si
-- ya se corrio, los ALTER fallan con "Duplicate column name" y no pasa nada.
-- ============================================================================

USE reportes;

-- ---------------------------------------------------------------------------
--  1. Apariencias
-- ---------------------------------------------------------------------------
ALTER TABLE face_enrolamiento
    ADD COLUMN etiqueta VARCHAR(40) NULL AFTER nombre;

-- Lo que ya estaba enrolado es la apariencia de siempre de esa persona.
UPDATE face_enrolamiento
SET etiqueta = 'Principal'
WHERE etiqueta IS NULL;

-- El indice de busqueda del estado pasa a listar VARIAS filas por persona, no
-- una: conviene que venga ordenado por fecha desde el indice.
ALTER TABLE face_enrolamiento
    ADD KEY ix_face_enrol_activas (claveusuario, estado, created_at);

-- ---------------------------------------------------------------------------
--  2. Firma del consentimiento
--
--  MEDIUMBLOB y no BLOB: un trazo en PNG anda por los 10-30 KB, que cabria en
--  BLOB (64 KB), pero un equipo con pantalla mas grande o un trazo muy largo
--  se acerca demasiado al limite, y truncar una firma la vuelve inservible
--  justo cuando alguien la pide.
-- ---------------------------------------------------------------------------
ALTER TABLE face_enrolamiento
    ADD COLUMN firma_png MEDIUMBLOB NULL AFTER aceptacion_texto,
    ADD COLUMN firma_at  DATETIME   NULL AFTER firma_png;

-- ---------------------------------------------------------------------------
--  3. Tope de apariencias
--
--  3 alcanza para el caso real que lo motivo: "normal", "con lentes" y la
--  transicion de la barba. Subirlo es cambiar este renglon; no hay que tocar
--  el API.
-- ---------------------------------------------------------------------------
INSERT INTO face_config (clave, valor, nota) VALUES
    ('max_apariencias', '3',
     'Enrolamientos ACTIVO simultaneos por persona. Cada uno suma ~26 vectores a la galeria 1:N.')
ON DUPLICATE KEY UPDATE valor = VALUES(valor), nota = VALUES(nota);


-- ---------------------------------------------------------------------------
--  Verificacion
-- ---------------------------------------------------------------------------
-- Estructura:
--   SHOW COLUMNS FROM face_enrolamiento LIKE 'etiqueta';
--   SHOW COLUMNS FROM face_enrolamiento LIKE 'firma%';
--   SELECT clave, valor FROM face_config WHERE clave = 'max_apariencias';
--
-- Quien tiene mas de una apariencia y cuantos vectores aporta cada una:
--   SELECT e.claveusuario, e.id, e.etiqueta, e.created_at,
--          COUNT(p.id) AS plantillas,
--          (e.firma_png IS NOT NULL) AS firmado
--   FROM face_enrolamiento e
--   LEFT JOIN face_plantilla p ON p.enrolamiento_id = e.id AND p.activo = 1
--   WHERE e.estado = 'ACTIVO'
--   GROUP BY e.id, e.claveusuario, e.etiqueta, e.created_at, firmado
--   ORDER BY e.claveusuario, e.created_at;
