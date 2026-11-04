using System.Text.RegularExpressions;

namespace PMMovil.Api.Services;

public sealed record CasetaPhotoStage(string FileName, string StagedPath);

public class CasetaPhotoStorage
{
    private readonly IConfiguration _config;
    private readonly ILogger<CasetaPhotoStorage> _logger;
    private readonly IFotoMejoraCola? _mejora;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public CasetaPhotoStorage(IConfiguration config, ILogger<CasetaPhotoStorage> logger, IFotoMejoraCola? mejora = null)
    {
        _config = config;
        _logger = logger;
        _mejora = mejora;
    }

    public string GetFileName(string casetaId, int index)
    {
        return $"{casetaId.Replace('/', '-')}-{index:D4}.jpg";
    }

    public string? FindReadablePhotoPath(string casetaId, int index, string? extension = null)
    {
        foreach (string fileName in GetCandidateFileNames(casetaId, index, extension))
        {
            string? path = FindReadablePhotoPath(fileName);
            if (path != null)
            {
                return path;
            }
        }

        return null;
    }

    public IReadOnlyList<string> GetCandidateFileNames(string casetaId, int index, string? extension = null)
    {
        string normalized = casetaId.Trim().Replace('/', '-');
        string compact = Regex.Replace(casetaId.Trim(), @"[^\w-]", "");
        string preferredExt = NormalizeExtension(extension);
        string[] extensions = new[] { preferredExt, "jpg", "jpeg", "png", "webp" }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var names = new List<string>();
        foreach (string ext in extensions)
        {
            names.Add($"{normalized}-{index:D4}.{ext}");
            names.Add($"{normalized}-106{index:D2}.{ext}");
            names.Add($"{compact}-106{index:D2}.{ext}");
            names.Add($"{compact}-{index:D4}.{ext}");
            names.Add($"{normalized}-{index:D2}.{ext}");
        }

        return names
            .Where(x => !string.IsNullOrWhiteSpace(Path.GetFileName(x)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<CasetaPhotoStage> StageAsync(byte[] data, string fileName, CancellationToken ct)
    {
        string safeFileName = Path.GetFileName(fileName);
        string staging = GetStagingDirectory();
        Directory.CreateDirectory(staging);

        string stagedFile = Path.Combine(staging, $"{Guid.NewGuid():N}-{safeFileName}");
        string tempFile = stagedFile + ".tmp";

        await File.WriteAllBytesAsync(tempFile, data, ct);
        File.Move(tempFile, stagedFile, overwrite: true);

        return new CasetaPhotoStage(safeFileName, stagedFile);
    }

    public void ActivateStagedPhotos(IEnumerable<CasetaPhotoStage> photos)
    {
        string pending = GetPendingDirectory();
        Directory.CreateDirectory(pending);

        foreach (CasetaPhotoStage photo in photos)
        {
            string target = Path.Combine(pending, photo.FileName);
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            File.Move(photo.StagedPath, target, overwrite: true);
        }
    }

    public void CleanupStagedPhotos(IEnumerable<CasetaPhotoStage> photos)
    {
        foreach (CasetaPhotoStage photo in photos)
        {
            TryDelete(photo.StagedPath);
        }
    }

    public void QueueDelete(string fileName)
    {
        string safeFileName = Path.GetFileName(fileName);
        string deleteQueue = GetDeleteDirectory();
        Directory.CreateDirectory(deleteQueue);

        TryDelete(Path.Combine(GetPendingDirectory(), safeFileName));

        string marker = Path.Combine(deleteQueue, safeFileName + ".delete");
        File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));
    }

    public string? FindReadablePhotoPath(string fileName)
    {
        string safeFileName = Path.GetFileName(fileName);
        string pending = Path.Combine(GetPendingDirectory(), safeFileName);
        string fallback = Path.Combine(GetFallbackDirectory(), safeFileName);
        string primary = Path.Combine(GetPrimaryDirectory(), safeFileName);
        string primaryThumb = Path.Combine(GetPrimaryDirectory(), "thumbnails", safeFileName);
        string fallbackThumb = Path.Combine(GetFallbackDirectory(), "thumbnails", safeFileName);

        foreach (string path in new[] { pending, fallback, primary, primaryThumb, fallbackThumb })
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public IEnumerable<string> EnumerateCasetaPhotos(string casetaId)
    {
        string prefix = casetaId.Replace("/", "-") + "-*.*";
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in new[] { GetPendingDirectory(), GetFallbackDirectory(), GetPrimaryDirectory() })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(directory, prefix).OrderBy(x => x))
            {
                string fileName = Path.GetFileName(file);
                files.TryAdd(fileName, file);
            }
        }

        return files
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Value);
    }

    public async Task PublishPendingPhotosAsync(CancellationToken ct)
    {
        if (!await _syncLock.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            ProcessDeleteQueue();

            string pending = GetPendingDirectory();
            if (!Directory.Exists(pending))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(pending, "*.jpg").OrderBy(x => x))
            {
                ct.ThrowIfCancellationRequested();
                await PublishOneAsync(file, ct);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task PublishOneAsync(string pendingFile, CancellationToken ct)
    {
        string fileName = Path.GetFileName(pendingFile);
        string primaryDirectory = GetPrimaryDirectory();
        string targetFile = Path.Combine(primaryDirectory, fileName);
        string tempTarget = targetFile + ".uploading";

        try
        {
            Directory.CreateDirectory(primaryDirectory);
            TryDelete(tempTarget);

            await using (FileStream source = new(
                pendingFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true))
            await using (FileStream target = new(
                tempTarget,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await source.CopyToAsync(target, ct);
            }

            File.Move(tempTarget, targetFile, overwrite: true);
            File.Delete(pendingFile);
            TryDelete(Path.Combine(GetFallbackDirectory(), fileName));
            _logger.LogInformation("Foto de caseta sincronizada: {FileName}", fileName);

            // Ya está en su lugar definitivo: se avisa para mejorarla en segundo plano (no espera ni puede fallar).
            _mejora?.Encolar(targetFile, "caseta");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempTarget);
            _logger.LogWarning(ex, "No se pudo sincronizar foto de caseta {FileName}. Se reintentara.", fileName);
        }
    }

    private void ProcessDeleteQueue()
    {
        string deleteQueue = GetDeleteDirectory();
        if (!Directory.Exists(deleteQueue))
        {
            return;
        }

        foreach (string marker in Directory.GetFiles(deleteQueue, "*.delete").OrderBy(x => x))
        {
            string fileName = Path.GetFileNameWithoutExtension(marker);
            try
            {
                TryDelete(Path.Combine(GetPendingDirectory(), fileName));
                TryDelete(Path.Combine(GetFallbackDirectory(), fileName));
                TryDelete(Path.Combine(GetPrimaryDirectory(), fileName));
                File.Delete(marker);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo procesar baja de foto de caseta {FileName}.", fileName);
            }
        }
    }

    private string GetPrimaryDirectory()
    {
        string? explicitPath = _config["CasetaPhotoSettings:BasePath"] ?? _config["PhotoSettings:CasetaBasePath"];
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        string? photoBase = _config["PhotoSettings:BasePath"];
        if (!string.IsNullOrWhiteSpace(photoBase))
        {
            string normalized = photoBase.TrimEnd('\\', '/');
            return normalized.EndsWith("Caseta", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : Path.Combine(normalized, "Caseta");
        }

        return @"\\192.168.10.23\Programas\Trafico\fotos\Caseta";
    }

    private string GetFallbackDirectory()
    {
        return _config["CasetaPhotoSettings:FallbackPath"]
            ?? @"C:\inetpub\wwwroot\PMMCaseta\Content\TempFotos";
    }

    private string GetQueueRoot()
    {
        return _config["CasetaPhotoSettings:QueuePath"]
            ?? Path.Combine(GetFallbackDirectory(), "CasetaQueue");
    }

    private string GetPendingDirectory()
    {
        return Path.Combine(GetQueueRoot(), "pending");
    }

    private string GetStagingDirectory()
    {
        return Path.Combine(GetQueueRoot(), "staging");
    }

    private string GetDeleteDirectory()
    {
        return Path.Combine(GetQueueRoot(), "delete");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string NormalizeExtension(string? extension)
    {
        string clean = (extension ?? "jpg").Trim().TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(clean) ? "jpg" : clean;
    }
}
