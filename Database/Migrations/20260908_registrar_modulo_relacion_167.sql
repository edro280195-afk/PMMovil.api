-- Registra el modulo movil 167 (MobileModuleIds.WarehouseRelation) en el
-- catalogo de pmmtraf.modulos. El codigo (LegacyModuleMap, HomeScreen.kt)
-- ya asume que este ID existe y hereda lectura del modulo historico 92
-- "Autorizar Salidas" -- pero sin esta fila, Auth/permisos nunca puede
-- devolverlo y la tarjeta "Relacion" no aparece para NINGUN usuario que no
-- sea de Sistemas, sin importar los permisos que tenga en `accesos`.
--
-- Sigue el mismo patron que las filas 165/166 (mismo lote, mismos valores
-- por default en las columnas legacy que no aplican a un modulo movil).
--
-- Verificado antes de escribir este script: SELECT * FROM modulos WHERE
-- ModuloID = 167 -> 0 filas (2026-09-08).

INSERT INTO modulos (
    ModuloID, Descripcion, NodoID, SoloAcceso, Categoria, NivelAsignado,
    Imagen, ImagenActiva, Inactivo, OrdenenLista, Sistemas, NombreTabla,
    Formulario, campoid, grid_vista, modulosist, parametros, tablabusca,
    campverbusq, verinfo, aplibus, opc1, campo1, opc2, campo2, opc3,
    campo3, tabla3, opc4, campo4, tabla4, busqueda, opc5, fbodega,
    fopera, FormularioWeb, sistema, iconW, orden_vista
) VALUES (
    167, 'Autorizar Salida de Relacion', 'MOVBOD', 0, 0, 2,
    0, 0, 0, '51-16', '', '',
    '', '', '', '', '', '',
    '', '1', 1, '', '', '', '', '',
    '', '', '', '', '', '', '', '',
    '', '', '', 'clipboard-check', ''
);

-- No toca `accesos`: en cuanto exista la fila, brgarza (y cualquier otro
-- usuario con el modulo 92 "Autorizar Salidas") hereda automaticamente
-- la lectura via LegacyModuleMap sin tocar nada mas.

-- Verificacion sugerida despues de correrlo:
-- SELECT * FROM modulos WHERE ModuloID = 167;
