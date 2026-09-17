using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Data;
using PMMovil.Api.Hubs;

namespace PMMovil.Api.Services;

public class AutoFinalizerService : BackgroundService
{
    private readonly IServiceProvider _services;

    private readonly ILogger<AutoFinalizerService> _logger;

    private readonly int _hourLocal;

    private readonly int _minuteLocal;

    private readonly string _publicApiBaseUrl;

    public AutoFinalizerService(IServiceProvider services, IConfiguration configuration, ILogger<AutoFinalizerService> logger)
    {
        _services = services;
        _logger = logger;
        _hourLocal = configuration.GetValue<int?>("AutoFinalizer:HourLocal") ?? 17;
        _minuteLocal = configuration.GetValue<int?>("AutoFinalizer:MinuteLocal").GetValueOrDefault();
        _publicApiBaseUrl = configuration["PublicLinks:ApiBaseUrl"] ?? string.Empty;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoFinalizerService iniciado. Ejecutará diariamente a las {H:00}:{M:00}.", _hourLocal, _minuteLocal);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                TimeSpan delay = CalcularEspera();
                _logger.LogInformation("AutoFinalizerService: próxima ejecución en {Delay} (a las {Time}).", delay, DateTime.Now.Add(delay).ToString("yyyy-MM-dd HH:mm"));
                await Task.Delay(delay, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                await EjecutarCierreAsync(stoppingToken);
                continue;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error en AutoFinalizerService. Reintentando en 5 minutos.");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5.0), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    goto end_IL_01ae;
                }
                continue;
            end_IL_01ae:;
            }
            break;
        }
        _logger.LogInformation("AutoFinalizerService detenido.");
    }

    private TimeSpan CalcularEspera()
    {
        DateTime now = DateTime.Now;
        DateTime next = new DateTime(now.Year, now.Month, now.Day, _hourLocal, _minuteLocal, 0, DateTimeKind.Local);
        if (next <= now)
        {
            next = next.AddDays(1.0);
        }
        return next - now;
    }

    private async Task EjecutarCierreAsync(CancellationToken stoppingToken)
    {
        using IServiceScope scope = _services.CreateScope();
        BaseRepository db = scope.ServiceProvider.GetRequiredService<BaseRepository>();
        IHubContext<WarehouseHub> hub = scope.ServiceProvider.GetRequiredService<IHubContext<WarehouseHub>>();
        RevisionNormalCompletionService completionService =
            scope.ServiceProvider.GetRequiredService<RevisionNormalCompletionService>();
        IRevisionReportService reportService =
            scope.ServiceProvider.GetRequiredService<IRevisionReportService>();
        using IDbConnection conn = db.CreateConnection();
        conn.Open();
        List<string> referencias = (await conn.QueryAsync<string>("    SELECT DISTINCT t.creferencia    FROM traftiemposrevision t    WHERE (t.estatus_final IS NULL OR t.estatus_final <> 1)  AND t.finiciorevision IS NOT NULL  AND YEAR(t.finiciorevision) >= 2000  AND (t.ffinrevision IS NULL OR YEAR(t.ffinrevision) < 2000)  AND NOT EXISTS (   SELECT 1 FROM fact f   WHERE f.creferencia = t.creferencia     AND (f.status_rev IS NULL OR f.status_rev <> 1)  )  AND EXISTS (   SELECT 1 FROM fact f2   WHERE f2.creferencia = t.creferencia  );")).ToList();
        if (referencias.Count == 0)
        {
            _logger.LogInformation("AutoFinalizer: no hay entradas 100% completas pendientes de cierre.");
            return;
        }
        _logger.LogInformation("AutoFinalizer: se detectaron {Count} entradas para auto-finalizar.", referencias.Count);
        int totalCerrados = 0;
        foreach (string cref in referencias)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            try
            {
                RevisionNormalCompletionState completionState =
                    await completionService.ObtenerEstadoAsync(conn, cref);
                if (!completionState.PuedeEnviarCorreo)
                {
                    _logger.LogWarning(
                        "AutoFinalizer: se omitió {Cref} porque todavía tiene facturas incompletas.",
                        cref);
                    continue;
                }

                using IDbTransaction transaction = conn.BeginTransaction();
                int rows;
                int asignacionesCerradas = 0;
                try
                {
                    rows = await conn.ExecuteAsync(
                        "UPDATE traftiemposrevision SET ffinrevision = NOW(), estatus_final = 1, usurevisa = COALESCE(NULLIF(usurevisa,''), 'AUTO-17H') WHERE creferencia = @cref AND (estatus_final IS NULL OR estatus_final <> 1) AND finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000 AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000);",
                        new { cref },
                        transaction);
                    if (rows > 0)
                    {
                        await completionService.SincronizarSistemaLegadoAsync(conn, cref, transaction);
                        asignacionesCerradas = await conn.ExecuteAsync(
                            "UPDATE asignaciones_revision SET estatus = 2, fecha_completada = NOW() WHERE cref_agrupada = @cref AND estatus IN (0, 1)",
                            new { cref },
                            transaction);
                    }
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }

                if (rows > 0)
                {
                    totalCerrados += rows;

                    await hub.Clients.All.SendAsync("onRevisionEstadoCambiado", new
                    {
                        creferencia = cref,
                        usuario = "AUTO-17H",
                        estado = "FINALIZADA",
                        fecha = DateTime.Now.ToString("o"),
                        estatusFinal = 1
                    }, stoppingToken);
                    await hub.Clients.All.SendAsync("onRevisionCompletada", cref, "AUTO-17H", stoppingToken);
                    _ = await reportService.EnviarReporteRevisionNormalAsync(cref, _publicApiBaseUrl);
                    _logger.LogInformation(
                        "AutoFinalizer: cerrada {Cref} ({Rows} sesiones, {Assignments} asignaciones).",
                        cref,
                        rows,
                        asignacionesCerradas);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "AutoFinalizer: falló el cierre de {Cref}.", cref);
            }
        }
        _logger.LogInformation("AutoFinalizer: ejecución terminada. Sesiones cerradas: {Total}.", totalCerrados);
    }
}
