-- Tabla para los links públicos de "Seguimiento de revisión" -- página
-- SEPARADA de "Seguimiento de entrada" (entrada_links_compartidos): antes el
-- botón Compartir de Revisión reusaba el link/página de Entrada (mismo
-- token, mismo contenido -- solo datos de traf: cliente, transportista,
-- fotos de recepción). Esta tabla soporta el equivalente para Revisión:
-- estatus por factura, discrepancias y fotos de evidencia de discrepancia
-- (fact/revdet/fotos ntipodocto=103), nunca datos de la entrada.
--
-- Ejecutar una sola vez en pmmutileria antes de publicar la API con el
-- endpoint POST Revision/{crefAgrupada}/compartir.

USE pmmutileria;

CREATE TABLE IF NOT EXISTS revision_links_compartidos (
    id_link         INT AUTO_INCREMENT PRIMARY KEY,
    token           CHAR(32) NOT NULL,
    cref_agrupada   VARCHAR(30) NOT NULL,
    usucrea         VARCHAR(30) NOT NULL,
    fcrea           DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    fexpira         DATETIME NOT NULL,
    revocado        TINYINT(1) NOT NULL DEFAULT 0,
    UNIQUE KEY uq_revision_links_token (token),
    KEY ix_revision_links_cref (cref_agrupada)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
