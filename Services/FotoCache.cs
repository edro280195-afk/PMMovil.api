namespace PMMovil.Api.Services;

/// <summary>
/// Reglas para que la tableta guarde las fotos (Caseta, Entrada Bodega y Revisión) y no las vuelva a pedir.
///
/// Cada foto tiene una versión (su <c>idfotos</c> y la hora de su último guardado, <c>fcrea</c>, que se actualiza al
/// reemplazarla). La app la recibe en el detalle y la pone en la dirección (<c>&amp;v=...</c>): una dirección con la versión
/// vigente nunca cambia de contenido, así que se puede guardar para siempre. Sin versión, o con una vieja, la tableta
/// pregunta cada vez si cambió (respuesta 304 de unos cuantos bytes si no).
/// </summary>
public static class FotoCache
{
    public const string ParaSiempre = "private, max-age=31536000, immutable";
    public const string Preguntar = "private, no-cache";

    /// <summary>Versión de una foto: cambia si se reemplaza (nuevo fcrea) o se borra y se vuelve a dar de alta (nuevo idfotos).</summary>
    public static string Version(long idFotos, long fcreaUnix)
    {
        return idFotos <= 0 ? string.Empty : $"{idFotos:x}-{Math.Max(0, fcreaUnix):x}";
    }

    public static string CacheControl(string? versionPedida, string versionVigente)
    {
        return !string.IsNullOrEmpty(versionVigente)
            && string.Equals(versionPedida?.Trim(), versionVigente, StringComparison.OrdinalIgnoreCase)
            ? ParaSiempre
            : Preguntar;
    }

    /// <summary>"mini" y "vista"; cualquier otra cosa (o nada, como piden las versiones anteriores de la app) es el original.</summary>
    public static FotoTamano Tamano(string? tam)
    {
        return (tam ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "mini" => FotoTamano.Mini,
            "vista" => FotoTamano.Vista,
            _ => FotoTamano.Original
        };
    }
}
