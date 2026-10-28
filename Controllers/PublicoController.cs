using System.Data;
using System.IO;

using Dapper;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;

using PMMovil.Api.Data;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Endpoints deliberadamente públicos, sin sesión de PMMovil -- seguimiento
/// de entrada, seguimiento de revisión, evidencia de caseta y reportes.
/// Cualquier cosa nueva aquí debe pasar por su propio token, nunca reusar el
/// JWT interno.
/// </summary>
[AllowAnonymous]
[ApiController]
[Route("api/publico")]
public class PublicoController : ControllerBase
{
    private readonly IEntradaShareService _shareService;
    private readonly IRevisionShareService _revisionShareService;
    private readonly ICasetaShareService _casetaShareService;
    private readonly IReportShareService _reportShareService;
    private readonly RevisionReportPdfService _reportPdfService;
    private readonly BaseRepository _db;
    private readonly IConfiguration _config;
    private readonly ILogger<PublicoController> _logger;

    public PublicoController(
        IEntradaShareService shareService,
        IRevisionShareService revisionShareService,
        ICasetaShareService casetaShareService,
        IReportShareService reportShareService,
        RevisionReportPdfService reportPdfService,
        BaseRepository db,
        IConfiguration config,
        ILogger<PublicoController> logger)
    {
        _shareService = shareService;
        _revisionShareService = revisionShareService;
        _casetaShareService = casetaShareService;
        _reportShareService = reportShareService;
        _reportPdfService = reportPdfService;
        _db = db;
        _config = config;
        _logger = logger;
    }

    [HttpGet("reporte/{token}.pdf")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<IActionResult> VerReporte(string token)
    {
        SetPublicShareHeaders();
        ReportShareTarget? target = await _reportShareService.ResolverAsync(token);
        if (target == null)
        {
            return NotFound(new { success = false, message = "Este enlace ya no está disponible." });
        }

        if (target.Tipo == "congelado" && target.IdReporte != null)
        {
            using IDbConnection conn = _db.CreateConnection();
            dynamic? row = await conn.QueryFirstOrDefaultAsync<object>(
                "SELECT pdf AS Pdf, folio AS Folio FROM pmmutileria.revision_reportes_validacion WHERE id = @id",
                new { id = target.IdReporte });
            if (row == null)
            {
                return NotFound(new { success = false, message = "No se encontró ese reporte." });
            }
            return File((byte[])row.Pdf, "application/pdf", $"{(string)row.Folio}.pdf");
        }

        if (target.Tipo == "vivo" && !string.IsNullOrEmpty(target.Referencia))
        {
            (byte[]? pdf, string? error) = await _reportPdfService.GenerarPdfAsync(target.Referencia);
            if (pdf == null)
            {
                return NotFound(new { success = false, message = error ?? "No se pudo generar el reporte." });
            }
            return File(pdf, "application/pdf", $"reporte-revision-{target.Referencia}.pdf");
        }

        return NotFound(new { success = false, message = "Enlace inválido." });
    }

    [HttpGet("entrada/{token}")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<ContentResult> VerEntrada(string token)
    {
        SetPublicShareHeaders();
        EntradaShareResumen? resumen = await _shareService.ObtenerResumenAsync(token);
        string html = resumen == null
            ? EntradaShareHtmlRenderer.RenderExpirado()
            : EntradaShareHtmlRenderer.RenderResumen(resumen, $"/api/publico/entrada/{token}/foto");

        return new ContentResult
        {
            Content = html,
            ContentType = "text/html; charset=utf-8",
            StatusCode = resumen == null ? 410 : 200
        };
    }

    [HttpGet("entrada/{token}/foto/{linea}")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<IActionResult> VerFoto(string token, int linea)
    {
        SetPublicShareHeaders();
        (string FilePath, string Extension)? foto = await _shareService.ObtenerFotoAsync(token, linea);
        if (foto == null)
        {
            return NotFound();
        }

        string contentType = foto.Value.Extension.ToLowerInvariant() == "png" ? "image/png" : "image/jpeg";
        return PhysicalFile(foto.Value.FilePath, contentType);
    }

    [HttpGet("revision/{token}")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<ContentResult> VerRevision(string token)
    {
        SetPublicShareHeaders();
        // La apariencia se decide al abrir el enlace (PublicLinks:Tema, por defecto "auto"): ver ShareThemeResolver.
        ShareTheme theme = ShareThemeResolver.Resolve(_config[ShareThemeResolver.ConfigurationKey], DateTime.Now);
        RevisionShareResumen? resumen = await _revisionShareService.ObtenerResumenAsync(token);
        string html = resumen == null
            ? RevisionShareHtmlRenderer.RenderExpirado(theme)
            : RevisionShareHtmlRenderer.RenderResumen(resumen, $"/api/publico/revision/{token}/foto", $"/api/publico/revision/{token}/zip", theme);

        return new ContentResult
        {
            Content = html,
            ContentType = "text/html; charset=utf-8",
            StatusCode = resumen == null ? 410 : 200
        };
    }

    [HttpGet("revision/{token}/foto/{idFotos}")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<IActionResult> VerFotoRevision(string token, int idFotos)
    {
        SetPublicShareHeaders();
        (string FilePath, string Extension)? foto = await _revisionShareService.ObtenerFotoAsync(token, idFotos);
        if (foto == null)
        {
            return NotFound();
        }

        string contentType = foto.Value.Extension.ToLowerInvariant() == "png" ? "image/png" : "image/jpeg";
        return PhysicalFile(foto.Value.FilePath, contentType);
    }

    [HttpGet("revision/{token}/zip")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<IActionResult> DescargarZipRevision(string token)
    {
        SetPublicShareHeaders();
        (string CrefAgrupada, List<(string FilePath, string FileName)> Archivos)? datos =
            await _revisionShareService.ObtenerArchivosParaZipAsync(token);

        if (datos == null || datos.Value.Archivos.Count == 0)
        {
            return NotFound(new { success = false, message = "No hay fotos disponibles para descargar." });
        }

        var ms = new MemoryStream();
        using (var archivo = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string filePath, string fileName) in datos.Value.Archivos)
            {
                var entrada = archivo.CreateEntry(fileName, System.IO.Compression.CompressionLevel.Optimal);
                using Stream entradaStream = entrada.Open();
                using FileStream origenStream = System.IO.File.OpenRead(filePath);
                await origenStream.CopyToAsync(entradaStream);
            }
        }

        ms.Position = 0;
        return File(ms, "application/zip", $"evidencia-{datos.Value.CrefAgrupada}.zip");
    }

    [HttpGet("caseta/{token}")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<ContentResult> VerCaseta(string token)
    {
        SetPublicShareHeaders();
        CasetaShareResumen? resumen = await _casetaShareService.ObtenerResumenAsync(token);
        string html = resumen == null
            ? CasetaShareHtmlRenderer.RenderExpirado()
            : CasetaShareHtmlRenderer.RenderResumen(
                resumen,
                $"/api/publico/caseta/{token}/foto",
                $"/api/publico/caseta/{token}/zip",
                // Operador, tractor y sellos ya van en el correo; la página los muestra salvo que se apague aquí.
                _config.GetValue("PublicLinks:MostrarDatosOperadorCaseta", true));

        return new ContentResult
        {
            Content = html,
            ContentType = "text/html; charset=utf-8",
            StatusCode = resumen == null ? 410 : 200
        };
    }

    [HttpGet("caseta/{token}/foto/{idFotos:int}")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<IActionResult> VerFotoCaseta(string token, int idFotos)
    {
        SetPublicShareHeaders();
        (string FilePath, string Extension)? foto = await _casetaShareService.ObtenerFotoAsync(token, idFotos);
        if (foto == null)
        {
            return NotFound();
        }

        string contentType = foto.Value.Extension switch
        {
            "png" => "image/png",
            "webp" => "image/webp",
            _ => "image/jpeg"
        };
        return PhysicalFile(foto.Value.FilePath, contentType);
    }

    [HttpGet("caseta/{token}/zip")]
    [EnableRateLimiting("PublicoLinksPolicy")]
    public async Task<IActionResult> DescargarZipCaseta(string token)
    {
        SetPublicShareHeaders();
        List<(string FilePath, string FileName)>? archivos = await _casetaShareService.ObtenerArchivosParaZipAsync(token);
        if (archivos == null || archivos.Count == 0)
        {
            return NotFound(new { success = false, message = "No hay fotos disponibles para descargar." });
        }

        var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string filePath, string fileName) in archivos)
            {
                var entry = zip.CreateEntry(fileName, System.IO.Compression.CompressionLevel.Optimal);
                using Stream entryStream = entry.Open();
                using FileStream sourceStream = System.IO.File.OpenRead(filePath);
                await sourceStream.CopyToAsync(entryStream);
            }
        }

        ms.Position = 0;
        return File(ms, "application/zip", "evidencia-caseta.zip");
    }

    /// <summary>
    /// Mismos encabezados que ya se ponían solo en MonitorTv -- el resto de
    /// los enlaces públicos también llevan un secreto en la URL (el token),
    /// así que también les conviene no cachearse, no indexarse en buscadores
    /// ni filtrar el origen por Referer hacia recursos de terceros.
    /// </summary>
    private void SetPublicShareHeaders()
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
