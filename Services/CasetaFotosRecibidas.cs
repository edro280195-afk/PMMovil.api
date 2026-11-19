namespace PMMovil.Api.Services;

/// <summary>
/// Fotos de caseta que llegan como archivos (multipart) en vez de texto base64 (ver <see cref="FotosRecibidas"/>). Las
/// reglas son las mismas que las de las rutas con base64.
/// </summary>
public static class CasetaFotosRecibidas
{
    /// <summary>Tope por foto (el mismo de Entrada y Revisión).</summary>
    public const long MaxBytesPorFoto = FotosRecibidas.MaxBytesPorFoto;

    public const int MaxFotosPorLoteEntrada = 4;
    public const int IndiceMaximoEntrada = 15;

    /// <summary>Lee los archivos de la petición. Devuelve el error a contestar si alguno está vacío o es demasiado grande.</summary>
    public static Task<(List<byte[]> Fotos, string? Error)> LeerAsync(IReadOnlyList<IFormFile>? archivos, CancellationToken ct)
    {
        return FotosRecibidas.LeerAsync(archivos, ct);
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
