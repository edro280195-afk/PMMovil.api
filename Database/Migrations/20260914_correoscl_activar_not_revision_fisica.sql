-- Activa correoscl.not_revision_fisica = 1 para los contactos de dominio
-- @grupopmm.com que ya tienen arribo_bodega = 1, siguiendo el mismo cruce
-- (por correo, no hay llave real entre correoscl y usuarios) que ya usa
-- RevisionReportService.ObtenerDestinatariosClienteAsync: solo cuenta si ese
-- correo es ademas una cuenta de PMM activa en lugar '09'.
--
-- Previsualizado en pmmtraf (192.168.10.30) el 2026-09-14 con el SELECT de
-- abajo, sin ejecutar el UPDATE:
--   978 filas cambiarian, en 375 clientes distintos, 45 correos distintos.
--   Los que mas contactos ganan: mariana.ortegon (132), elsa.gomez (123),
--   tatis.guevara (113), lourdes.marquez (47), johanna.acosta (46)...
--
-- Ojo: hay ~51 correos con arribo_bodega=1 y dominio @grupopmm.com que NO
-- pasan el candado de usuarios (no matchean ninguna cuenta activa en lugar
-- '09') -- esos se quedan sin tocar, tal como debe ser. Incluye
-- sergio.miranda@grupopmm.com (el mismo caso que ya vimos en
-- RevisionReportService: sigue registrado como contacto pero su usuario ya
-- no esta activo/en lugar 09) y algunos con formato sucio en el correo
-- (ej. "Hernandez fabiola.hernandez@grupopmm.com", con el nombre pegado
-- adelante) -- no se tocan ni se corrigen aqui, fuera de alcance de este
-- script.

-- ============================================================
-- 1) PREVIEW -- correr primero, revisar antes de correr el UPDATE
-- ============================================================
SELECT
    c.idcorreo,
    c.cidcte,
    cl.cnombre              AS cliente,
    c.ccontacto,
    c.cemail,
    c.not_revision_fisica   AS actual
FROM correoscl c
LEFT JOIN clientes cl ON cl.cidcte = c.cidcte
WHERE c.arribo_bodega = 1
  AND c.cemail LIKE '%@grupopmm.com'
  AND c.inactivo = 0
  AND (c.not_revision_fisica IS NULL OR c.not_revision_fisica <> 1)
  AND EXISTS (
        SELECT 1 FROM usuarios u
        WHERE u.correo = c.cemail AND u.lugar = '09' AND u.inactivo = 0
      )
ORDER BY cl.cnombre, c.cemail;

-- ============================================================
-- 2) UPDATE -- correr solo despues de revisar el preview de arriba
-- ============================================================
UPDATE correoscl c
SET c.not_revision_fisica = 1
WHERE c.arribo_bodega = 1
  AND c.cemail LIKE '%@grupopmm.com'
  AND c.inactivo = 0
  AND (c.not_revision_fisica IS NULL OR c.not_revision_fisica <> 1)
  AND EXISTS (
        SELECT 1 FROM usuarios u
        WHERE u.correo = c.cemail AND u.lugar = '09' AND u.inactivo = 0
      );

-- ============================================================
-- 3) Verificacion sugerida despues de correrlo
-- ============================================================
-- SELECT COUNT(*) FROM correoscl WHERE not_revision_fisica = 1;
-- (antes del UPDATE eran 9 filas, todas del grupo ExxonMobil -- ver
-- RevisionReportService.cs; despues deberian ser 9 + 978 = 987)
