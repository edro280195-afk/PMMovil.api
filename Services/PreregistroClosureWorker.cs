namespace PMMovil.Api.Services;

/// <summary>
/// Reintenta el cierre del QR despues de que la entrada de caseta ya fue
/// confirmada. Mantiene consistente el flujo aunque fallen temporalmente la
/// conexion a pmmutileria o la llamada original desde Android.
/// </summary>
public sealed class PreregistroClosureWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PreregistroClosureWorker> _logger;
    private readonly TimeSpan _interval;

    public PreregistroClosureWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PreregistroClosureWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        int minutos = configuration.GetValue<int?>("Preregistro:ClosureRetryIntervalMinutes") ?? 1;
        _interval = TimeSpan.FromMinutes(Math.Clamp(minutos, 1, 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "PreregistroClosureWorker iniciado. Intervalo: {Interval}.",
            _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                PreregistroService service = scope.ServiceProvider.GetRequiredService<PreregistroService>();
                int resueltos = await service.ProcesarCierresPendientesAsync(stoppingToken);
                if (resueltos > 0)
                {
                    _logger.LogInformation(
                        "PreregistroClosureWorker: {Resueltos} cierres pendientes procesados.",
                        resueltos);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando cierres pendientes de pre-registros.");
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

        _logger.LogInformation("PreregistroClosureWorker detenido.");
    }
}
