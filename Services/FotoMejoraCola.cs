using System.Threading.Channels;

namespace PMMovil.Api.Services;

/// <summary>
/// Punto único para pedir que una foto ya guardada se mejore. Encolar no espera nada, no falla nunca y no toca el archivo:
/// quien guarda fotos solo avisa y sigue con lo suyo.
/// </summary>
public interface IFotoMejoraCola
{
    void Encolar(string rutaArchivo, string origen);
}

/// <summary>
/// Lo que se sabía del archivo al encolarlo (tamaño y fecha de escritura). El trabajador lo compara antes de reemplazar:
/// si cambió o desapareció mientras esperaba turno, no lo toca.
/// </summary>
public sealed record FotoMejoraTrabajo(string Ruta, string Origen, long Bytes, DateTime UltimaEscrituraUtc);

public sealed class FotoMejoraCola : IFotoMejoraCola
{
    private readonly Channel<FotoMejoraTrabajo> _canal;
    private readonly FotoMejoraOptions _options;
    private readonly ILogger<FotoMejoraCola> _logger;

    public FotoMejoraCola(FotoMejoraOptions options, ILogger<FotoMejoraCola> logger)
    {
        _options = options;
        _logger = logger;
        // Modo Wait: TryWrite devuelve false cuando está llena (con DropWrite devolvería true y no nos enteraríamos).
        _canal = Channel.CreateBounded<FotoMejoraTrabajo>(new BoundedChannelOptions(Math.Max(10, options.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false
        });
    }

    public ChannelReader<FotoMejoraTrabajo> Lector => _canal.Reader;

    public void Encolar(string rutaArchivo, string origen)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(rutaArchivo)) return;

        try
        {
            string extension = Path.GetExtension(rutaArchivo);
            if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                return;

            var info = new FileInfo(rutaArchivo);
            if (!info.Exists) return;

            if (!_canal.Writer.TryWrite(new FotoMejoraTrabajo(rutaArchivo, origen, info.Length, info.LastWriteTimeUtc)))
                _logger.LogWarning("Cola de mejora de fotos llena: {Archivo} se queda sin mejorar.", rutaArchivo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo encolar {Archivo} para mejora.", rutaArchivo);
        }
    }
}
