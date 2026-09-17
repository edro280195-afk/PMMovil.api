using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Attributes;
using PMMovil.Api.Data;

using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.Zip;

namespace PMMovil.Api.Controllers;

[Authorize]
[RequiereModulo(MobileModuleIds.Review)]
[ApiController]
[Route("api/[controller]")]
public class FacturaPdfController : ControllerBase
{
    private sealed record DocumentoLocalizado(string RutaContenedor, TipoContenedor Tipo, string? NombreEntradaInterna);

    private enum TipoContenedor { Pdf, Zip, Rar }

    private sealed class DocumentoEntradaRow
    {
        public string? Creferencia { get; set; }
        public string? CrefAgrupada { get; set; }
        public string? Cclienteid { get; set; }
        public string? Carpeta { get; set; }
        public string? NombreCliente { get; set; }
    }

    private readonly BaseRepository _db;
    private readonly IConfiguration _config;
    private readonly ILogger<FacturaPdfController> _logger;
    private readonly IMemoryCache _cache;
    private readonly string _basePath;

    private const string SuffixFactura = "-015";
    private const string SuffixBl = "-050";
    private const string LegacySuffixBl = "-016";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    public FacturaPdfController(
   BaseRepository db,
   IConfiguration config,
   ILogger<FacturaPdfController> logger,
   IMemoryCache cache)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _cache = cache;
        _basePath = _config["DocumentSettings:BasePath"]
      ?? "\\\\192.168.10.23\\Programas\\Trafico\\documento";
    }

    [HttpGet("entrada/{crefAgrupada}")]
    public async Task<IActionResult> GetDocumentosEntrada(string crefAgrupada)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            List<DocumentoEntradaRow> rows = (await conn.QueryAsync<DocumentoEntradaRow>(@"
  SELECT DISTINCT
 a.creferencia   AS Creferencia,
 a.cref_agrupada AS CrefAgrupada,
 f.cclienteidtraf     AS Cclienteid,
 COALESCE(c.carpeta, c.cidcte) AS Carpeta,
 c.cnombre   AS NombreCliente
  FROM agrupa a
  LEFT JOIN traf f ON f.creferencia = a.creferencia
  LEFT JOIN clientes c  ON c.cidcte = f.cclienteidtraf
  WHERE a.cref_agrupada = @cref
  ORDER BY a.creferencia", new { cref = crefAgrupada })).ToList();

            if (rows.Count == 0)
            {
                rows = (await conn.QueryAsync<DocumentoEntradaRow>(@"
 SELECT DISTINCT
 f.creferencia   AS Creferencia,
 @cref            AS CrefAgrupada,
 f.cclienteidtraf     AS Cclienteid,
 COALESCE(c.carpeta, c.cidcte) AS Carpeta,
 c.cnombre   AS NombreCliente
 FROM traf f
 LEFT JOIN clientes c ON c.cidcte = f.cclienteidtraf
 WHERE f.creferencia = @cref
 ORDER BY f.creferencia", new { cref = crefAgrupada })).ToList();
            }

            List<object> items = new List<object>();
            foreach (DocumentoEntradaRow r in rows)
            {
                string?[] referencias = { r.Creferencia, r.CrefAgrupada };
                DocumentoLocalizado? doc15 = LocalizarDocumentoPorReferencias(
                    r.Carpeta ?? "",
                    referencias,
                    SuffixFactura);
                if (doc15 != null)
                {
                    items.Add(new
                    {
                        creferencia = r.Creferencia,
                        crefAgrupada = r.CrefAgrupada,
                        cclienteid = r.Cclienteid,
                        carpeta = r.Carpeta,
                        nombreCliente = r.NombreCliente,
                        tipo = 15,
                        label = r.Creferencia + " (Factura)",
                        disponible = true
                    });
                }

                DocumentoLocalizado? doc16 = LocalizarDocumentoPorReferencias(
                    r.Carpeta ?? "",
                    referencias,
                    SuffixBl,
                    LegacySuffixBl);
                if (doc16 != null)
                {
                    items.Add(new
                    {
                        creferencia = r.Creferencia,
                        crefAgrupada = r.CrefAgrupada,
                        cclienteid = r.Cclienteid,
                        carpeta = r.Carpeta,
                        nombreCliente = r.NombreCliente,
                        tipo = 16,
                        label = (r.CrefAgrupada ?? r.Creferencia) + " (BL)",
                        disponible = true
                    });
                }

                if (doc15 == null && doc16 == null)
                {
                    DocumentoLocalizado? docGeneric = LocalizarDocumentoPorReferencias(
                        r.Carpeta ?? "",
                        referencias,
                        "");
                    items.Add(new
                    {
                        creferencia = r.Creferencia,
                        crefAgrupada = r.CrefAgrupada,
                        cclienteid = r.Cclienteid,
                        carpeta = r.Carpeta,
                        nombreCliente = r.NombreCliente,
                        tipo = 15,
                        label = r.Creferencia,
                        disponible = docGeneric != null
                    });
                }
            }

            return Ok(new { success = true, items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listando documentos para {cref}", crefAgrupada);
            return StatusCode(500, new { success = false, message = "Error al listar documentos" });
        }
    }

    [HttpGet("ver/{creferencia}")]
    public async Task<IActionResult> VerDocumento(string creferencia, [FromQuery] int? tipo)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            DocumentoEntradaRow? documentRow = await conn.QueryFirstOrDefaultAsync<DocumentoEntradaRow>(@"
  SELECT f.creferencia AS Creferencia,
         a.cref_agrupada AS CrefAgrupada,
         COALESCE(c.carpeta, c.cidcte) AS Carpeta
  FROM traf f
  LEFT JOIN clientes c ON c.cidcte = f.cclienteidtraf
  LEFT JOIN agrupa a ON a.creferencia = f.creferencia
  WHERE f.creferencia = @cref OR a.cref_agrupada = @cref
  ORDER BY CASE WHEN f.creferencia = @cref THEN 0 ELSE 1 END
  LIMIT 1", new { cref = creferencia });

            string? carpeta = documentRow?.Carpeta;
            if (string.IsNullOrWhiteSpace(carpeta))
                return NotFound(new { message = "Cliente/carpeta no encontrado" });

            string documentReference = documentRow?.Creferencia ?? creferencia;
            string?[] referencias = {
                documentReference,
                documentRow?.CrefAgrupada,
                creferencia
            };
            string suffix = (tipo == 16) ? SuffixBl : SuffixFactura;
            string[] suffixes = tipo == 16
                ? new[] { SuffixBl, LegacySuffixBl }
                : new[] { SuffixFactura };
            DocumentoLocalizado? doc = LocalizarDocumentoPorReferencias(
                carpeta,
                referencias,
                suffixes);

            if (doc == null && !tipo.HasValue)
            {
                doc = LocalizarDocumentoPorReferencias(
                    carpeta,
                    referencias,
                    SuffixBl,
                    LegacySuffixBl);
                if (doc != null) suffix = SuffixBl;
            }

            if (doc == null)
                return NotFound(new { message = $"PDF no encontrado: {documentReference}{suffix}.pdf" });

            string filePath = await ObtenerRutaPdfAsync(doc, suffix, documentReference);
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                return NotFound(new { message = "El archivo PDF no se pudo leer" });

            var fileInfo = new FileInfo(filePath);
            Response.Headers.Append("Accept-Ranges", "bytes");
            Response.Headers.Append("ETag", $"\"{fileInfo.LastWriteTimeUtc.Ticks:X}\"");
            Response.Headers.Append("Content-Disposition", $"inline; filename=\"{documentReference}{suffix}.pdf\"");

            return PhysicalFile(filePath, "application/pdf", enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sirviendo documento {cref}", creferencia);
            return StatusCode(500, new { message = "Error al leer el documento" });
        }
    }

    private DocumentoLocalizado? LocalizarDocumento(string carpeta, string creferencia, string suffix)
    {
        if (string.IsNullOrWhiteSpace(carpeta) || string.IsNullOrWhiteSpace(creferencia))
            return null;

        string dir = Path.Combine(_basePath, carpeta);
        if (!Directory.Exists(dir)) return null;

        string pdfDirecto = Path.Combine(dir, creferencia + suffix + ".pdf");
        if (System.IO.File.Exists(pdfDirecto))
            return new DocumentoLocalizado(pdfDirecto, TipoContenedor.Pdf, null);

        if (suffix == SuffixFactura)
        {
            string pdfAlt = Path.Combine(dir, creferencia + ".pdf");
            if (System.IO.File.Exists(pdfAlt))
                return new DocumentoLocalizado(pdfAlt, TipoContenedor.Pdf, null);
        }

        string[] candidatosZip = {
   Path.Combine(dir, creferencia + suffix + ".zip"),
   Path.Combine(dir, creferencia + ".zip")
   };
        foreach (string c in candidatosZip)
            if (System.IO.File.Exists(c))
                return new DocumentoLocalizado(c, TipoContenedor.Zip, null);

        string[] candidatosRar = {
   Path.Combine(dir, creferencia + suffix + ".rar"),
   Path.Combine(dir, creferencia + ".rar")
   };
        foreach (string c in candidatosRar)
            if (System.IO.File.Exists(c))
                return new DocumentoLocalizado(c, TipoContenedor.Rar, null);

        return null;
    }

    private DocumentoLocalizado? LocalizarDocumentoPorReferencias(
        string carpeta,
        IEnumerable<string?> referencias,
        params string[] suffixes)
    {
        foreach (string referencia in referencias
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string suffix in suffixes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                DocumentoLocalizado? documento = LocalizarDocumento(carpeta, referencia, suffix);
                if (documento != null)
                    return documento;
            }
        }

        return null;
    }

    private async Task<string> ObtenerRutaPdfAsync(DocumentoLocalizado doc, string suffix, string creferencia)
    {
        if (doc.Tipo == TipoContenedor.Pdf)
            return doc.RutaContenedor;

        string cacheKey = $"pdf_extracted_{doc.RutaContenedor}_{suffix}";

        if (_cache.TryGetValue<string>(cacheKey, out string? cachedPath) &&
        !string.IsNullOrWhiteSpace(cachedPath) &&
        System.IO.File.Exists(cachedPath))
        {
            return cachedPath;
        }

        byte[] bytes = await ExtraerPdfDeArchivoAsync(doc, suffix);
        if (bytes.Length == 0) return string.Empty;

        string extDir = Path.Combine(Path.GetTempPath(), "PMM_PDF_Cache");
        Directory.CreateDirectory(extDir);

        string safeRef = System.Text.RegularExpressions.Regex.Replace(creferencia ?? "doc", @"[^\w-]", "_");
        string tempPath = Path.Combine(extDir, $"{safeRef}{suffix}_{Guid.NewGuid():N}.pdf");

        await System.IO.File.WriteAllBytesAsync(tempPath, bytes);

        _cache.Set(cacheKey, tempPath, CacheDuration);
        return tempPath;
    }

    private static async Task<byte[]> ExtraerPdfDeArchivoAsync(DocumentoLocalizado doc, string suffix)
    {
        using FileStream stream = System.IO.File.OpenRead(doc.RutaContenedor);
        using IArchive archive = doc.Tipo == TipoContenedor.Zip
        ? ZipArchive.OpenArchive(stream)
        : RarArchive.OpenArchive(stream);

        string targetSuffix = string.IsNullOrEmpty(suffix) ? ".pdf" : (suffix + ".pdf");
        List<IArchiveEntry> entries = archive.Entries
        .Where(e => !e.IsDirectory && e.Key != null &&
      e.Key.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        .ToList();

        if (entries.Count == 0) return Array.Empty<byte>();

        IArchiveEntry preferida = entries.FirstOrDefault(e =>
        e.Key!.EndsWith(targetSuffix, StringComparison.OrdinalIgnoreCase)) ?? entries.First();

        using MemoryStream ms = new MemoryStream();
        using Stream entryStream = preferida.OpenEntryStream();
        await entryStream.CopyToAsync(ms);
        return ms.ToArray();
    }
}
