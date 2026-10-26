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

## Regla obligatoria de configuración

El despliegue productivo actual funciona con configuración basada en archivos.
El release de la API **debe incluir** estos archivos:

- `appsettings.json`
- `appsettings.Local.json`
- `appsettings.Production.json`
- `web.config`

`Program.cs` carga `appsettings.Local.json` también cuando el entorno es
`Production`, por compatibilidad con el servidor existente. Por eso el ZIP de
la API contiene información sensible: debe copiarse directamente al servidor,
no subirse a Git, no enviarse por canales públicos y no quedar expuesto por
IIS. `appsettings.Development.json` no se incluye.

`ConnectionStrings:PmmutileriaDb` no es obligatoria si `PmmtrafDb` permite que
`BaseRepository` derive la conexión de `pmmutileria`. No agregar una validación
manual que vuelva a exigirla.

## Cómo generar el release (comando oficial)

Desde `C:\Codigos\PMMovil-migracion` ejecutar PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\scripts\publish-pmmovil-api.ps1 -ReleaseSlug "preregistro-config"
```

El script usa `dotnet publish`, verifica que estén los cuatro archivos de
configuración requeridos, elimina cualquier `appsettings.Development.json`,
incluye las migraciones de preregistro como trazabilidad, genera el ZIP y
calcula su SHA-256. No usar un `dotnet publish -o` manual para producción,
porque es fácil olvidar `appsettings.Local.json`.

El artefacto queda en `_release/api/` con el formato:
`PMMovil.Api-release-<fecha>-<slug>.zip`.

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

4. Verificar que el sitio conserve `ASPNETCORE_ENVIRONMENT=Production`, copiar
   el contenido del release al directorio de la API, reciclar el App Pool y
   ejecutar el smoke test de login antes de entregar el cambio.
