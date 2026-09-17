-- ============================================================================
--  Laboratorio biometrico facial -- tablas TEMPORALES de prueba
--  Base: reportes  (la misma que usa AuthService via ReportesDb)
--
--  OJO: esto es un banco de pruebas, NO el diseno definitivo. Sirve para medir
--  si el reconocimiento facial aguanta las condiciones reales de bodega
--  (oscuridad, distancia, angulo) antes de comprometer nada en produccion.
--  Todas las tablas llevan prefijo bio_lab_ para poder borrarlas de un golpe:
--      DROP TABLE bio_lab_prueba, bio_lab_plantilla, bio_lab_sujeto;
-- ============================================================================

-- ---------------------------------------------------------------------------
--  Sujetos de prueba. claveusuario NO tiene llave foranea contra usuarios a
--  proposito: queremos poder enrolar companeros de oficina que no existan en
--  pmmtraf, sin ensuciar el catalogo real.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS bio_lab_sujeto (
    id            INT           NOT NULL AUTO_INCREMENT PRIMARY KEY,
    claveusuario  VARCHAR(80)   NOT NULL,
    nombre        VARCHAR(160)  NOT NULL,
    notas         VARCHAR(255)      NULL,
    activo        TINYINT(1)    NOT NULL DEFAULT 1,
    created_at    DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY uq_bio_lab_sujeto (claveusuario)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------
--  Plantillas (embeddings). Varias por sujeto: cada captura de enrolamiento
--  genera una. El vector son 128 floats little-endian = 512 bytes exactos,
--  tal como los saca FaceRecognizerSF (SFace).
--
--  Se guarda el vector CRUDO, sin normalizar, para poder re-analizar despues
--  con otro criterio. La normalizacion L2 se hace en memoria al comparar.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS bio_lab_plantilla (
    id               BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,
    sujeto_id        INT           NOT NULL,
    modelo           VARCHAR(40)   NOT NULL DEFAULT 'sface-2021dec',
    dims             SMALLINT      NOT NULL DEFAULT 128,
    vector           BLOB          NOT NULL,

    -- Condiciones en las que se capturo esta plantilla. Importan: una
    -- plantilla enrolada a oscuras contamina todos los reconocimientos.
    luz_lux          FLOAT             NULL,
    luma             FLOAT             NULL,
    nitidez          FLOAT             NULL,
    rostro_px        INT               NULL,
    score_deteccion  FLOAT             NULL,
    yaw              FLOAT             NULL,
    roll             FLOAT             NULL,

    dispositivo      VARCHAR(120)      NULL,
    camara           VARCHAR(20)       NULL,
    created_at       DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP,

    KEY ix_bio_lab_plantilla_sujeto (sujeto_id),
    CONSTRAINT fk_bio_lab_plantilla_sujeto
        FOREIGN KEY (sujeto_id) REFERENCES bio_lab_sujeto (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------
--  Cada intento de reconocimiento, con TODAS sus condiciones. Esta es la
--  tabla que de verdad contesta la pregunta: a que nivel de luz deja de
--  funcionar, y cuanto margen hay contra el impostor mas parecido.
--
--  sujeto_esperado_id lo declara el operador ANTES de disparar (la verdad),
--  sujeto_top1_id es lo que dijo el motor. Comparar los dos da el veredicto.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS bio_lab_prueba (
    id                  BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,

    sujeto_esperado_id  INT               NULL,
    sujeto_top1_id      INT               NULL,
    sim_top1            FLOAT             NULL,
    sujeto_top2_id      INT               NULL,
    sim_top2            FLOAT             NULL,
    margen              FLOAT             NULL,   -- sim_top1 - sim_top2
    umbral              FLOAT             NULL,   -- el que estaba vigente al probar
    veredicto           VARCHAR(24)       NULL,   -- ACIERTO / FALSO_POSITIVO /
                                                  -- RECHAZO / SIN_ROSTRO / IMPOSTOR_OK

    -- Condiciones medidas
    luz_lux             FLOAT             NULL,   -- sensor ambiental del equipo
    luma                FLOAT             NULL,   -- brillo medio del recorte, 0-255
    nitidez             FLOAT             NULL,   -- varianza del Laplaciano
    rostro_px           INT               NULL,   -- ancho del bbox en pixeles
    score_deteccion     FLOAT             NULL,   -- confianza de YuNet
    yaw                 FLOAT             NULL,
    roll                FLOAT             NULL,

    -- Desempeno
    ms_deteccion        INT               NULL,
    ms_embedding        INT               NULL,
    ms_total            INT               NULL,

    -- Contexto de la corrida
    escenario           VARCHAR(60)       NULL,   -- "bodega noche", "anden sol", "cubrebocas"
    pantalla_flash      TINYINT(1)    NOT NULL DEFAULT 0,
    exposicion          INT               NULL,   -- compensacion EV aplicada
    dispositivo         VARCHAR(120)      NULL,
    camara              VARCHAR(20)       NULL,
    created_at          DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP,

    KEY ix_bio_lab_prueba_fecha (created_at),
    KEY ix_bio_lab_prueba_escenario (escenario)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
