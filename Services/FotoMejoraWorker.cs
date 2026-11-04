namespace PMMovil.Api.Services;

public enum FotoMejoraEstado
{
    /// <summary>La foto se reemplazó por la versión mejorada.</summary>
    Mejorada,

    /// <summary>La foto no lo necesitaba o no se pudo mejorar: se queda exactamente como estaba.</summary>
    SinCambios,

    /// <summary>No se tocó porque cambió, desapareció o no se pudo respaldar el original.</summary>
    Omitida
}

public sealed record FotoMejoraSalida(FotoMejoraEstado Estado, string Detalle);

/// <summary>
/// Mejora en segundo plano las fotos que el API acaba de guardar. Los endpoints responden con la foto original de
/// inmediato; unos segundos después este trabajador la reemplaza en el mismo archivo si hacía falta. Así subir fotos
/// no se vuelve más lento y, si algo falla, la foto original sigue ahí.
///
/// Reglas de seguridad al reemplazar:
///  - Solo se reemplaza si el archivo sigue igual (tamaño y fecha) que cuando se encoló.
///  - Antes de reemplazar se respalda el original (configurable); si el respaldo falla, no se reemplaza.
///  - El reemplazo usa un archivo temporal oculto (".mej-*.tmp") que no coincide con ningún patrón de fotos, y
///    File.Replace, que falla si la foto ya se borró en vez de resucitarla.
///  - Las fotos encoladas pero aún sin procesar se pierden si el API se reinicia; se quedan como llegaron.
/// </summary>
public sealed class FotoMejoraWorker : BackgroundService
{
    private readonly FotoMejoraCola _cola;
    private readonly FotoMejoraService _servicio;
    private readonly FotoMejoraOptions _options;
    private readonly ILogger<FotoMejoraWorker> _logger;
    private readonly string _raizOriginales;

    private long _mejoradas;
    private long _sinCambios;
    private long _omitidas;
    private long _fallidas;

    public FotoMejoraWorker(
        FotoMejoraCola cola,
        FotoMejoraService servicio,
        FotoMejoraOptions options,
        IConfiguration config,
        ILogger<FotoMejoraWorker> logger)
    {
        _cola = cola;
        _servicio = servicio;
        _options = options;
        _logger = logger;
        _raizOriginales = ResolverRaizOriginales(options, config);
    }

    public long Mejoradas => Interlocked.Read(ref _mejoradas);
    public long SinCambios => Interlocked.Read(ref _sinCambios);
    public long Omitidas => Interlocked.Read(ref _omitidas);
    public long Fallidas => Interlocked.Read(ref _fallidas);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Mejora automática de fotos desactivada (FotoMejora:Enabled=false).");
            return;
        }

        if (!_servicio.IntentarIniciar()) return;

        int hilos = Math.Clamp(_options.Parallelism, 1, 4);
        _logger.LogInformation(
            "Mejora automática de fotos activa: {Hilos} hilo(s); luz < {Luz}, contraste < {Contraste}, nitidez < {Nitidez}; originales en {Originales}.",
            hilos, _options.LuzOscura, _options.ContrasteBajo, _options.NitidezBaja,
            _options.GuardarOriginales ? _raizOriginales : "(no se guardan)");

        var ciclos = Enumerable.Range(0, hilos).Select(_ => Task.Run(() => CicloAsync(stoppingToken), stoppingToken));
        await Task.WhenAll(ciclos);
    }

    private async Task CicloAsync(CancellationToken ct)
    {
        try
        {
            await foreach (FotoMejoraTrabajo trabajo in _cola.Lector.ReadAllAsync(ct))
            {
                try
                {
                    Procesar(trabajo);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _fallidas);
                    _logger.LogWarning(ex, "No se pudo mejorar {Archivo}; se queda como llegó.", trabajo.Ruta);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Apagado del API.
        }
    }

    /// <summary>Procesa un trabajo. Público para poder probarlo sin levantar el servicio hospedado.</summary>
    public FotoMejoraSalida Procesar(FotoMejoraTrabajo trabajo)
    {
        var info = new FileInfo(trabajo.Ruta);
        if (!info.Exists) return Omitir("ya no existe");
        if (info.Length != trabajo.Bytes || info.LastWriteTimeUtc != trabajo.UltimaEscrituraUtc)
            return Omitir("cambió mientras esperaba");

        byte[] original = File.ReadAllBytes(trabajo.Ruta);
        FotoMejoraResultado resultado = _servicio.Mejorar(original);
        if (!resultado.Modificada)
        {
            Interlocked.Increment(ref _sinCambios);
            return new FotoMejoraSalida(FotoMejoraEstado.SinCambios, resultado.Detalle);
        }

        if (_options.GuardarOriginales && !GuardarOriginal(trabajo.Ruta, original))
            return Omitir("no se pudo respaldar el original");

        string carpeta = Path.GetDirectoryName(trabajo.Ruta) ?? string.Empty;
        string temporal = Path.Combine(carpeta, $".mej-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporal, resultado.Bytes);

            info.Refresh();
            if (!info.Exists) return Omitir("se borró mientras se mejoraba");
            if (info.Length != trabajo.Bytes || info.LastWriteTimeUtc != trabajo.UltimaEscrituraUtc)
                return Omitir("cambió mientras se mejoraba");

            try
            {
                File.Replace(temporal, trabajo.Ruta, null, ignoreMetadataErrors: true);
            }
            catch (FileNotFoundException)
            {
                return Omitir("se borró mientras se mejoraba");
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                // Algunos recursos compartidos no soportan File.Replace: se cae a mover encima, pero solo si la foto sigue ahí.
                if (!File.Exists(trabajo.Ruta)) return Omitir("se borró mientras se mejoraba");
                File.Move(temporal, trabajo.Ruta, overwrite: true);
            }
        }
        finally
        {
            TryDelete(temporal);
        }

        Interlocked.Increment(ref _mejoradas);
        _logger.LogInformation(
            "Foto mejorada ({Origen}) {Archivo}: {Acciones}; luz {Luz0:0}->{Luz1:0}, nitidez {Nit0:0}->{Nit1:0}, {Kb0}->{Kb1} KB, {Ms} ms.",
            trabajo.Origen, Path.GetFileName(trabajo.Ruta), resultado.Detalle,
            resultado.Antes?.Luz, resultado.Despues?.Luz, resultado.Antes?.Nitidez, resultado.Despues?.Nitidez,
            original.Length / 1024, resultado.Bytes.Length / 1024, resultado.Milisegundos);
        return new FotoMejoraSalida(FotoMejoraEstado.Mejorada, resultado.Detalle);
    }

    private FotoMejoraSalida Omitir(string motivo)
    {
        Interlocked.Increment(ref _omitidas);
        _logger.LogInformation("Mejora de foto omitida: {Motivo}.", motivo);
        return new FotoMejoraSalida(FotoMejoraEstado.Omitida, motivo);
    }

    /// <summary>
    /// Respalda el original en la raíz de originales como "carpeta-padre\archivo", la misma forma que tiene el árbol de
    /// fotos (cliente\foto, Caseta\foto). Si la foto se vuelve a subir, el respaldo se sobrescribe: corresponde siempre a la
    /// versión que se mejoró.
    /// </summary>
    private bool GuardarOriginal(string rutaFoto, byte[] original)
    {
        try
        {
            string padre = new DirectoryInfo(Path.GetDirectoryName(rutaFoto) ?? string.Empty).Name;
            string destino = Path.Combine(_raizOriginales, padre, Path.GetFileName(rutaFoto));
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            File.WriteAllBytes(destino, original);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo respaldar el original de {Archivo}.", rutaFoto);
            return false;
        }
    }

    private static string ResolverRaizOriginales(FotoMejoraOptions options, IConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(options.OriginalesPath)) return options.OriginalesPath!;

        // Dentro del árbol de fotos y con punto inicial, igual que ".label-captures": mismo volumen (el reemplazo es atómico),
        // permisos de escritura seguros (el API ya escribe ahí) y fuera de la vista como si fuera una carpeta de cliente.
        string basePath = (config["PhotoSettings:BasePath"] ?? @"F:\Trafico\fotos").TrimEnd('\\', '/');
        return Path.Combine(basePath, ".originales");
    }

    private static void TryDelete(string ruta)
    {
        try
        {
            if (File.Exists(ruta)) File.Delete(ruta);
        }
        catch
        {
        }
    }
}
