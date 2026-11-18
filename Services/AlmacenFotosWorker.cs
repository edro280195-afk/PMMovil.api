namespace PMMovil.Api.Services;

/// <summary>
/// Pasa al 23 las fotos que llegaron a un almacén (en cuanto llegan, no cada tantos segundos) y limpia su almacén caliente
/// de lo que ya cumplió su tiempo. Ver <see cref="AlmacenFotos"/>. Cada almacén tiene el suyo:
/// <see cref="CasetaPhotoSyncWorker"/> y <see cref="ClientePhotoSyncWorker"/>.
/// </summary>
public abstract class AlmacenFotosWorker : BackgroundService
{
    private readonly AlmacenFotos _almacen;
    private readonly ILogger _logger;
    private readonly string _nombre;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _limpiezaCada;

    protected AlmacenFotosWorker(AlmacenFotos almacen, IConfiguration config, string seccion, string nombre, ILogger logger)
    {
        _almacen = almacen;
        _logger = logger;
        _nombre = nombre;

        int seconds = config.GetValue<int?>($"{seccion}:SyncIntervalSeconds") ?? 15;
        _interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 3600));

        int minutos = config.GetValue<int?>($"{seccion}:CleanupIntervalMinutes") ?? 15;
        _limpiezaCada = TimeSpan.FromMinutes(Math.Clamp(minutos, 1, 24 * 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _almacen.MarcarTrabajadorActivo();

        // Leerlo aquí hace la prueba de escritura del almacén caliente al arrancar y deja en el log si quedó activo.
        bool caliente = _almacen.HotCacheActivo;
        _logger.LogInformation(
            "Sincronización de fotos de {Almacen} iniciada. Intervalo máximo: {Interval}; almacén caliente: {Caliente}; limpieza cada {Limpieza}.",
            _nombre,
            _interval,
            caliente ? "activo" : "apagado",
            _limpiezaCada);

        DateTime proximaLimpieza = DateTime.UtcNow.AddMinutes(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _almacen.PublishPendingPhotosAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error general sincronizando fotos de {Almacen}.", _nombre);
            }

            if (DateTime.UtcNow >= proximaLimpieza)
            {
                proximaLimpieza = DateTime.UtcNow + _limpiezaCada;
                try
                {
                    LimpiezaFotosResultado resultado = await _almacen.LimpiarCalienteAsync(DateTime.UtcNow, stoppingToken);
                    if (resultado.HuboCambios)
                    {
                        _logger.LogInformation(
                            "Almacén caliente de {Almacen}: {Desalojadas} foto(s) ya en el 23 se quitaron, {SinCopia} se conservan por no estar confirmadas en el 23, {Vistas} vista(s), {Minis} miniatura(s) y {Marcas} marca(s) vencidas.",
                            _nombre,
                            resultado.Desalojadas,
                            resultado.SinCopiaEn23,
                            resultado.VistasBorradas,
                            resultado.MiniaturasBorradas,
                            resultado.MarcasBorradas);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error limpiando el almacén caliente de fotos de {Almacen}.", _nombre);
                }
            }

            try
            {
                // Despierta en cuanto llega una foto o una baja; si no llega nada, a más tardar en el intervalo.
                await _almacen.EsperarTrabajoAsync(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Sincronización de fotos de {Almacen} detenida.", _nombre);
    }
}

/// <summary>Trabajador del almacén de Entrada Bodega y Revisión (<see cref="ClientePhotoStorage"/>).</summary>
public sealed class ClientePhotoSyncWorker : AlmacenFotosWorker
{
    public ClientePhotoSyncWorker(ClientePhotoStorage storage, IConfiguration config, ILogger<ClientePhotoSyncWorker> logger)
        : base(storage, config, ClientePhotoStorage.Seccion, "Entrada y Revisión", logger)
    {
    }
}
