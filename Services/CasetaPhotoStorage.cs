using System.Text.RegularExpressions;

namespace PMMovil.Api.Services;

/// <summary>
/// Fotos de caseta (entrada 106 y salida 101). Viven todas en una sola carpeta del 23 (<c>Fotos\Caseta</c>) con el nombre
/// <c>folio-índice.jpg</c> (<c>E-021107-26-0003.jpg</c>). Cómo se mueven entre la cola, lo caliente y el 23 lo hace
/// <see cref="AlmacenFotos"/>; aquí solo van los nombres y las rutas propias de Caseta, que son las mismas de siempre
/// (la cola ya existía en producción y no se mueve).
/// </summary>
public class CasetaPhotoStorage : AlmacenFotos
{
    private readonly IConfiguration _config;

    public CasetaPhotoStorage(IConfiguration config, ILogger<CasetaPhotoStorage> logger, IFotoMejoraCola? mejora = null)
        : base(Opciones(config), logger, mejora)
    {
        _config = config;
    }

    private static AlmacenFotosOpciones Opciones(IConfiguration config)
    {
        string respaldo = GetFallbackDirectory(config);
        return AlmacenFotosOpciones.Leer(config, "CasetaPhotoSettings", "Caseta", "Caseta", GetQueueRoot(config)) with
        {
            CarpetaRespaldo = respaldo,
            CarpetasMiniaturaAnterior = new[]
            {
                Path.Combine(GetPrimaryDirectory(config), "thumbnails"),
                Path.Combine(respaldo, "thumbnails")
            }
        };
    }

    protected override string RutaEn23(string nombre) => Path.Combine(GetPrimaryDirectory(_config), Path.GetFileName(nombre));

    protected override string Raiz23 => GetPrimaryDirectory(_config);

    protected override string OrigenMejora(string nombre) => "caseta";

    public string GetFileName(string casetaId, int index)
    {
        return $"{casetaId.Replace('/', '-')}-{index:D4}.jpg";
    }

    public string? FindReadablePhotoPath(string casetaId, int index, string? extension = null)
    {
        return BuscarOriginal(GetCandidateFileNames(casetaId, index, extension));
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

    /// <summary>Foto de caseta en el tamaño pedido (ver <see cref="AlmacenFotos.ResolverAsync(IReadOnlyList{string}, FotoTamano, CancellationToken)"/>).</summary>
    public Task<FotoArchivo?> ResolverAsync(
        string casetaId,
        int index,
        string? extension,
        FotoTamano tamano,
        CancellationToken ct = default)
    {
        return ResolverAsync(GetCandidateFileNames(casetaId, index, extension), tamano, ct);
    }

    public IEnumerable<string> EnumerateCasetaPhotos(string casetaId)
    {
        string prefix = casetaId.Replace("/", "-") + "-*.*";
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var directories = new List<string> { DirectorioPendientes };
        if (HotCacheActivo)
        {
            directories.Add(DirectorioCalienteFotos);
        }

        if (DirectorioRespaldo != null)
        {
            directories.Add(DirectorioRespaldo);
        }

        directories.Add(GetPrimaryDirectory(_config));

        foreach (string directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(directory, prefix).OrderBy(x => x))
            {
                string fileName = Path.GetFileName(file);
                if (EsTemporal(fileName) || ExisteBaja(fileName))
                {
                    continue;
                }

                files.TryAdd(fileName, file);
            }
        }

        return files
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Value);
    }

    private static string GetPrimaryDirectory(IConfiguration config)
    {
        string? explicitPath = config["CasetaPhotoSettings:BasePath"] ?? config["PhotoSettings:CasetaBasePath"];
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        string? photoBase = config["PhotoSettings:BasePath"];
        if (!string.IsNullOrWhiteSpace(photoBase))
        {
            string normalized = photoBase.TrimEnd('\\', '/');
            return normalized.EndsWith("Caseta", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : Path.Combine(normalized, "Caseta");
        }

        return @"\\192.168.10.23\Programas\Trafico\fotos\Caseta";
    }

    private static string GetFallbackDirectory(IConfiguration config)
    {
        return config["CasetaPhotoSettings:FallbackPath"]
            ?? @"C:\inetpub\wwwroot\PMMCaseta\Content\TempFotos";
    }

    private static string GetQueueRoot(IConfiguration config)
    {
        return config["CasetaPhotoSettings:QueuePath"]
            ?? Path.Combine(GetFallbackDirectory(config), "CasetaQueue");
    }

    private static string NormalizeExtension(string? extension)
    {
        string clean = (extension ?? "jpg").Trim().TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(clean) ? "jpg" : clean;
    }
}
