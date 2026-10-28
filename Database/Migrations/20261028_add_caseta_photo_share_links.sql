-- Tokens temporales para compartir evidencia de entrada y salida de caseta.
-- El token completo solo viaja en el enlace; en la base queda guardado su hash.
USE pmmutileria;

CREATE TABLE IF NOT EXISTS caseta_fotos_links_compartidos (
    id_link       INT AUTO_INCREMENT PRIMARY KEY,
    token         CHAR(8) NOT NULL,
    token_hash    CHAR(64) NOT NULL,
    tipo          VARCHAR(16) NOT NULL,
    referencia    VARCHAR(64) NOT NULL,
    usucrea       VARCHAR(50) NOT NULL,
    fcrea         DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    fexpira       DATETIME NOT NULL,
    revocado      TINYINT(1) NOT NULL DEFAULT 0,
    UNIQUE KEY uq_caseta_fotos_token_hash (token_hash),
    KEY ix_caseta_fotos_referencia (tipo, referencia),
    KEY ix_caseta_fotos_expira (fexpira)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
