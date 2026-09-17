using System.Text.Json;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Catalogo Clase -> Modelo de carga para el mapa de bodega.
/// Los valores por defecto viven en codigo (fuente unica de verdad). Si existe el
/// archivo wwwroot/config/clase-models.json, sus entradas sobreescriben/agregan a los
/// defaults. El editor persiste cambios en ese archivo. NO usa base de datos.
/// </summary>
public class ClaseModelService
{
    private readonly IWebHostEnvironment _env;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public ClaseModelService(IWebHostEnvironment env)
    {
        _env = env;
    }

    private string ConfigDir =>
        Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "config");

    private string ConfigPath => Path.Combine(ConfigDir, "clase-models.json");

    public async Task<ClaseModelCatalog> GetCatalogAsync()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(ConfigPath);
                var stored = JsonSerializer.Deserialize<ClaseModelCatalog>(json, JsonOpts);
                if (stored != null)
                {
                    return Merge(DefaultSpecs(), stored);
                }
            }
            catch
            {
                // archivo corrupto -> se ignora y se usan defaults
            }
        }

        return new ClaseModelCatalog { Items = DefaultSpecs() };
    }

    public async Task<ClaseModelCatalog> SaveCatalogAsync(ClaseModelCatalog request, string usuario)
    {
        var merged = Merge(DefaultSpecs(), request);
        merged.UpdatedBy = string.IsNullOrWhiteSpace(usuario) ? "sistema" : usuario;
        merged.UpdatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        merged.Version = (request.Version <= 0 ? 1 : request.Version) + 1;

        Directory.CreateDirectory(ConfigDir);
        string json = JsonSerializer.Serialize(merged, JsonOpts);
        await File.WriteAllTextAsync(ConfigPath, json);

        return merged;
    }

    /// <summary>Combina defaults con overlay (por ClaseId), preservando el orden de defaults.</summary>
    private static ClaseModelCatalog Merge(List<ClaseModelSpec> defaults, ClaseModelCatalog overlay)
    {
        var overlayItems = overlay.Items ?? new List<ClaseModelSpec>();

        var byId = new Dictionary<string, ClaseModelSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defaults) byId[d.ClaseId] = d;
        foreach (var o in overlayItems)
        {
            if (!string.IsNullOrWhiteSpace(o.ClaseId)) byId[o.ClaseId] = o;
        }

        var ordered = new List<ClaseModelSpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defaults)
        {
            ordered.Add(byId[d.ClaseId]);
            seen.Add(d.ClaseId);
        }
        foreach (var o in overlayItems)
        {
            if (!string.IsNullOrWhiteSpace(o.ClaseId) && seen.Add(o.ClaseId))
                ordered.Add(byId[o.ClaseId]);
        }

        return new ClaseModelCatalog
        {
            Version = overlay.Version,
            UpdatedBy = overlay.UpdatedBy,
            UpdatedAt = overlay.UpdatedAt,
            Items = ordered
        };
    }

    /// <summary>Defaults de las 14 clases reales (tabla `clases`).</summary>
    public static List<ClaseModelSpec> DefaultSpecs() => new()
    {
        new() { ClaseId = "CAJA(S)",        Primitive = "box",       Color = "#b08968", UnitFootprint = 0.62, UnitHeight = 0.52, Stackable = true,  Icon = "box" },
        new() { ClaseId = "HUACAL(ES)",     Primitive = "crate",     Color = "#a97142", UnitFootprint = 0.72, UnitHeight = 0.58, Stackable = true,  Icon = "crate" },
        new() { ClaseId = "CONTENEDOR(ES)", Primitive = "container", Color = "#3b6ea5", UnitFootprint = 2.60, UnitHeight = 2.25, Stackable = false, Icon = "container" },
        new() { ClaseId = "TAMBOR(ES)",     Primitive = "drum",      Color = "#2b6cb0", UnitFootprint = 0.58, UnitHeight = 0.88, Stackable = true,  Icon = "drum" },
        new() { ClaseId = "TINETA(S)",      Primitive = "tub",       Color = "#2c9c8f", UnitFootprint = 0.56, UnitHeight = 0.46, Stackable = true,  Icon = "tub" },
        new() { ClaseId = "SACO(S)",        Primitive = "sack",      Color = "#d6a960", UnitFootprint = 0.72, UnitHeight = 0.42, Stackable = true,  Icon = "sack" },
        new() { ClaseId = "BULTO(S)",       Primitive = "soft",      Color = "#9aa2ad", UnitFootprint = 0.78, UnitHeight = 0.52, Stackable = true,  Icon = "package" },
        new() { ClaseId = "ATADO(S)",       Primitive = "bundle",    Color = "#7a8450", UnitFootprint = 1.35, UnitHeight = 0.34, Stackable = true,  Icon = "bundle" },
        new() { ClaseId = "ROLLO(S)",       Primitive = "rollH",     Color = "#6b5bd0", UnitFootprint = 0.82, UnitHeight = 0.68, Stackable = true,  Icon = "roll" },
        new() { ClaseId = "CARRETE(S)",     Primitive = "reel",      Color = "#5b6b7a", UnitFootprint = 0.78, UnitHeight = 0.76, Stackable = true,  Icon = "reel" },
        new() { ClaseId = "TUBO(S)",        Primitive = "pipe",      Color = "#7b8a99", UnitFootprint = 1.60, UnitHeight = 0.32, Stackable = true,  Icon = "pipe" },
        new() { ClaseId = "PIEZA(S)",       Primitive = "piece",     Color = "#c2703d", UnitFootprint = 0.70, UnitHeight = 0.45, Stackable = false, Icon = "gear" },
        new() { ClaseId = "LOTE(S)",        Primitive = "mixed",     Color = "#8a5cc0", UnitFootprint = 0.90, UnitHeight = 0.62, Stackable = true,  Icon = "layers" },
        new() { ClaseId = "SOBRE(S)",       Primitive = "flat",      Color = "#d9a441", UnitFootprint = 0.55, UnitHeight = 0.08, Stackable = true,  Icon = "envelope" },
    };
}
