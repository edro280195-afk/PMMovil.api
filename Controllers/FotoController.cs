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

    public FotoController(
        BaseRepository db,
        IConfiguration config,
        ILogger<FotoController> logger,
        CasetaPhotoStorage casetaPhotoStorage,
        IModulePermissionService permissionService,
        IHubContext<WarehouseHub> hub)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _basePath = _config["PhotoSettings:BasePath"] ?? "F:\\Trafico\\fotos";
        _casetaPhotoStorage = casetaPhotoStorage;
        _permissionService = permissionService;
        _hub = hub;
    }

    [HttpGet("ver/{id}")]
    [RequiereModulo(MobileModuleIds.Review, "Lectura", true, MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> VerFoto(int id)
    {

        try
        {
            using IDbConnection conn = _db.CreateConnection();
            string sql = @"
				SELECT TRIM(f.creferencia) AS Creferencia,
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
            string? filePath = PhotoFileNameResolver.FindExistingPath(
                _basePath,
                folder,
                cref,
                tipoDocto,
                line,
                ext);
            _logger.LogInformation("VerFoto: ID {id}, Referencia {cref}, Carpeta {folder}, Path {path}", id, cref, folder, filePath ?? fileName);
            if (filePath == null)
            {
                _logger.LogWarning("VerFoto: Archivo no encontrado. Nombre esperado {fileName}", fileName);
                return NotFound(new
                {
                    message = "Archivo de imagen no encontrado en disco",
                    fileName = fileName,
                    path = Path.Combine(_basePath, folder, fileName),
                    basePath = _basePath
                });
            }
            return PhysicalFile(filePath, ext.ToLower() == "png" ? "image/png" : "image/jpeg");
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
            string sql = "SELECT idfotos AS IdFotos, nlineno AS nLineNo, cextension AS cExtension, usucrea AS UsuCrea, DATE_FORMAT(fcrea, '%Y-%m-%d %H:%i:%s') AS fCrea, ntipodocto AS NTipoDocto FROM fotos WHERE creferencia = @cref AND ntipodocto IN (107, 103) ORDER BY nlineno";
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
					   usucrea AS UsuCrea, DATE_FORMAT(fcrea, '%Y-%m-%d %H:%i:%s') AS fCrea
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

    [HttpGet("entrada/ver/{creferencia}/{linea}")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> VerFotoEntrada(string creferencia, int linea)
    {
        return await VerFotoEntradaCore(creferencia, linea, ntipodocto: 100);
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
					   usucrea AS UsuCrea, DATE_FORMAT(fcrea, '%Y-%m-%d %H:%i:%s') AS fCrea
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

    [HttpGet("entrada-danio/ver/{creferencia}/{linea}")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> VerFotoEntradaDanio(string creferencia, int linea)
    {
        return await VerFotoEntradaCore(creferencia, linea, ntipodocto: 103);
    }

    private async Task<IActionResult> VerFotoEntradaCore(string creferencia, int linea, int ntipodocto)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            var data = await conn.QueryFirstOrDefaultAsync(@"
				SELECT f.cextension, f.nlineno,
					   COALESCE(NULLIF(cl.carpeta,''), cl.cidcte) AS carpeta
				FROM fotos f
				JOIN traf t ON t.creferencia = f.creferencia
				JOIN clientes cl ON cl.cidcte = t.cclienteidtraf
				WHERE f.creferencia = @cref AND f.nlineno = @linea AND f.ntipodocto = @ntipodocto
				LIMIT 1", new { cref = creferencia, linea, ntipodocto });

            if (data == null)
                return NotFound(new { message = "Foto no encontrada" });

            string carpeta = ((string)data.carpeta).Trim();
            string ext = PhotoFileNameResolver.NormalizeExtension((string?)data.cextension);
            int nLineNo = (int)data.nlineno;
            string fileName = PhotoFileNameResolver.Build(creferencia, ntipodocto, nLineNo, ext);
            string? filePath = PhotoFileNameResolver.FindExistingPath(
                _basePath,
                carpeta,
                creferencia,
                ntipodocto,
                nLineNo,
                ext);

            if (filePath == null)
                return NotFound(new { message = $"Archivo no encontrado: {Path.Combine(_basePath, carpeta, fileName)}" });

            string contentType = ext.ToLower() == "png" ? "image/png" : "image/jpeg";
            return PhysicalFile(filePath, contentType);
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
                string sqlIns = "INSERT INTO fotos (creferencia, nlineno, ntipodocto, cextension, fcrea, usucrea) VALUES (@cref, @line, @tipo, @ext, NOW(), @usu); SELECT LAST_INSERT_ID();";
                int fotoId = await conn.ExecuteScalarAsync<int>(sqlIns, new
                {
                    cref = creferencia,
                    line = nextLine,
                    tipo = ntipodocto,
                    ext = extension,
                    usu = usuario
                });
                string finalPath = Path.Combine(_basePath, folder);
                if (!Directory.Exists(finalPath))
                {
                    Directory.CreateDirectory(finalPath);
                }
                string fileName = PhotoFileNameResolver.Build(
                    creferencia,
                    ntipodocto,
                    nextLine,
                    extension);
                string fullFilePath = Path.Combine(finalPath, fileName);
                _logger.LogInformation("SubirFoto: Guardando en {path}", fullFilePath);
                await System.IO.File.WriteAllBytesAsync(fullFilePath, imageBytes);
                await NotificarFotosActualizadasAsync(creferencia, usuario);
                return Ok(new
                {
                    success = true,
                    id = fotoId,
                    fileName = fileName
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
            if (puedeBorrarFotoRevision)
            {
                await NotificarFotosActualizadasAsync(cref, username);
            }
            string ext = PhotoFileNameResolver.NormalizeExtension(data.CExtension);
            int line = data.NLineNo;
            foreach (string filePath in PhotoFileNameResolver.GetCandidatePaths(
                         _basePath,
                         folder,
                         cref,
                         ntipodocto,
                         line,
                         ext))
            {
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }
            }
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

    [HttpGet("caseta/ver")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> VerFotoCasetaPorQuery([FromQuery] string casetaId, [FromQuery] int indice)
    {
        return await VerFotoCasetaCore(casetaId, indice);
    }

    [HttpGet("caseta/{casetaId}/{indice}")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> VerFotoCaseta(string casetaId, int indice)
    {
        return await VerFotoCasetaCore(casetaId, indice);
    }

    private async Task<IActionResult> VerFotoCasetaCore(string casetaId, int indice)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(casetaId) || indice <= 0)
                return BadRequest(new { message = "CasetaId e indice son obligatorios" });

            using IDbConnection conn = _db.CreateConnection();
            string? extension = await conn.ExecuteScalarAsync<string?>(@"
				SELECT COALESCE(NULLIF(cextension, ''), 'jpg')
				FROM fotos
				WHERE creferencia = @casetaId AND nlineno = @indice AND ntipodocto = 106
				LIMIT 1", new { casetaId, indice });

            string? filePath = _casetaPhotoStorage.FindReadablePhotoPath(casetaId, indice, extension);
            if (filePath == null)
                return NotFound(new { message = "Foto no encontrada" });

            return PhysicalFile(filePath, GetImageContentType(filePath, extension));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al servir foto de caseta {CasetaId}/{Indice}", casetaId, indice);
            return StatusCode(500, new { message = "Error al leer la imagen" });
        }
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
