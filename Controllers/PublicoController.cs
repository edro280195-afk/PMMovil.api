using System.Data;
using System.IO;

using Dapper;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Data;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Endpoints deliberadamente públicos, sin sesión de PMMovil -- "Seguimiento
/// de entrada" (EntradaShareService) y "Ver reporte" (ReportShareService).
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
    private readonly IReportShareService _reportShareService;
    private readonly RevisionReportPdfService _reportPdfService;
    private readonly BaseRepository _db;
    private readonly ILogger<PublicoController> _logger;

    public PublicoController(
        IEntradaShareService shareService,
        IRevisionShareService revisionShareService,
        IReportShareService reportShareService,
        RevisionReportPdfService reportPdfService,
        BaseRepository db,
        ILogger<PublicoController> logger)
    {
        _shareService = shareService;
        _revisionShareService = revisionShareService;
        _reportShareService = reportShareService;
        _reportPdfService = reportPdfService;
        _db = db;
        _logger = logger;
    }

    [HttpGet("reporte/{token}.pdf")]
    public async Task<IActionResult> VerReporte(string token)
    {
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
    public async Task<ContentResult> VerEntrada(string token)
    {
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
    public async Task<IActionResult> VerFoto(string token, int linea)
    {
        (string FilePath, string Extension)? foto = await _shareService.ObtenerFotoAsync(token, linea);
        if (foto == null)
        {
            return NotFound();
        }

        string contentType = foto.Value.Extension.ToLowerInvariant() == "png" ? "image/png" : "image/jpeg";
        return PhysicalFile(foto.Value.FilePath, contentType);
    }

    [HttpGet("revision/{token}")]
    public async Task<ContentResult> VerRevision(string token)
    {
        RevisionShareResumen? resumen = await _revisionShareService.ObtenerResumenAsync(token);
        string html = resumen == null
            ? RevisionShareHtmlRenderer.RenderExpirado()
            : RevisionShareHtmlRenderer.RenderResumen(resumen, $"/api/publico/revision/{token}/foto", $"/api/publico/revision/{token}/zip");

        return new ContentResult
        {
            Content = html,
            ContentType = "text/html; charset=utf-8",
            StatusCode = resumen == null ? 410 : 200
        };
    }

    [HttpGet("revision/{token}/foto/{idFotos}")]
    public async Task<IActionResult> VerFotoRevision(string token, int idFotos)
    {
        (string FilePath, string Extension)? foto = await _revisionShareService.ObtenerFotoAsync(token, idFotos);
        if (foto == null)
        {
            return NotFound();
        }

        string contentType = foto.Value.Extension.ToLowerInvariant() == "png" ? "image/png" : "image/jpeg";
        return PhysicalFile(foto.Value.FilePath, contentType);
    }

    [HttpGet("revision/{token}/zip")]
    public async Task<IActionResult> DescargarZipRevision(string token)
    {
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
}
