using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Dapper;
using PMMovil.Api.Data;
using PMMovil.Api.Models;
using System.Data;

namespace PMMovil.Api.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class FotoController : ControllerBase
    {
        private readonly BaseRepository _db;
        private readonly IConfiguration _config;
        private readonly ILogger<FotoController> _logger;
        private readonly string _basePath;

        public FotoController(BaseRepository db, IConfiguration config, ILogger<FotoController> logger)
        {
            _db = db;
            _config = config;
            _logger = logger;
            _basePath = _config["PhotoSettings:BasePath"] ?? @"F:\Trafico\fotos";
        }

        /// <summary>
        /// Sirve la imagen de una foto directamente como archivo (JPEG/PNG).
        /// El cliente Angular la carga con HttpClient (Bearer token) y crea un Blob URL.
        /// </summary>
        [HttpGet("ver/{id}")]
        public async Task<IActionResult> VerFoto(int id)
        {
            try
            {
                using var conn = _db.CreateConnection();
                string sql = @"
                    SELECT f.creferencia, f.cextension,
                           COALESCE(c.carpeta, c.cidcte) AS Carpeta
                    FROM fotos f
                    LEFT JOIN fact fa ON fa.cref_agrupada = f.creferencia
                    LEFT JOIN clientes c ON c.cidcte = fa.cclienteid
                    WHERE f.idfotos = @id AND f.ntipodocto = 107 LIMIT 1";

                var data = await conn.QueryFirstOrDefaultAsync<dynamic>(sql, new { id });
                if (data == null) return NotFound(new { message = "Foto no encontrada en base de datos" });

                string ext = ((string)(data.cextension ?? "jpg")).TrimStart('.');
                string carpeta = data.Carpeta ?? "";
                string filePath = Path.Combine(_basePath, carpeta, (string)data.creferencia, $"{id}.{ext}");

                if (!System.IO.File.Exists(filePath))
                    return NotFound(new { message = "Archivo de imagen no encontrado en disco" });

                string mimeType = ext.ToLower() == "png" ? "image/png" : "image/jpeg";
                var bytes = await System.IO.File.ReadAllBytesAsync(filePath);
                return File(bytes, mimeType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en VerFoto {id}", id);
                return StatusCode(500, new { message = "Error al leer la imagen" });
            }
        }

        /// <summary>
        /// Obtiene el listado de fotos registradas para una referencia.
        /// </summary>
        [HttpGet("{creferencia}")]
        public async Task<IActionResult> GetFotos(string creferencia)
        {
            try
            {
                using var conn = _db.CreateConnection();
                string sql = @"
                    SELECT idfotos AS IdFotos, nlineno AS nLineNo, cextension AS cExtension, 
                           usubaja AS UsuCrea, DATE_FORMAT(fcrea, '%Y-%m-%d %H:%i:%s') AS fCrea
                    FROM fotos 
                    WHERE creferencia = @cref AND ntipodocto = 107 AND (usubaja IS NULL OR usubaja = '')
                    ORDER BY nlineno";

                var items = await conn.QueryAsync<FotoRevision>(sql, new { cref = creferencia });
                return Ok(new { success = true, items });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en GetFotos para {cref}", creferencia);
                return StatusCode(500, new { success = false, message = "Error al obtener lista de fotos" });
            }
        }

        /// <summary>
        /// Sube una fotografía al servidor. Recibe la imagen en Base64.
        /// </summary>
        [HttpPost("subir")]
        public async Task<IActionResult> SubirFoto([FromBody] dynamic payload)
        {
            try
            {
                string creferencia = payload.creferencia;
                string cclienteid = payload.cclienteid;
                string base64 = payload.image; // El prefijo 'data:image/...' es opcional
                string extension = payload.extension ?? ".jpg";
                string usuario = User.Identity?.Name ?? "Desconocido";

                if (string.IsNullOrEmpty(base64)) return BadRequest("No se recibió imagen");

                // Limpiar base64 si trae el header de Data URI
                if (base64.Contains(",")) base64 = base64.Split(',')[1];
                byte[] imageBytes = Convert.FromBase64String(base64);

                using var conn = _db.CreateConnection();
                conn.Open();

                // 1. Obtener la carpeta del cliente (Cimiento: Mirror mejorado de DbHelper)
                string sqlCte = "SELECT COALESCE(carpeta, cidcte) FROM clientes WHERE cidcte = @id LIMIT 1";
                string folder = await conn.ExecuteScalarAsync<string>(sqlCte, new { id = cclienteid }) ?? cclienteid;

                // 2. Determinar el siguiente número de línea (nlineno)
                string sqlMax = "SELECT COALESCE(MAX(nlineno), 0) + 1 FROM fotos WHERE creferencia = @cref AND ntipodocto = 107";
                int nextLine = await conn.ExecuteScalarAsync<int>(sqlMax, new { cref = creferencia });

                // 3. Registrar en base de datos primero para obtener el ID único
                string sqlIns = @"
                    INSERT INTO fotos (creferencia, nlineno, ntipodocto, cextension, fcrea, usumodi, usubaja) 
                    VALUES (@cref, @line, 107, @ext, NOW(), @usu, '');
                    SELECT LAST_INSERT_ID();";

                int fotoId = await conn.ExecuteScalarAsync<int>(sqlIns, new { 
                    cref = creferencia, line = nextLine, ext = extension, usu = usuario 
                });

                // 4. Guardar archivo físicamente
                string finalPath = Path.Combine(_basePath, folder, creferencia);
                if (!Directory.Exists(finalPath)) Directory.CreateDirectory(finalPath);

                string fileName = $"{fotoId}{extension}";
                string fullFilePath = Path.Combine(finalPath, fileName);
                
                await System.IO.File.WriteAllBytesAsync(fullFilePath, imageBytes);

                return Ok(new { success = true, id = fotoId, fileName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en SubirFoto");
                return StatusCode(500, new { success = false, message = "Error al guardar la fotografía" });
            }
        }

        /// <summary>
        /// Elimina una fotografía tanto de la base de datos como del disco físico.
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> EliminarFoto(int id)
        {
            try
            {
                string usuario = User.Identity?.Name ?? "Desconocido";
                using var conn = _db.CreateConnection();
                conn.Open();

                // 1. Obtener datos de la foto antes de borrarla para saber qué archivo eliminar
                string sqlData = @"
                    SELECT f.creferencia, f.cextension, c.carpeta, c.cidcte
                    FROM fotos f
                    INNER JOIN fact fa ON fa.creferencia = f.creferencia
                    INNER JOIN clientes c ON c.cidcte = fa.cclienteid
                    WHERE f.idfotos = @id LIMIT 1";
                
                var data = await conn.QueryFirstOrDefaultAsync<dynamic>(sqlData, new { id });

                if (data == null) return NotFound("Foto no encontrada");

                // 2. Eliminar de la base de datos (Usamos baja lógica o física según prefieras, WebForms hacía física)
                string sqlDel = "DELETE FROM fotos WHERE idfotos = @id";
                await conn.ExecuteAsync(sqlDel, new { id });

                // 3. Intentar eliminar del disco
                string folder = data.carpeta ?? data.cidcte;
                string filePath = Path.Combine(_basePath, folder, data.creferencia, $"{id}{data.cextension}");

                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en EliminarFoto {id}", id);
                return StatusCode(500, new { success = false, message = "Error al eliminar la fotografía" });
            }
        }
    }
}
