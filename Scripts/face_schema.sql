-- ============================================================================
--  Acceso facial -- tablas de PRODUCCIÓN
--  Base: reportes  (la misma de AuthService, para que claveusuario cuadre sin
--                   brincar de conexión)
--
--  Reemplazan a las bio_lab_* para el uso real. Las del laboratorio NO se
--  tocan: son la evidencia de las mediciones y no estorban.
--
--  Diferencia de fondo con el laboratorio: aquí el banco NUNCA sale del
--  servidor. El dispositivo manda un vector de 512 bytes y el servidor decide
--  quién es y firma el token.
-- ============================================================================

USE reportes;


-- ---------------------------------------------------------------------------
--  Un registro por alta de rostro.
--
--  Sobre la ausencia de llave foránea contra `usuarios`: esa tabla es
--  heredada y no se sabe con qué motor ni con qué índices vive. Un
--  FOREIGN KEY contra una tabla MyISAM (o sin índice único en usuarioid)
--  revienta el CREATE y deja el despliegue a medias. Se resuelve con índice
--  y validación en el servicio; abajo queda el ALTER opcional por si algún
--  día se confirma que se puede.
--
--  aceptacion_at es el sello de consentimiento: dato biométrico de empleados.
--  Sin ese sello el enrolamiento no se guarda.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS face_enrolamiento (
    id                BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,

    usuarioid         VARCHAR(20)       NULL,   -- copia del usuarios.usuarioid al momento del alta
    claveusuario      VARCHAR(80)   NOT NULL,
    nombre            VARCHAR(160)      NULL,   -- copia para la bitácora, no es la fuente de verdad

    modelo            VARCHAR(40)   NOT NULL DEFAULT 'sface-2021dec',
    dims              SMALLINT      NOT NULL DEFAULT 128,

    -- Consentimiento
    aceptacion_at     DATETIME      NOT NULL,
    aceptacion_texto  TEXT               NULL,   -- texto completo del aviso que se aceptó

    -- Contexto del alta
    dispositivo       VARCHAR(120)      NULL,
    planta            VARCHAR(40)       NULL,

    estado            VARCHAR(16)   NOT NULL DEFAULT 'ACTIVO',   -- ACTIVO / REVOCADO
    created_at        DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP,
    revocado_at       DATETIME          NULL,

    KEY ix_face_enrol_usuario (claveusuario, estado),
    KEY ix_face_enrol_fecha   (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;


-- ---------------------------------------------------------------------------
--  Las plantillas: ~12 vectores por persona, uno por pose canónica.
--
--  claveusuario va DESNORMALIZADO a propósito. El login lo lee en caliente en
--  cada intento y no vale la pena pagar el JOIN contra face_enrolamiento en el
--  camino crítico.
--
--  El vector son 128 floats little-endian = 512 bytes exactos, igual que en el
--  laboratorio y tal como los saca FaceRecognizerSF. Se guarda CRUDO, sin
--  normalizar: la normalización L2 se hace en memoria al comparar, para poder
--  re-analizar después con otro criterio sin haber perdido información.
--
--  pose_sector: -1 = frontal, 0..11 = sector de 30 grados del aro de enrolamiento
--  (una plantilla por sector y por vuelta; el aro que ve el usuario se dibuja
--  con 72 tramos, pero eso es progreso visual y no se guarda).
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS face_plantilla (
    id               BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,
    enrolamiento_id  BIGINT        NOT NULL,
    claveusuario     VARCHAR(80)   NOT NULL,

    modelo           VARCHAR(40)   NOT NULL DEFAULT 'sface-2021dec',
    dims             SMALLINT      NOT NULL DEFAULT 128,
    vector           BLOB          NOT NULL,

    -- Pose en la que se capturó
    pose_sector      TINYINT       NOT NULL DEFAULT -1,
    yaw              FLOAT             NULL,
    pitch            FLOAT             NULL,
    roll             FLOAT             NULL,

    -- Condiciones. Una plantilla capturada a oscuras contamina todos los
    -- reconocimientos posteriores, así que conviene poder auditarlas.
    luma             FLOAT             NULL,
    nitidez          FLOAT             NULL,
    rostro_px        INT               NULL,
    score_deteccion  FLOAT             NULL,

    activo           TINYINT(1)    NOT NULL DEFAULT 1,
    created_at       DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP,

    KEY ix_face_plantilla_user  (claveusuario, activo),
    KEY ix_face_plantilla_enrol (enrolamiento_id),
    CONSTRAINT fk_face_plantilla_enrol
        FOREIGN KEY (enrolamiento_id) REFERENCES face_enrolamiento (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;


-- ---------------------------------------------------------------------------
--  Bitácora de TODOS los intentos de entrar con la cara, pasen o no.
--
--  Es la única fuente que va a decir cuándo hay que mover el umbral, y es la
--  mitigación que sostiene el sistema mientras no haya detección de vivacidad:
--  si alguien entra con una foto, aquí queda el rastro con hora y equipo.
--
--  NO guarda a quién se pareció cuando rechaza por debajo del umbral: el
--  claveusuario_top1 solo se llena si el intento fue aceptado o si quedó cerca
--  (arriba del umbral pero sin margen). Un rechazo de plano no debe dejar
--  escrito "se parecía a Fulano" -- eso es un mapa para el que quiera probar
--  suerte.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS face_intento (
    id                  BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,

    claveusuario_top1   VARCHAR(80)       NULL,
    sim_top1            FLOAT             NULL,
    claveusuario_top2   VARCHAR(80)       NULL,
    sim_top2            FLOAT             NULL,
    margen              FLOAT             NULL,

    umbral              FLOAT             NULL,   -- el que estaba vigente al decidir
    margen_minimo       FLOAT             NULL,

    decision            VARCHAR(16)   NOT NULL,   -- ACEPTADO / RECHAZADO
    motivo              VARCHAR(32)       NULL,   -- BAJO_UMBRAL / MARGEN_INSUFICIENTE /
                                                  -- SIN_PLANTILLAS / VECTOR_INVALIDO
    intento             TINYINT       NOT NULL DEFAULT 1,   -- 1 o 2 dentro de la misma presencia

    -- Contexto
    planta              VARCHAR(40)       NULL,
    galeria_n           INT               NULL,   -- cuántas personas se compararon
    dispositivo         VARCHAR(120)      NULL,
    ip                  VARCHAR(45)       NULL,

    -- Condiciones medidas en el dispositivo
    luz_lux             FLOAT             NULL,
    luma                FLOAT             NULL,
    nitidez             FLOAT             NULL,
    rostro_px           INT               NULL,
    score_deteccion     FLOAT             NULL,
    yaw                 FLOAT             NULL,
    pitch               FLOAT             NULL,
    roll                FLOAT             NULL,

    ms_dispositivo      INT               NULL,   -- detección + embedding en el equipo
    ms_servidor         INT               NULL,   -- búsqueda 1:N

    created_at          DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP,

    KEY ix_face_intento_fecha    (created_at),
    KEY ix_face_intento_usuario  (claveusuario_top1, created_at),
    KEY ix_face_intento_decision (decision, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;


-- ---------------------------------------------------------------------------
--  Parámetros vivos del reconocimiento.
--
--  El umbral y el margen NO van en appsettings.json: se ajustan con datos de
--  la bitácora y tienen que poder moverse sin volver a desplegar el API.
--  Los valores que quedan aquí son un PUNTO DE PARTIDA CONSERVADOR --
--  reemplázalos con lo que salga de face_umbral_analisis.sql.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS face_config (
    clave       VARCHAR(40)   NOT NULL PRIMARY KEY,
    valor       VARCHAR(80)   NOT NULL,
    nota        VARCHAR(255)      NULL,
    updated_at  DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO face_config (clave, valor, nota) VALUES
    ('umbral',          '0.45', 'Coseno mínimo para aceptar. SFace sugiere 0.363 en 1:1; aquí va más alto porque es 1:N.'),
    ('margen_minimo',   '0.06', 'Distancia mínima contra el segundo lugar. Si dos empatan, no entra ninguno.'),
    ('galeria_planta',  '1',    '1 = comparar solo contra quien trabaja en la planta del equipo. Baja N y baja el falso positivo.'),
    ('max_intentos',    '2',    'Intentos por presencia antes de que la cara se retire y deje el gafete al frente.'),
    ('activo',          '0',    '0 = el endpoint responde 503. Se prende cuando el umbral ya salió de datos reales.')
ON DUPLICATE KEY UPDATE nota = VALUES(nota);


-- ---------------------------------------------------------------------------
--  OPCIONAL -- solo si se confirma que `usuarios` es InnoDB y usuarioid es
--  único. Verifica primero con:
--
--      SELECT ENGINE FROM information_schema.TABLES
--       WHERE TABLE_SCHEMA = 'reportes' AND TABLE_NAME = 'usuarios';
--
--      SHOW INDEX FROM usuarios WHERE Column_name = 'usuarioid';
--
--  Si sale InnoDB y hay índice único, entonces:
--
--  ALTER TABLE face_enrolamiento
--      ADD CONSTRAINT fk_face_enrol_usuario
--      FOREIGN KEY (usuarioid) REFERENCES usuarios (usuarioid);
-- ---------------------------------------------------------------------------
