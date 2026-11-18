namespace PMMovil.Api.Services;

/// <summary>
/// Pasa al 23 las fotos de caseta que llegaron (en cuanto llegan, no cada tantos segundos) y limpia el almacén caliente
/// de lo que ya cumplió su tiempo. Ver <see cref="CasetaPhotoStorage"/>.
/// </summary>
public class CasetaPhotoSyncWorker : BackgroundService
{
    private readonly CasetaPhotoStorage _storage;
    private readonly ILogger<CasetaPhotoSyncWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _limpiezaCada;

    public CasetaPhotoSyncWorker(
        CasetaPhotoStorage storage,
        IConfiguration config,
        ILogger<CasetaPhotoSyncWorker> logger)
    {
        _storage = storage;
        _logger = logger;

        int seconds = config.GetValue<int?>("CasetaPhotoSettings:SyncIntervalSeconds") ?? 15;
        _interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 3600));

        int minutos = config.GetValue<int?>("CasetaPhotoSettings:CleanupIntervalMinutes") ?? 15;
        _limpiezaCada = TimeSpan.FromMinutes(Math.Clamp(minutos, 1, 24 * 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leerlo aquí hace la prueba de escritura del almacén caliente al arrancar y deja en el log si quedó activo.
        bool caliente = _storage.HotCacheActivo;
        _logger.LogInformation(
            "CasetaPhotoSyncWorker iniciado. Intervalo máximo: {Interval}; almacén caliente: {Caliente}; limpieza cada {Limpieza}.",
            _interval,
            caliente ? "activo" : "apagado",
            _limpiezaCada);

        DateTime proximaLimpieza = DateTime.UtcNow.AddMinutes(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _storage.PublishPendingPhotosAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error general sincronizando fotos de caseta.");
            }

            if (DateTime.UtcNow >= proximaLimpieza)
            {
                proximaLimpieza = DateTime.UtcNow + _limpiezaCada;
                try
                {
                    CasetaLimpiezaResultado resultado = await _storage.LimpiarCalienteAsync(DateTime.UtcNow, stoppingToken);
                    if (resultado.HuboCambios)
                    {
                        _logger.LogInformation(
                            "Almacén caliente de Caseta: {Desalojadas} foto(s) ya en el 23 se quitaron, {SinCopia} se conservan por no estar confirmadas en el 23, {Vistas} vista(s), {Minis} miniatura(s) y {Marcas} marca(s) vencidas.",
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
                    _logger.LogError(ex, "Error limpiando el almacén caliente de fotos de caseta.");
                }
            }

            try
            {
                // Despierta en cuanto llega una foto o una baja; si no llega nada, a más tardar en el intervalo.
                await _storage.EsperarTrabajoAsync(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("CasetaPhotoSyncWorker detenido.");
    }
}
