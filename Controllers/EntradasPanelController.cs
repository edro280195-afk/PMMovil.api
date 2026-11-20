using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

public sealed class EntryPanelQuery
{
    [FromQuery(Name = "desde")] public string? From { get; set; }
    [FromQuery(Name = "hasta")] public string? To { get; set; }
    [FromQuery(Name = "clientes")] public string? Clients { get; set; }
    [FromQuery(Name = "manejos")] public string? Handlings { get; set; }
    [FromQuery(Name = "recibieron")] public string? Receivers { get; set; }
    [FromQuery(Name = "transportes")] public string? Carriers { get; set; }
    [FromQuery(Name = "tipo")] public string? Type { get; set; }
    [FromQuery(Name = "fotos")] public string? Photos { get; set; }
    [FromQuery(Name = "danada")] public bool Damaged { get; set; }
    [FromQuery(Name = "q")] public string? Text { get; set; }
    [FromQuery(Name = "orden")] public string? Sort { get; set; }
    [FromQuery(Name = "pagina")] public int Page { get; set; } = 1;
    [FromQuery(Name = "tamano")] public int PageSize { get; set; }
    [FromQuery(Name = "ventana")] public int WindowDays { get; set; }

    public EntryPanelFilter ToFilter(DateTime today)
    {
        return EntryPanelRules.NormalizeFilter(
            today, From, To, Clients, Handlings, Receivers, Carriers, Type, Photos, Damaged, Text, Sort, Page, PageSize);
    }
}

[Authorize]
[ApiController]
[Route("api/EntradasPanel")]
[RequiereModulo(MobileModuleIds.EntriesPanel)]
public sealed class EntradasPanelController : ControllerBase
{
    private const int MaxPhotosPerUpload = 20;
    private const long MaxUploadBytes = 320L * 1024 * 1024;

    private readonly EntryPanelService _panel;
    private readonly EntradaService _entradaService;
    private readonly EntryRealtimePublisher _realtime;
    private readonly ClientePhotoStorage _clientePhotos;
    private readonly ILogger<EntradasPanelController> _logger;

    public EntradasPanelController(
        EntryPanelService panel,
        EntradaService entradaService,
        EntryRealtimePublisher realtime,
        ClientePhotoStorage clientePhotos,
        ILogger<EntradasPanelController> logger)
    {
        _panel = panel;
        _entradaService = entradaService;
        _realtime = realtime;
        _clientePhotos = clientePhotos;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> ListEntries([FromQuery] EntryPanelQuery query)
    {
        try
        {
            EntryListResult result = await _panel.ListAsync(query.ToFilter(DateTime.Now), HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error al listar las entradas del panel.");
            return StatusCode(500, new { success = false, message = "No se pudieron consultar las entradas." });
        }
    }

    [HttpGet("resumen")]
    public async Task<IActionResult> Summary([FromQuery] EntryPanelQuery query)
    {
        try
        {
            DateTime now = DateTime.Now;
            EntrySummaryResult result = await _panel.SummaryAsync(query.ToFilter(now), now, query.WindowDays, HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error al resumir las entradas del panel.");
            return StatusCode(500, new { success = false, message = "No se pudo calcular el resumen de entradas." });
        }
    }

    [HttpGet("exportar")]
    public async Task<IActionResult> Export([FromQuery] EntryPanelQuery query)
    {
        try
        {
            DateTime now = DateTime.Now;
            EntryPanelFilter filter = query.ToFilter(now);
            IReadOnlyList<EntryRow> rows = await _panel.ExportRowsAsync(filter, HttpContext.RequestAborted);
            bool truncated = rows.Count >= EntryPanelRules.MaxExportRows;
            byte[] workbook = EntryPanelExport.Build(rows, filter, now, truncated);
            return File(workbook, EntryPanelExport.ContentType, EntryPanelExport.FileName(now));
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error al exportar las entradas del panel.");
            return StatusCode(500, new { success = false, message = "No se pudo generar el archivo de Excel." });
        }
    }

    [HttpGet("{reference}")]
    public async Task<IActionResult> Detail(string reference)
    {
        try
        {
            EntryDetailResult? detail = await _panel.GetDetailAsync(reference, HttpContext.RequestAborted);
            if (detail is null)
                return NotFound(new { success = false, message = "No se encontró la entrada." });
            return Ok(detail);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error al consultar la entrada {Reference}.", reference);
            return StatusCode(500, new { success = false, message = "No se pudo consultar la entrada." });
        }
    }

    [HttpGet("{reference}/fotos/{photoId:long}")]
    public async Task<IActionResult> Photo(string reference, long photoId, [FromQuery] string? tam = null, [FromQuery] string? v = null)
    {
        try
        {
            CancellationToken ct = HttpContext.RequestAborted;
            EntryPhotoTarget? target = await _panel.FindPhotoAsync(reference, photoId, ct);
            if (target is null)
                return NotFound(new { message = "Foto no encontrada" });

            string extension = PhotoFileNameResolver.NormalizeExtension(target.Extension);
            var photo = new FotoCliente(target.Id, target.Folder, target.Reference, target.DocType, target.Line, extension);
            FotoTamano size = FotoCache.Tamano(tam);
            FotoArchivo? file = await _clientePhotos.ResolverAsync(photo, size, ct);
            if (file != null && !System.IO.File.Exists(file.Ruta))
                file = await _clientePhotos.ResolverAsync(photo, size, ct);
            if (file == null)
                return NotFound(new { message = "El archivo de la foto no está en el servidor." });

            string version = FotoCache.Version(target.Id, target.CreatedUnix);
            Response.Headers.CacheControl = FotoCache.CacheControl(v, version);
            var tag = string.IsNullOrEmpty(version)
                ? null
                : new EntityTagHeaderValue($"\"{version}-{size.ToString().ToLowerInvariant()}\"");
            return new PhysicalFileResult(file.Ruta, file.EsVariante ? "image/jpeg" : ContentTypeFor(file.Ruta, extension))
            {
                LastModified = new DateTimeOffset(DateTime.SpecifyKind(file.ModificadaUtc, DateTimeKind.Utc)),
                EntityTag = tag
            };
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error al servir la foto {PhotoId} de {Reference}.", photoId, reference);
            return StatusCode(500, new { message = "Error al leer la imagen" });
        }
    }

    [HttpPost("{reference}/fotos")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<IActionResult> UploadPhotos(string reference, [FromForm] string? tipo, [FromForm] List<IFormFile>? fotos)
    {
        string? username = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username))
            return Unauthorized(new { success = false, message = "No se pudo identificar al usuario." });

        try
        {
            CancellationToken ct = HttpContext.RequestAborted;
            if (!await _panel.EntryExistsAsync(reference, ct))
                return NotFound(new { success = false, message = "No se encontró la entrada." });
            if (fotos is null || fotos.Count == 0)
                return BadRequest(new { success = false, message = "Elige al menos una foto." });
            if (fotos.Count > MaxPhotosPerUpload)
                return BadRequest(new { success = false, message = $"Sube máximo {MaxPhotosPerUpload} fotos a la vez." });
            if (!await _panel.LogTableExistsAsync(ct))
                return StatusCode(503, new { success = false, message = "Falta crear la bitácora de fotos en la base de datos. No se agregó nada." });

            (List<byte[]> received, string? error) = await FotosRecibidas.LeerAsync(fotos, ct);
            if (error != null)
                return BadRequest(new { success = false, message = error });

            int docType = EntryPanelRules.DocTypeFor(tipo);
            long maxBefore = await _panel.MaxPhotoIdAsync(ct);
            object saved = docType == EntryPanelRules.DamagePhotoType
                ? await _entradaService.GuardarFotosDanioEntradaArchivosAsync(reference.Trim(), received, username)
                : await _entradaService.GuardarFotosEntradaArchivosAsync(reference.Trim(), received, username);

            IReadOnlyList<EntryPhoto> added = await _panel.PhotosAddedAfterAsync(reference, docType, maxBefore, CancellationToken.None);
            if (added.Count == 0)
            {
                return Ok(new EntryPhotoUploadResult
                {
                    Success = false,
                    Message = MessageOf(saved) ?? "No se pudo guardar ninguna foto."
                });
            }

            string label = docType == EntryPanelRules.DamagePhotoType ? "de daño" : "de arribo";
            foreach (EntryPhoto photo in added)
            {
                await _panel.TryWriteLogAsync(reference, "AGREGAR", photo.Id, docType, photo.Line, photo.Extension,
                    username, "PANEL", $"Foto {label} agregada desde el panel");
            }

            (int arrival, int damage) = await _panel.CountPhotosAsync(reference, CancellationToken.None);
            await _realtime.PublishPhotosAsync(reference, username, "added", added.Count);
            return Ok(new EntryPhotoUploadResult
            {
                Success = true,
                Message = added.Count == 1 ? "Se agregó 1 foto." : $"Se agregaron {added.Count} fotos.",
                Photos = added,
                ArrivalPhotos = arrival,
                DamagePhotos = damage
            });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error al agregar fotos a la entrada {Reference}.", reference);
            return StatusCode(500, new { success = false, message = "No se pudieron agregar las fotos." });
        }
    }

    [HttpDelete("{reference}/fotos/{photoId:long}")]
    public async Task<IActionResult> DeletePhoto(string reference, long photoId)
    {
        string? username = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username))
            return Unauthorized(new { success = false, message = "No se pudo identificar al usuario." });

        try
        {
            CancellationToken ct = HttpContext.RequestAborted;
            EntryPhotoTarget? target = await _panel.FindPhotoAsync(reference, photoId, ct);
            if (target is null)
                return NotFound(new { success = false, message = "La foto ya no existe." });
            if (!await _panel.LogTableExistsAsync(ct))
                return StatusCode(503, new { success = false, message = "Falta crear la bitácora de fotos en la base de datos. No se eliminó nada." });

            string label = target.DocType == EntryPanelRules.DamagePhotoType ? "de daño" : "de arribo";
            try
            {
                await _panel.WriteLogAsync(reference, "ELIMINAR", target.Id, target.DocType, target.Line, target.Extension,
                    username, "PANEL", $"Foto {label} eliminada desde el panel", CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "No se pudo registrar en la bitácora la baja de la foto {PhotoId}.", photoId);
                return StatusCode(500, new { success = false, message = "No se pudo registrar la baja en la bitácora. La foto no se eliminó." });
            }

            int deleted = await _panel.DeletePhotoRowAsync(reference, photoId, CancellationToken.None);
            if (deleted == 0)
                return NotFound(new { success = false, message = "La foto ya no existe." });

            if (target.DocType == EntryPanelRules.DamagePhotoType)
                await _panel.AdjustDamageTrackingAsync(reference);

            string extension = PhotoFileNameResolver.NormalizeExtension(target.Extension);
            _clientePhotos.Borrar(new FotoCliente(target.Id, target.Folder, target.Reference, target.DocType, target.Line, extension));

            (int arrival, int damage) = await _panel.CountPhotosAsync(reference, CancellationToken.None);
            await _realtime.PublishPhotosAsync(reference, username, "removed", 1);
            return Ok(new { success = true, message = "Se eliminó la foto.", arrivalPhotos = arrival, damagePhotos = damage });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error al eliminar la foto {PhotoId} de {Reference}.", photoId, reference);
            return StatusCode(500, new { success = false, message = "No se pudo eliminar la foto." });
        }
    }

    private static string? MessageOf(object result)
    {
        JsonElement element = JsonSerializer.SerializeToElement(result);
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("message", out JsonElement message)
            && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
    }

    private static string ContentTypeFor(string path, string? extension)
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
}
