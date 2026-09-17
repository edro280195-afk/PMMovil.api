namespace PMMovil.Api.Services;

public class CasetaPhotoSyncWorker : BackgroundService
{
    private readonly CasetaPhotoStorage _storage;
    private readonly ILogger<CasetaPhotoSyncWorker> _logger;
    private readonly TimeSpan _interval;

    public CasetaPhotoSyncWorker(
        CasetaPhotoStorage storage,
        IConfiguration config,
        ILogger<CasetaPhotoSyncWorker> logger)
    {
        _storage = storage;
        _logger = logger;

        int seconds = config.GetValue<int?>("CasetaPhotoSettings:SyncIntervalSeconds") ?? 15;
        _interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 3600));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CasetaPhotoSyncWorker iniciado. Intervalo: {Interval}.", _interval);

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

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("CasetaPhotoSyncWorker detenido.");
    }
}
