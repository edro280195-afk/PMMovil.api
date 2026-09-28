# Proceso de release — PMMovil.Api

División de trabajo para publicar una nueva versión en producción
(`revisionapi.grupopmm.com`, IIS en el servidor real):

## Quién hace qué

| Paso | Quién |
|---|---|
| Escribir/revisar el código, migraciones SQL nuevas | Claude (o quien desarrolle) |
| `dotnet build` para verificar que compila | Claude |
| Commit en git | Claude, cuando se pida explícitamente |
| `dotnet publish` a una carpeta en `_release/api/` | Claude, cuando se pida explícitamente |
| Copiar los archivos publicados al servidor real (IIS) | **Eduardo, manualmente** |
| Correr migraciones SQL contra `pmmutileria`/`pmmtraf` en el servidor real | **Eduardo, manualmente** |
| Contraseñas / usuarios de MySQL, cualquier cosa en la BD de producción | **Eduardo, nunca Claude** |

Claude **no tiene acceso** al servidor de producción: los publish profiles del
proyecto (`Properties/PublishProfiles/*.pubxml`) son de tipo `FileSystem` —
copian a una carpeta local, no hay Web Deploy/FTP/SSH configurado hacia el
servidor real. Generar el build es responsabilidad de Claude; subirlo al
servidor y correr las migraciones es responsabilidad de Eduardo.

## Cómo generar el release

```bash
cd PMMovil.Api/PMMovil.Api
dotnet publish -c Release -r win-x64 --self-contained false \
  -o "../../_release/api/PMMovil.Api-release-<fecha>-<slug-descriptivo>"
```

Ejemplo real: `_release/api/PMMovil.Api-release-20260928-seguridad-publico/`.

## Antes de copiar un release al servidor

1. **Revisar `Database/Migrations/`** — si el release trae una migración nueva
   (fecha posterior a la última que ya corriste en el servidor), esa migración
   **debe correr en pmmutileria/pmmtraf antes o junto con** el deploy del
   `.dll`. Si el código nuevo ya espera una columna/tabla que la migración
   todavía no creó, el endpoint que la usa falla o queda inconsistente en vivo.
   - Ejemplo: `20260928_hash_tokens_enlaces_publicos.sql` — el código de esa
     misma fecha ya busca por `token_hash`; si se publica el `.dll` sin correr
     antes esa migración, los enlaces públicos vigentes de "Seguimiento de
     entrada/revisión" y "Ver reporte" dejan de resolver hasta que se corra.
2. **Reciclar el app pool de IIS** después de copiar los archivos nuevos
   (el proceso viejo sigue corriendo con el código anterior hasta que se
   recicla).
3. Los logs de producción quedan en `C:\inetpub\wwwroot\pmmovil\_api\Logs`
   (ver `Program.cs`, `ResolveLogFilePath`) — revisar ahí si algo falla
   después del deploy.
