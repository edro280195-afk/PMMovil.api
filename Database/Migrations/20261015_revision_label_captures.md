# Captura segura de etiquetas

Implementación local. Este cambio no ejecuta SQL, no cambia configuración ni despliega el API.

## Preparación de base de datos

Revisar y aplicar manualmente `20261015_revision_label_captures.sql` en la base de tráfico configurada.
También debe existir `traf3.hazmat_grupo CHAR(3)` (migración de Hazmat del 14 de octubre de 2026).
El servicio comprueba tabla/columnas requeridas y motor InnoDB de `revdet`, `traf3`, `fotos` y
`revision_label_captures` antes de escribir. Si falta preparación responde 503 `MIGRATION_REQUIRED`.
La tabla nueva conserva vínculo UUID–partida–foto y auditoría de valores antes/después; no modifica las
tablas legacy. La foto mantiene tipo 107, nombre y carpeta que ya consumen FoxPro y `FotoController`.

## Contrato HTTP

Ambas operaciones requieren JWT y módulo Revisión. Confirmar exige `Modificar` y `Agregar`.

`GET /api/Revision/label-captures/context/{idRevdet}?crefAgrupada=...`

Devuelve `success`, `idRevdet`, `snapshot`, `canEdit`, `canMarkReviewed`, `message`.
El snapshot contiene `numLote`, `numSer`, `marca`, `modelo`, `cPaisOrigen`, `revisada`,
`hazmatUn`, `hazmatClase`, `hazmatGrupo`. Debe conservarse hasta enviar; refrescarlo automáticamente
justo antes de confirmar escondería las modificaciones de otro usuario.

`POST /api/Revision/label-captures/confirm`

```json
{
  "captureId": "3583d20a-27cb-4af6-9f59-3e840ba7b8ae",
  "crefAgrupada": "01C260000000000",
  "idRevdet": 7,
  "expectedSnapshot": {
    "numLote": "L-001", "numSer": "", "marca": "SKF", "modelo": "",
    "cPaisOrigen": "THA", "revisada": false,
    "hazmatUn": "1866", "hazmatClase": "3", "hazmatGrupo": "III"
  },
  "fields": { "numLote": "L-002", "cPaisOrigen": "THA" },
  "hazmat": { "un": "1866" },
  "photoBase64": "BASE64_DE_LA_FOTO_JPG_O_PNG",
  "extension": "jpg",
  "markReviewed": false,
  "physicalReviewConfirmed": false,
  "observations": [
    { "field": "lote", "value": "L-002", "rawValue": "(10)L-002", "source": "GS1", "accepted": true }
  ],
  "rawText": "Texto opcional leído de la etiqueta",
  "rawCodes": ["Código opcional leído"]
}
```

Campos omitidos/null preservan valores; blanco explícito se rechaza. `numLote` añade valores y
elimina duplicados sin quitar signos internos ni perder lotes anteriores. El país se valida contra
el catálogo. UN admite cuatro dígitos y prefijo UN opcional; clase se valida contra el catálogo activo;
grupo solo I, II o III. Clase/grupo ausentes se conservan, también en el PATCH Hazmat anterior.

Para marcar partida se requiere **ambos** `markReviewed=true` y `physicalReviewConfirmed=true`.
Se valida pertenencia de partida/factura/referencia y los candados actuales, con desbloqueo vigente
para el usuario solicitante. La operación no cambia cantidades ni completa factura/referencia;
el cierre actual conserva validaciones de bultos, peso, hojas de seguridad y discrepancias.

El envío se congela por `captureId`, incluyendo snapshot, decisiones, auditoría e imagen. Reintentar
debe reutilizar el mismo UUID y contenido. UUID exitoso con contenido distinto devuelve 409
`CAPTURE_CONFLICT`; un snapshot que cambió devuelve 409 `SNAPSHOT_CONFLICT` con datos actuales.
Tras revisar el conflicto, una nueva decisión usa una nueva captura. Un HTTP fallido no significa
que COMMIT haya fallado: conservar siempre el envío para reintentar.

Éxito 200: `success`, `captureId`, `idRevdet`, `photoId`, `revisada`, `snapshot`, `replayed`, `message`.
Repetir un envío confirmado devuelve la respuesta persistida con `replayed=true` y no agrega fotos.
503 `COMMIT_UNCERTAIN` exige reenviar exactamente la misma captura. `REVIEW_LOCKED`,
`HAZMAT_INCONSISTENT` y `RECOVERY_REQUIRED` requieren atender el mensaje; no descartar el borrador.

## Escritura y recuperación

Se escribe una copia durable en `.label-captures`, se bloquean los datos y se coteja el snapshot.
La imagen final queda escrita y validada antes del COMMIT que guarda partida, Hazmat, foto y captura.
Un manifiesto por UUID permite identificar un archivo de una ejecución interrumpida. Un fallo conocido
anterior al COMMIT revierte SQL y retira el archivo propio. Si el resultado del COMMIT es incierto, se
conserva la imagen: el siguiente envío del mismo UUID consulta primero la respuesta persistida.
Si no hubo COMMIT, la recuperación comprueba ausencia de metadatos y hash antes de retirar el archivo.
No se borran imágenes por antigüedad ni se elimina evidencia que pueda corresponder a un COMMIT válido.

Los manifiestos de capturas que nunca vuelvan a enviarse deben revisarse con su UUID y la tabla de
capturas antes de limpiarlos. No existe transacción distribuida entre el disco y MySQL.

La auditoría de la tabla nueva sí forma parte de la transacción; las notificaciones SignalR y el historial
general son posteriores y su fallo no invalida una confirmación. No se guarda base64 ni se registran
códigos/textos leídos en logs. Texto crudo y observaciones quedan en la auditoría autenticada.

`parteCte`, `hazmat`, `cptCveHazmat` se agregan a la respuesta de partidas solo desde las columnas
`parte_cte`, `lhazmat`, `cpt_cve_hazmat` que existan realmente; si no están, se entregan valores vacíos.

## Verificación local sin datos

```powershell
dotnet build --no-restore --nologo -v quiet -clp:ErrorsOnly
dotnet run --project Tests\LabelCapture.Regression\LabelCapture.Regression.csproj --verbosity quiet
```

Las regresiones ejercitan multilote, Hazmat parcial, snapshot, firma de idempotencia, confirmación física,
contrato JSON, autenticación declarativa y fallos de validación/permisos anteriores al I/O. No abren bases
de datos ni levantan el servidor. Antes de habilitar en producción falta una prueba de integración en
una base aislada con la migración aplicada, incluidos corte de red y fallo de disco/COMMIT.
