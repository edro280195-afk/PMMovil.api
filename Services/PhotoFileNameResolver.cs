using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace PMMovil.Api.Services;

/// <summary>
/// Resuelve los nombres físicos de las fotografías para mantener compatibilidad
/// con el formato que consume el sistema anterior de PMM (Visual FoxPro).
/// </summary>
internal static class PhotoFileNameResolver
{
    /// <summary>
    /// Normaliza la extensión para almacenarla y utilizarla sin punto.
    /// </summary>
    public static string NormalizeExtension(string? extension)
    {
        string normalized = (extension ?? "jpg").Trim().TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "jpg" : normalized;
    }

    /// <summary>
    /// Formato nuevo compatible con FoxPro: la línea no se rellena con cero.
    /// Ejemplo: REF-1071.jpg.
    /// </summary>
    public static string Build(string creferencia, int ntipodocto, int nlineno, string? extension)
    {
        string safeReference = SanitizeReference(creferencia);
        string normalizedExtension = NormalizeExtension(extension);
        return $"{safeReference}-{ntipodocto}{nlineno}.{normalizedExtension}";
    }

    /// <summary>
    /// Formato anterior del API, conservado como alternativa durante la
    /// renombrada de archivos existentes. Ejemplo: REF-10701.jpg.
    /// </summary>
    public static string BuildLegacy(string creferencia, int ntipodocto, int nlineno, string? extension)
    {
        string safeReference = SanitizeReference(creferencia);
        string normalizedExtension = NormalizeExtension(extension);
        return $"{safeReference}-{ntipodocto}{nlineno:D2}.{normalizedExtension}";
    }

    public static IEnumerable<string> GetCandidatePaths(
        string basePath,
        string folder,
        string creferencia,
        int ntipodocto,
        int nlineno,
        string? extension)
    {
        string preferredPath = Path.Combine(
            basePath,
            folder,
            Build(creferencia, ntipodocto, nlineno, extension));
        yield return preferredPath;

        string legacyPath = Path.Combine(
            basePath,
            folder,
            BuildLegacy(creferencia, ntipodocto, nlineno, extension));
        if (!string.Equals(preferredPath, legacyPath, System.StringComparison.OrdinalIgnoreCase))
        {
            yield return legacyPath;
        }
    }

    public static string? FindExistingPath(
        string basePath,
        string folder,
        string creferencia,
        int ntipodocto,
        int nlineno,
        string? extension)
    {
        foreach (string path in GetCandidatePaths(
                     basePath,
                     folder,
                     creferencia,
                     ntipodocto,
                     nlineno,
                     extension))
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string SanitizeReference(string? creferencia)
    {
        return Regex.Replace((creferencia ?? string.Empty).Trim(), @"[^\w-]", string.Empty);
    }
}
