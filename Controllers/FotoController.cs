using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Attributes;
using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class FotoController : ControllerBase
{
    private readonly BaseRepository _db;

    private readonly IConfiguration _config;

    private readonly ILogger<FotoController> _logger;

    private readonly string _basePath;

    private readonly CasetaPhotoStorage _casetaPhotoStorage;

    private readonly IModulePermissionService _permissionService;
    private readonly IHubContext<WarehouseHub> _hub;

    // Fotos de Revisión y de Entrada Bodega (carpeta del cliente en el 23): se escriben, leen y borran por aquí.
    private readonly ClientePhotoStorage _clientePhotos;

    public FotoController(
        BaseRepository db,
        IConfiguration config,
        ILogger<FotoController> logger,
        CasetaPhotoStorage casetaPhotoStorage,
        ClientePhotoStorage clientePhotos,
        IModulePermissionService permissionService,
        IHubContext<WarehouseHub> hub)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _basePath = _config["PhotoSettings:BasePath"] ?? "F:\\Trafico\\fotos";
        _casetaPhotoStorage = casetaPhotoStorage;
        _clientePhotos = clientePhotos;
        _permissionService = permissionService;
        _hub = hub;
    }

    /// <summary>
    /// Foto de revisión (107) o de discrepancia (103) por su idfotos. <paramref name="tam"/>: "mini" (cuadrículas),
    /// "vista" (pantalla) o nada para el original; <paramref name="v"/>: la versión que dio la lista (ver <see cref="FotoCache"/>).
    /// </summary>
    [HttpGet("ver/{id}")]
    [RequiereModulo(MobileModuleIds.Review, "Lectura", true, MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> VerFoto(int id, [FromQuery] string? tam = null, [FromQuery] string? v = null)
    {

        try
        {
            using IDbConnection conn = _db.CreateConnection();
            string sql = @"
				SELECT f.idfotos AS IdFotos,
					   CAST(IFNULL(UNIX_TIMESTAMP(f.fcrea), 0) AS SIGNED) AS FcreaUnix,
					   TRIM(f.creferencia) AS Creferencia,
					   f.cextension AS CExtension,
					   f.nlineno AS NLineNo,
					   f.ntipodocto AS NTipoDocto,
					   COALESCE(
						   NULLIF(fa.cclienteidtraf, ''),
						   NULLIF(ff.cclienteid, '')
					   ) AS Cidcte
				FROM fotos f
				LEFT JOIN traf fa ON fa.creferencia = f.creferencia
				LEFT JOIN fact ff
				  ON ff.cnofact = f.creferencia
				  OR ff.cref_agrupada = f.creferencia
				  OR ff.creferencia = f.creferencia
				WHERE f.idfotos = @id
				  AND f.ntipodocto IN (107, 103)
				LIMIT 1";
            // Antes: QueryFirstOrDefaultAsync<object> (dynamic/DapperRow). Igual que
            // en Caseta/Finalizar, eso ya causó lecturas silenciosamente nulas por
            // mayúsculas/minúsculas -- con una clase tipada, Dapper mapea columna→
            // propiedad sin distinguir mayúsculas, así que ya no importa cómo haya
            // quedado el nombre real de la columna en MySQL.
            FotoVerInfo? data = await conn.QueryFirstOrDefaultAsync<FotoVerInfo>(sql, new { id });
            if (data == null)
            {
                return NotFound(new
                {
                    message = "Foto no encontrada en base de datos"
                });
            }
            string cidcte = (data.Cidcte ?? "").Trim();

            string sqlCte = "SELECT COALESCE(NULLIF(carpeta,''), cidcte) FROM clientes WHERE cidcte = @id LIMIT 1";
            string folder = ((await conn.ExecuteScalarAsync<string>(sqlCte, new
            {
                id = cidcte
            })) ?? cidcte).Trim();

            string ext = PhotoFileNameResolver.NormalizeExtension(data.CExtension);
            string cref = data.Creferencia.Trim();
            int line = data.NLineNo;
            int tipoDocto = data.NTipoDocto;
            string fileName = PhotoFileNameResolver.Build(cref, tipoDocto, line, ext);
            var foto = new FotoCliente(data.IdFotos > 0 ? data.IdFotos : id, folder, cref, tipoDocto, line, ext);
            return await ServirFotoClienteAsync(foto, data.FcreaUnix, tam, v, () =>
            {
                _logger.LogWarning("VerFoto: Archivo no encontrado. ID {id}, carpeta {folder}, nombre esperado {fileName}", id, folder, fileName);
                return NotFound(new
                {
                    message = "Archivo de imagen no encontrado en disco",
                    fileName = fileName,
                    path = Path.Combine(_basePath, folder, fileName),
                    basePath = _basePath
                });
            });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en VerFoto {id}", id);
            return StatusCode(500, new
            {
                message = "Error al leer la imagen"
            });
        }
    }

    [HttpGet("{creferencia}")]
    [RequiereModulo(MobileModuleIds.Review, "Lectura", true, MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetFotos(string creferencia)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            // ntipodocto IN (107, 103): 107 = evidencia normal de revisión,
            // 103 = evidencia de discrepancia (mismo tipo que daño de Entrada
            // Bodega). Se traen juntas; el cliente las distingue por NTipoDocto.
            string sql = "SELECT idfotos AS IdFotos, nlineno AS nLineNo, cextension AS cExtension, usucrea AS UsuCrea, DATE_FORMAT(fcrea, '%Y-%m-%d %H:%i:%s') AS fCrea, CAST(IFNULL(UNIX_TIMESTAMP(fcrea), 0) AS SIGNED) AS FcreaUnix, ntipodocto AS NTipoDocto FROM fotos WHERE creferencia = @cref AND ntipodocto IN (107, 103) ORDER BY nlineno";
            return Ok(new
            {
                success = true,
                items = await conn.QueryAsync<FotoRevision>(sql, new
                {
                    cref = creferencia
                })
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetFotos para {cref}", creferencia);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al obtener lista de fotos"
            });
        }
    }

    [HttpGet("entrada/{creferencia}")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> GetFotosEntrada(string creferencia)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            string sql = @"
				SELECT idfotos AS IdFotos, nlineno AS nLineNo, cextension AS cExtension,
					   usucrea AS UsuCrea, DATE_FORMAT(fcrea, '%Y-%m-%d %H:%i:%s') AS fCrea,
					   CAST(IFNULL(UNIX_TIMESTAMP(fcrea), 0) AS SIGNED) AS FcreaUnix
				FROM fotos
				WHERE creferencia = @cref AND ntipodocto = 100
				ORDER BY nlineno";
            var items = await conn.QueryAsync<FotoRevision>(sql, new { cref = creferencia });
            return Ok(new { success = true, items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al listar fotos de entrada para {Cref}", creferencia);
            return Ok(new { success = false, message = "Error al obtener fotos" });
        }
    }

    /// <summary>
    /// Foto de evidencia de Entrada Bodega (100). <paramref name="tam"/>: "mini", "vista" o nada para el original;
    /// <paramref name="v"/>: la versión que dio la lista. Ojo: la línea se reusa (borrar la última y tomar otra), por eso
    /// la versión lleva el idfotos y sin ella la tableta pregunta cada vez si cambió.
    /// </summary>
    [HttpGet("entrada/ver/{creferencia}/{linea}")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> VerFotoEntrada(
        string creferencia,
        int linea,
        [FromQuery] string? tam = null,
        [FromQuery] string? v = null)
    {
        return await VerFotoEntradaCore(creferencia, linea, ntipodocto: 100, tam, v);
    }

    [HttpGet("entrada-danio/{creferencia}")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> GetFotosEntradaDanio(string creferencia)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            string sql = @"
				SELECT idfotos AS IdFotos, nlineno AS nLineNo, cextension AS cExtension,
					   usucrea AS UsuCrea, DATE_FORMAT(fcrea, '%Y-%m-%d %H:%i:%s') AS fCrea,
					   CAST(IFNULL(UNIX_TIMESTAMP(fcrea), 0) AS SIGNED) AS FcreaUnix
				FROM fotos
				WHERE creferencia = @cref AND ntipodocto = 103
				ORDER BY nlineno";
            var items = await conn.QueryAsync<FotoRevision>(sql, new { cref = creferencia });
            return Ok(new { success = true, items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al listar fotos de daño de entrada para {Cref}", creferencia);
            return Ok(new { success = false, message = "Error al obtener fotos de daño" });
        }
    }

    /// <summary>Foto de daño de Entrada Bodega (103). Mismos <paramref name="tam"/> y <paramref name="v"/> que la de evidencia.</summary>
    [HttpGet("entrada-danio/ver/{creferencia}/{linea}")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> VerFotoEntradaDanio(
        string creferencia,
        int linea,
        [FromQuery] string? tam = null,
        [FromQuery] string? v = null)
    {
        return await VerFotoEntradaCore(creferencia, linea, ntipodocto: 103, tam, v);
    }

    private async Task<IActionResult> VerFotoEntradaCore(string creferencia, int linea, int ntipodocto, string? tam, string? v)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            // La más reciente si hubiera dos filas con la misma línea (su archivo en el 23 es el mismo).
            FotoEntradaFila? data = await conn.QueryFirstOrDefaultAsync<FotoEntradaFila>(@"
				SELECT f.idfotos AS IdFotos,
					   CAST(IFNULL(UNIX_TIMESTAMP(f.fcrea), 0) AS SIGNED) AS FcreaUnix,
					   f.cextension AS CExtension,
					   f.nlineno AS NLineNo,
					   COALESCE(NULLIF(cl.carpeta,''), cl.cidcte) AS Carpeta
				FROM fotos f
				JOIN traf t ON t.creferencia = f.creferencia
				JOIN clientes cl ON cl.cidcte = t.cclienteidtraf
				WHERE f.creferencia = @cref AND f.nlineno = @linea AND f.ntipodocto = @ntipodocto
				ORDER BY f.idfotos DESC
				LIMIT 1", new { cref = creferencia, linea, ntipodocto });

            if (data == null)
                return NotFound(new { message = "Foto no encontrada" });

            string carpeta = (data.Carpeta ?? string.Empty).Trim();
            string ext = PhotoFileNameResolver.NormalizeExtension(data.CExtension);
            string fileName = PhotoFileNameResolver.Build(creferencia, ntipodocto, data.NLineNo, ext);
            var foto = new FotoCliente(data.IdFotos, carpeta, creferencia, ntipodocto, data.NLineNo, ext);
            return await ServirFotoClienteAsync(foto, data.FcreaUnix, tam, v,
                () => NotFound(new { message = $"Archivo no encontrado: {Path.Combine(_basePath, carpeta, fileName)}" }));
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al servir foto de entrada {Cref}/{Linea} (tipo {Tipo})", creferencia, linea, ntipodocto);
            return StatusCode(500, new { message = "Error al leer la imagen" });
        }
    }

    [HttpPost("subir")]
    [RequiereModulo(MobileModuleIds.Review, "Agregar")]
    public async Task<IActionResult> SubirFoto([FromBody] SubirFotoRequest payload)
    {
        try
        {
            string creferencia = (payload.creferencia ?? "").Trim();
            string cclienteid = payload.cclienteid;
            string base64 = payload.image;
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            string extRaw = (payload.extension ?? ".jpg").ToLowerInvariant().TrimStart('.');
            HashSet<string> allowed = new HashSet<string> { "jpg", "jpeg", "png", "webp" };
            if (!allowed.Contains(extRaw))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Tipo de archivo no permitido"
                });
            }
            string extension = PhotoFileNameResolver.NormalizeExtension(extRaw);
            if (string.IsNullOrEmpty(base64))
            {
                return BadRequest("No se recibió imagen");
            }
            if (base64.Contains(","))
            {
                base64 = base64.Split(',')[1];
            }
            byte[] imageBytes = Convert.FromBase64String(base64);
            using IDbConnection conn = _db.CreateConnection();
            conn.Open();
            string lockName = $"lock_foto_{creferencia}";
            var lockAcquired = await conn.ExecuteScalarAsync<int>("SELECT GET_LOCK(@lockName, 10)", new { lockName });
            if (lockAcquired != 1)
            {
                return BadRequest(new { success = false, message = "No se pudo obtener el bloqueo para subir la fotografía. Intente de nuevo." });
            }
            try
            {
                // 103 = evidencia de discrepancia (mismo ntipodocto que daño de
                // Entrada Bodega), 107 = evidencia normal de revisión.
                int ntipodocto = payload.EsDiscrepancia ? 103 : 107;
                string sqlCte = "SELECT COALESCE(NULLIF(carpeta,''), cidcte) FROM clientes WHERE cidcte = @id LIMIT 1";
                string folder = ((await conn.ExecuteScalarAsync<string>(sqlCte, new
                {
                    id = cclienteid
                })) ?? cclienteid).Trim();
                string sqlMax = "SELECT COALESCE(MAX(nlineno), 0) + 1 FROM fotos WHERE creferencia = @cref AND ntipodocto = @tipo";
                int nextLine = await conn.ExecuteScalarAsync<int>(sqlMax, new
                {
                    cref = creferencia,
                    tipo = ntipodocto
                });

                // Primero la foto (en la cola local: milisegundos, ya no la copia lenta al 23 dentro de la petición) y luego
                // su fila; si la fila no entra, la foto se descarta. La copia al 23 y la mejora corren en segundo plano.
                FotoClientePreparada preparada = await _clientePhotos.PrepararAsync(
                    imageBytes, folder, creferencia, ntipodocto, nextLine, extension, HttpContext.RequestAborted);
                int fotoId;
                try
                {
                    string sqlIns = "INSERT INTO fotos (creferencia, nlineno, ntipodocto, cextension, fcrea, usucrea) VALUES (@cref, @line, @tipo, @ext, NOW(), @usu); SELECT LAST_INSERT_ID();";
                    fotoId = await conn.ExecuteScalarAsync<int>(sqlIns, new
                    {
                        cref = creferencia,
                        line = nextLine,
                        tipo = ntipodocto,
                        ext = extension,
                        usu = usuario
                    });
                }
                catch
                {
                    _clientePhotos.Descartar(preparada);
                    throw;
                }

                _clientePhotos.Activar(preparada, fotoId);
                await NotificarFotosActualizadasAsync(creferencia, usuario);
                return Ok(new
                {
                    success = true,
                    id = fotoId,
                    fileName = preparada.Archivo
                });
            }
            finally
            {
                await conn.ExecuteAsync("SELECT RELEASE_LOCK(@lockName)", new { lockName });
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en SubirFoto");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al guardar la fotografía"
            });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> EliminarFoto(int id)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            conn.Open();
            string sqlData = @"
				SELECT f.creferencia AS Creferencia, f.cextension AS CExtension,
					   f.nlineno AS NLineNo, f.ntipodocto AS NTipoDocto,
					   COALESCE(
						   NULLIF(fa.cclienteidtraf, ''),
						   NULLIF(ff.cclienteid, '')
					   ) AS Cidcte
				FROM fotos f
				LEFT JOIN traf fa ON fa.creferencia = f.creferencia
				LEFT JOIN fact ff
				  ON ff.cnofact = f.creferencia
				  OR ff.cref_agrupada = f.creferencia
				  OR ff.creferencia = f.creferencia
				WHERE f.idfotos = @id LIMIT 1";
            // Misma clase tipada que VerFoto -- ver comentario ahí sobre por qué ya
            // no se usa dynamic/QueryFirstOrDefaultAsync<object>.
            FotoVerInfo? data = await conn.QueryFirstOrDefaultAsync<FotoVerInfo>(sqlData, new { id });
            if (data == null)
            {
                return NotFound(new { success = false, message = "Foto no encontrada" });
            }
            int ntipodocto = data.NTipoDocto == 0 ? 100 : data.NTipoDocto;
            var username = User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(username))
                return Unauthorized(new { success = false, message = "No se pudo identificar al usuario." });

            bool puedeBorrarFotoRevision = (ntipodocto == 107 || ntipodocto == 103)
                && await _permissionService.HasPermissionAsync(
                    username, MobileModuleIds.Review, "Borrar", HttpContext.RequestAborted);
            // ntipodocto=103 lo escriben DOS flujos distintos que comparten el
            // mismo apartado en el PMM/FoxPro: daño de Entrada Bodega y
            // discrepancia de Revisión. No hay forma de saber, solo con la fila
            // de "fotos", cuál de los dos la subió (comparten hasta la misma
            // creferencia para un mismo embarque) — así que para borrar basta
            // con tener el permiso de Borrar de CUALQUIERA de los dos módulos.
            // 107 y el resto de tipos siguen con su único módulo de siempre.
            bool canDelete;
            if (ntipodocto == 107)
            {
                canDelete = puedeBorrarFotoRevision;
            }
            else if (ntipodocto == 103)
            {
                canDelete = puedeBorrarFotoRevision
                    || await _permissionService.HasPermissionAsync(
                    username, MobileModuleIds.WarehouseOperations, "Borrar", HttpContext.RequestAborted);
            }
            else
            {
                canDelete = await _permissionService.HasPermissionAsync(
                    username, MobileModuleIds.WarehouseOperations, "Borrar", HttpContext.RequestAborted);
            }
            if (!canDelete) return Forbid();

            string sqlCte = "SELECT COALESCE(NULLIF(carpeta,''), cidcte) FROM clientes WHERE cidcte = @id LIMIT 1";
            string folder = ((await conn.ExecuteScalarAsync<string>(sqlCte, new
            {
                id = data.Cidcte
            })) ?? data.Cidcte).Trim();
            string sqlDel = "DELETE FROM fotos WHERE idfotos = @id";
            await conn.ExecuteAsync(sqlDel, new { id });
            string cref = data.Creferencia.Trim();
            if (ntipodocto == 103)
            {
                // Foto de daño (103): baja en uno lo que el enlace de la entrada esperaba, para que no diga «faltan N» por
                // una foto eliminada a propósito (tolerante a que falte el script SQL opcional).
                await EntradaDanioSeguimiento.AjustarAlBorrarFotoAsync(_db, _logger, cref);
            }
            if (puedeBorrarFotoRevision)
            {
                await NotificarFotosActualizadasAsync(cref, username);
            }
            // Lo local se quita en el acto y la copia del 23 también; si el 23 no contesta, el almacén lo reintenta solo.
            string ext = PhotoFileNameResolver.NormalizeExtension(data.CExtension);
            _clientePhotos.Borrar(new FotoCliente(id, folder, cref, ntipodocto, data.NLineNo, ext));
            return Ok(new { success = true });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en EliminarFoto {id}", id);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al eliminar la fotografía"
            });
        }
    }

    private async Task NotificarFotosActualizadasAsync(string creferencia, string usuario)
    {
        await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync(
            "onRevisionActualizada",
            new WarehouseHub.RevisionActualizadaEvent
            {
                Entidad = "FOTOS",
                Creferencia = creferencia,
                Usuario = usuario,
                Fecha = DateTime.UtcNow.ToString("O")
            });
    }

    /// <summary>
    /// Foto de entrada a caseta. <paramref name="tam"/>: "mini" (cuadrículas, ~14 KB), "vista" (pantalla, ~130 KB) o
    /// nada para el original. <paramref name="v"/>: la versión que dio el detalle; con la vigente, la tableta la guarda
    /// para siempre (ver <see cref="FotoCache"/>).
    /// </summary>
    [HttpGet("caseta/ver")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> VerFotoCasetaPorQuery(
        [FromQuery] string casetaId,
        [FromQuery] int indice,
        [FromQuery] string? tam = null,
        [FromQuery] string? v = null)
    {
        return await VerFotoCasetaCore(casetaId, indice, tam, v);
    }

    [HttpGet("caseta/{casetaId}/{indice}")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> VerFotoCaseta(
        string casetaId,
        int indice,
        [FromQuery] string? tam = null,
        [FromQuery] string? v = null)
    {
        return await VerFotoCasetaCore(casetaId, indice, tam, v);
    }

    private async Task<IActionResult> VerFotoCasetaCore(string casetaId, int indice, string? tam, string? v)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(casetaId) || indice <= 0)
                return BadRequest(new { message = "CasetaId e indice son obligatorios" });

            using IDbConnection conn = _db.CreateConnection();
            CasetaFotoFila? fila = await conn.QueryFirstOrDefaultAsync<CasetaFotoFila>(@"
				SELECT COALESCE(NULLIF(cextension, ''), 'jpg') AS Extension,
				       idfotos AS IdFotos,
				       CAST(IFNULL(UNIX_TIMESTAMP(fcrea), 0) AS SIGNED) AS FcreaUnix
				FROM fotos
				WHERE creferencia = @casetaId AND nlineno = @indice AND ntipodocto = 106
				ORDER BY idfotos DESC
				LIMIT 1", new { casetaId, indice });

            FotoTamano tamano = FotoCache.Tamano(tam);
            CancellationToken ct = HttpContext.RequestAborted;
            FotoArchivo? archivo = await _casetaPhotoStorage.ResolverAsync(casetaId, indice, fila?.Extension, tamano, ct);
            // Si la limpieza o una publicación lo movió entre la búsqueda y ahora, se vuelve a buscar una vez.
            if (archivo != null && !System.IO.File.Exists(archivo.Ruta))
                archivo = await _casetaPhotoStorage.ResolverAsync(casetaId, indice, fila?.Extension, tamano, ct);
            if (archivo == null)
                return NotFound(new { message = "Foto no encontrada" });

            string version = fila == null ? string.Empty : FotoCache.Version(fila.IdFotos, fila.FcreaUnix);
            return ServirArchivo(archivo, version, v, tamano, fila?.Extension);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al servir foto de caseta {CasetaId}/{Indice}", casetaId, indice);
            return StatusCode(500, new { message = "Error al leer la imagen" });
        }
    }

    /// <summary>
    /// Sirve una foto de la carpeta del cliente (Revisión o Entrada Bodega) desde el almacén: la copia local si la hay
    /// (recién subida o consultada en las últimas 48 h), si no la del 23, y en el tamaño pedido.
    /// </summary>
    private async Task<IActionResult> ServirFotoClienteAsync(
        FotoCliente foto,
        long fcreaUnix,
        string? tam,
        string? v,
        Func<IActionResult> noEncontrada)
    {
        FotoTamano tamano = FotoCache.Tamano(tam);
        CancellationToken ct = HttpContext.RequestAborted;
        FotoArchivo? archivo = await _clientePhotos.ResolverAsync(foto, tamano, ct);
        // Si la limpieza o una publicación lo movió entre la búsqueda y ahora, se vuelve a buscar una vez.
        if (archivo != null && !System.IO.File.Exists(archivo.Ruta))
            archivo = await _clientePhotos.ResolverAsync(foto, tamano, ct);
        if (archivo == null)
            return noEncontrada();

        return ServirArchivo(archivo, FotoCache.Version(foto.IdFotos, fcreaUnix), v, tamano, foto.Extension);
    }

    /// <summary>
    /// Con la versión vigente en la dirección la tableta guarda la foto para siempre; sin ella (o con otra) pregunta cada vez
    /// y la etiqueta (ETag) le contesta «no cambió» con unos cuantos bytes. Ver <see cref="FotoCache"/>.
    /// </summary>
    private IActionResult ServirArchivo(FotoArchivo archivo, string version, string? versionPedida, FotoTamano tamano, string? extension)
    {
        Response.Headers.CacheControl = FotoCache.CacheControl(versionPedida, version);
        var etiqueta = string.IsNullOrEmpty(version)
            ? null
            : new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{version}-{tamano.ToString().ToLowerInvariant()}\"");

        return PhysicalFile(
            archivo.Ruta,
            archivo.EsVariante ? "image/jpeg" : GetImageContentType(archivo.Ruta, extension),
            new DateTimeOffset(DateTime.SpecifyKind(archivo.ModificadaUtc, DateTimeKind.Utc)),
            etiqueta);
    }

    private sealed class CasetaFotoFila
    {
        public string Extension { get; set; } = "jpg";
        public long IdFotos { get; set; }
        public long FcreaUnix { get; set; }
    }

    private sealed class FotoEntradaFila
    {
        public long IdFotos { get; set; }
        public long FcreaUnix { get; set; }
        public string? CExtension { get; set; }
        public int NLineNo { get; set; }
        public string? Carpeta { get; set; }
    }

    private static string GetImageContentType(string path, string? extension)
    {
        string ext = (extension ?? Path.GetExtension(path)).Trim().TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "png" => "image/png",
            "webp" => "image/webp",
            "gif" => "image/gif",
            _ => "image/jpeg"
        };
    }

    private static string ExtractRef4(string referencia)
    {
        if (string.IsNullOrWhiteSpace(referencia))
            return "";

        string[] parts = referencia.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4)
            return parts[3];

        string clean = referencia.Trim();
        if (clean.Length > 9 && clean.StartsWith("01C", StringComparison.OrdinalIgnoreCase))
            return clean[5..^4];

        string digits = Regex.Replace(clean, @"\D", "");
        return digits.Length > 8 ? digits[4..^4] : "";
    }
}
