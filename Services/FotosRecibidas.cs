namespace PMMovil.Api.Services;

/// <summary>
/// Fotos que llegan como archivos (multipart) en vez de texto base64, que infla cada foto un 33 %. Las tabletas de la
/// oficina suben por el internet de la oficina (~13 Mbps de subida compartidos, medido el 18-nov-2026): como archivo, 8
/// fotos pasan de ~8.5 MB a ~6.4 MB. Lo usan Caseta (<see cref="CasetaFotosRecibidas"/>), Entrada Bodega y Revisión.
/// </summary>
public static class FotosRecibidas
{
    /// <summary>Tope por foto: la app manda ~0.5-1.1 MB (2560 px, calidad 92); esto solo frena algo que no es una foto de la app.</summary>
    public const long MaxBytesPorFoto = 15L * 1024 * 1024;

    /// <summary>Lee los archivos de la petición. Devuelve el error a contestar si alguno está vacío o es demasiado grande.</summary>
    public static async Task<(List<byte[]> Fotos, string? Error)> LeerAsync(IReadOnlyList<IFormFile>? archivos, CancellationToken ct)
    {
        var fotos = new List<byte[]>();
        if (archivos == null)
        {
            return (fotos, null);
        }

        foreach (IFormFile archivo in archivos)
        {
            if (archivo.Length <= 0)
            {
                return (fotos, "Una foto llegó vacía.");
            }

            if (archivo.Length > MaxBytesPorFoto)
            {
                return (fotos, "Una foto es demasiado grande.");
            }

            using var memoria = new MemoryStream((int)archivo.Length);
            await archivo.CopyToAsync(memoria, ct);
            fotos.Add(memoria.ToArray());
        }

        return (fotos, null);
    }

    /// <summary>"jpg", "png" o "webp" según los primeros bytes del archivo. La app manda JPEG; cualquier otra cosa se toma por JPEG.</summary>
    public static string ExtensionDe(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "png";
        }

        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return "webp";
        }

        return "jpg";
    }
}
