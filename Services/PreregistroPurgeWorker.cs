namespace PMMovil.Api.Services;

/// <summary>
/// Purga caseta_preregistro: pasa a EXPIRADO los VIGENTE vencidos y borra
/// fisicamente todo lo que ya paso su fpurga (72 h desde la creacion, se haya
/// usado o no). La auditoria (preregistro_auditoria) NUNCA se toca aqui -- no
/// contiene datos personales y es el unico rastro que queda de un
/// pre-registro purgado.
/// </summary>
public class PreregistroPurgeWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PreregistroPurgeWorker> _logger;
    private readonly TimeSpan _interval;

    public PreregistroPurgeWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<PreregistroPurgeWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        int minutos = config.GetValue<int?>("Preregistro:PurgeIntervalMinutes") ?? 60;
        _interval = TimeSpan.FromMinutes(Math.Clamp(minutos, 5, 1440));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PreregistroPurgeWorker iniciado. Intervalo: {Interval}.", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<PreregistroService>();
                int borrados = await service.PurgarVencidosAsync(stoppingToken);
                if (borrados > 0)
                    _logger.LogInformation("PreregistroPurgeWorker: {Borrados} pre-registros purgados (fpurga vencida).", borrados);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error purgando pre-registros vencidos.");
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

        _logger.LogInformation("PreregistroPurgeWorker detenido.");
    }
}
