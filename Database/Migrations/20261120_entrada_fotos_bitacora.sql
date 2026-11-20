-- Bitacora de fotos de las entradas de bodega (pmmtraf). El panel web NO agrega ni elimina fotos sin esta tabla:
-- cada alta y cada baja deja su renglon (quien, cuando, que foto y desde donde: PANEL = web, APP = telefono).
-- Las bajas se registran ANTES de borrar; si la tabla falta, el panel rechaza la operacion con un aviso claro.
-- Servidor 30 = MySQL 5.7. Es idempotente.

USE pmmtraf;

CREATE TABLE IF NOT EXISTS entrada_fotos_bitacora (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    creferencia VARCHAR(20) NOT NULL,
    accion VARCHAR(10) NOT NULL,
    idfotos INT NULL,
    ntipodocto INT NOT NULL,
    nlineno INT NOT NULL DEFAULT 0,
    cextension VARCHAR(10) NOT NULL DEFAULT '',
    usuario VARCHAR(30) NOT NULL,
    origen VARCHAR(10) NOT NULL,
    detalle VARCHAR(200) NOT NULL DEFAULT '',
    fecha DATETIME NOT NULL,
    KEY ix_entrada_fotos_bitacora_ref (creferencia, id),
    KEY ix_entrada_fotos_bitacora_fecha (fecha)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

SELECT COUNT(*) AS filas FROM entrada_fotos_bitacora;
