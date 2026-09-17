-- ============================================================================
-- Acceso facial - corrección de capacidad para el texto de consentimiento
--
-- El aviso que manda PMMovil se guarda completo para conservar la evidencia
-- exacta de lo aceptado. La definición inicial de la tabla usaba VARCHAR(255),
-- pero el aviso real supera ese límite y MySQL rechazaba todo el enrolamiento.
-- ============================================================================

USE reportes;

ALTER TABLE face_enrolamiento
    MODIFY COLUMN aceptacion_texto TEXT NULL;
