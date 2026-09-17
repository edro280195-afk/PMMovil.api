namespace PMMovil.Api.Models;

/// <summary>
/// Asocia una clase de mercancia (tipo de bulto, tabla `clases`) con su
/// representacion visual en el mapa de bodega: la primitiva 2D/3D, color,
/// dimensiones de una unidad y, opcionalmente, un modelo .glb (solo web).
/// </summary>
public class ClaseModelSpec
{
    /// <summary>Valor de la clase tal cual se guarda (ej. "CAJA(S)").</summary>
    public string ClaseId { get; set; } = string.Empty;

    /// <summary>Primitiva del vocabulario: box, crate, container, drum, tub, sack,
    /// soft, bundle, rollH, reel, pipe, piece, mixed, flat.</summary>
    public string Primitive { get; set; } = "box";

    /// <summary>Color base en hex para 2D/3D y leyenda.</summary>
    public string Color { get; set; } = "#9ca3af";

    /// <summary>Ancho/fondo de una unidad, en metros.</summary>
    public double UnitFootprint { get; set; } = 0.6;

    /// <summary>Alto de una unidad, en metros.</summary>
    public double UnitHeight { get; set; } = 0.6;

    /// <summary>Si la carga se puede apilar (afecta el stack en 3D).</summary>
    public bool Stackable { get; set; } = true;

    /// <summary>Glyph 2D para el mapa plano y el dropdown de entrada.</summary>
    public string Icon { get; set; } = "cube";

    /// <summary>Ruta a un modelo .glb opcional. Solo lo usa el render web (three.js).</summary>
    public string? ModelUrl { get; set; }
}

/// <summary>Catalogo completo Clase -> Modelo, con metadatos de versionado.</summary>
public class ClaseModelCatalog
{
    public int Version { get; set; } = 1;
    public string UpdatedBy { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public List<ClaseModelSpec> Items { get; set; } = new();
}
