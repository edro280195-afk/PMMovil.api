namespace PMMovil.Api.Services;

/// <summary>
/// Fotos de caseta que llegan como archivos (multipart) en vez de texto base64. El base64 infla cada foto un 33 %, y en
/// caseta todo cruza el internet de la oficina (~13 Mbps de subida compartidos, medido el 18-nov-2026): como archivo, una
/// salida de 8 fotos pasa de ~8.5 MB a ~6.4 MB. Las reglas son las mismas que las de las rutas con base64.
/// </summary>
public static class CasetaFotosRecibidas
{
    /// <summary>Tope por foto: la app manda ~0.5-1.1 MB (2560 px, calidad 92); esto solo frena algo que no es una foto de la app.</summary>
    public const long MaxBytesPorFoto = 15L * 1024 * 1024;

    public const int MaxFotosPorLoteEntrada = 4;
    public const int IndiceMaximoEntrada = 15;

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

    /// <summary>Las mismas reglas que <c>CasetaService.SubirFotosSalidaAsync</c> (posiciones desde 1, lotes chicos).</summary>
    public static string? ValidarLoteSalida(int indiceInicial, IReadOnlyList<byte[]>? fotos, int maxPorLote, int maxTotal)
    {
        if (fotos == null || fotos.Count == 0) return "No llegó ninguna foto.";
        if (fotos.Count > maxPorLote) return $"Máximo {maxPorLote} fotos por envío.";
        if (indiceInicial < 1 || indiceInicial + fotos.Count - 1 > maxTotal) return $"La salida admite hasta {maxTotal} fotos.";

        for (int i = 0; i < fotos.Count; i++)
        {
            if (fotos[i] == null || fotos[i].Length == 0) return $"La foto {indiceInicial + i} llegó vacía.";
        }

        return null;
    }

    /// <summary>Las mismas reglas que <c>CasetaService.SubirFotosEntradaCasetaAsync</c>; cada foto trae su índice.</summary>
    public static string? ValidarLoteEntrada(IReadOnlyList<int>? indices, IReadOnlyList<byte[]>? fotos)
    {
        if (fotos == null || fotos.Count == 0) return "No se recibieron fotos.";
        if (indices == null || indices.Count != fotos.Count) return "Cada foto debe llevar su indice.";
        if (fotos.Count > MaxFotosPorLoteEntrada) return $"Solo se permiten {MaxFotosPorLoteEntrada} fotos por lote.";
        if (indices.Any(x => x is < 1 or > IndiceMaximoEntrada)) return $"El indice de foto debe estar entre 1 y {IndiceMaximoEntrada}.";
        if (indices.Distinct().Count() != indices.Count) return "No se permiten indices de foto repetidos.";

        for (int i = 0; i < fotos.Count; i++)
        {
            if (fotos[i] == null || fotos[i].Length == 0) return $"La foto {indices[i]} no es valida.";
        }

        return null;
    }
}
