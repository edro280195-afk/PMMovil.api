using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Data;
using PMMovil.Api.Hubs;

namespace PMMovil.Api.Services;

/// <summary>
/// Las tres automatizaciones "reversibles" que quedaron aprobadas en el
/// rediseño del Monitor de revisadores: cerrar sesiones fantasma, liberar
/// facturas huérfanas, y auto-finalizar entradas al completarse. Corre cada
/// minuto -- <see cref="AutoFinalizerService"/> sigue existiendo tal cual,
/// como red de seguridad diaria a las 17:00, por si este servicio estuviera
/// caído un rato; ambos son idempotentes (el UPDATE no encuentra filas si
/// el otro ya cerró la entrada, sin condición de carrera real).
///
/// Toda acción queda en revision_actividad con origen "SISTEMA" y se avisa
/// por el mismo WarehouseHub que ya usan las acciones manuales del
/// supervisor -- ningún cliente (Android/Angular) necesita cablear nada
/// nuevo para verlas en tiempo real.
/// </summary>
public class RevisionHygieneService : BackgroundService
{
    private const string Sistema = "SISTEMA";
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _services;
    private readonly ILogger<RevisionHygieneService> _logger;
    private readonly string _publicApiBaseUrl;

    public RevisionHygieneService(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<RevisionHygieneService> logger)
    {
        _services = services;
        _logger = logger;
        _publicApiBaseUrl = configuration["PublicLinks:ApiBaseUrl"] ?? string.Empty;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RevisionHygieneService iniciado. Ciclo cada {Intervalo}.", Intervalo);

        // Primer ciclo casi de inmediato (10s) para no esperar un minuto
        // completo al arrancar la API; los siguientes sí respetan Intervalo.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EjecutarCicloAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RevisionHygieneService: error en un ciclo. Continúa en el siguiente.");
            }

            try
            {
                await Task.Delay(Intervalo, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("RevisionHygieneService detenido.");
    }

    private async Task EjecutarCicloAsync(CancellationToken stoppingToken)
    {
        using IServiceScope scope = _services.CreateScope();
        BaseRepository db = scope.ServiceProvider.GetRequiredService<BaseRepository>();
        IHubContext<WarehouseHub> hub = scope.ServiceProvider.GetRequiredService<IHubContext<WarehouseHub>>();
        MonitorTvRealtimePublisher monitorTvPublisher = scope.ServiceProvider.GetRequiredService<MonitorTvRealtimePublisher>();
        RevisionActivityService activityService = scope.ServiceProvider.GetRequiredService<RevisionActivityService>();
        RevisionNormalCompletionService completionService =
            scope.ServiceProvider.GetRequiredService<RevisionNormalCompletionService>();
        IRevisionReportService reportService =
            scope.ServiceProvider.GetRequiredService<IRevisionReportService>();

        int minutosInactividad = await LeerMinutosInactividadAsync(db);

        await CerrarSesionesFantasmaAsync(db, hub, activityService, minutosInactividad, stoppingToken);
        await LiberarFacturasHuerfanasAsync(db, hub, stoppingToken);
        await AutoFinalizarAlCompletarAsync(
            db,
            hub,
            activityService,
            completionService,
            reportService,
            monitorTvPublisher,
            stoppingToken);
    }

    private static async Task<int> LeerMinutosInactividadAsync(BaseRepository db)
    {
        using IDbConnection conn = db.CreateConnection();
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS monitor_config (
                clave VARCHAR(50) PRIMARY KEY,
                valor VARCHAR(200) NOT NULL,
                fecha_mod DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            )");
        string? valor = await conn.QueryFirstOrDefaultAsync<string?>(
            "SELECT valor FROM monitor_config WHERE clave = 'higiene_inactividad_min'");
        return int.TryParse(valor, out int minutos) ? minutos : 45;
    }

    // ── 1. Sesiones fantasma ────────────────────────────────────────────
    // Timer abierto (con inicio o solo toma de documento) hace más de N
    // minutos, y sin un solo evento en revision_actividad para ese usuario
    // en esa entrada dentro de esos N minutos. Se resuelve con dos consultas
    // simples en vez de un JOIN entre bases -- pmmtraf y pmmutileria pueden
    // vivir en servidores distintos.
    private async Task CerrarSesionesFantasmaAsync(
        BaseRepository db,
        IHubContext<WarehouseHub> hub,
        RevisionActivityService activityService,
        int minutosInactividad,
        CancellationToken stoppingToken)
    {
        using IDbConnection conn = db.CreateConnection();

        // Misma lección que en LiberarFacturasHuerfanasAsync: sin tope
        // superior, esto "cierra" cualquier fila que quedó abierta hace
        // años por algún caso viejo sin resolver, no solo la de hoy. Un
        // límite de 7 días separa "se fue a comer y no cerró" de "esto ya
        // era basura histórica antes de que este servicio existiera".
        string sqlCandidatas = @"
            SELECT DISTINCT t.creferencia, t.usuario
            FROM traftiemposrevision t
            WHERE (t.ffinrevision IS NULL OR YEAR(t.ffinrevision) < 2000)
              AND t.usuario IS NOT NULL AND t.usuario != ''
              AND (
                    (t.finiciorevision IS NOT NULL AND YEAR(t.finiciorevision) >= 2000
                     AND t.finiciorevision < DATE_SUB(NOW(), INTERVAL @minutos MINUTE)
                     AND t.finiciorevision >= DATE_SUB(NOW(), INTERVAL 7 DAY))
                 OR (t.ftomadocumento IS NOT NULL AND YEAR(t.ftomadocumento) >= 2000
                     AND t.ftomadocumento < DATE_SUB(NOW(), INTERVAL @minutos MINUTE)
                     AND t.ftomadocumento >= DATE_SUB(NOW(), INTERVAL 7 DAY))
              )";
        List<(string Creferencia, string Usuario)> candidatas = (await conn.QueryAsync<(string, string)>(
            sqlCandidatas, new { minutos = minutosInactividad })).ToList();

        if (candidatas.Count == 0) return;

        List<string> referencias = candidatas.Select(c => c.Creferencia).Distinct().ToList();

        Dictionary<(string Cref, string Usuario), DateTime> ultimaActividad;
        using (IDbConnection connUtileria = db.CreatePmmutileriaConnection())
        {
            string sqlActividad = @"
                SELECT creferencia, usuario, MAX(fecha) AS ultima
                FROM revision_actividad
                WHERE creferencia IN @referencias
                GROUP BY creferencia, usuario";
            IEnumerable<dynamic> filas = await connUtileria.QueryAsync<dynamic>(sqlActividad, new { referencias });
            ultimaActividad = filas.ToDictionary(
                (dynamic f) => ((string)f.creferencia, (string)f.usuario),
                (dynamic f) => (DateTime)f.ultima);
        }

        DateTime limite = DateTime.Now.AddMinutes(-minutosInactividad);
        List<(string Creferencia, string Usuario)> fantasma = candidatas
            .Where(c => !ultimaActividad.TryGetValue((c.Creferencia, c.Usuario), out DateTime ultima) || ultima < limite)
            .ToList();

        foreach ((string creferencia, string usuario) in fantasma)
        {
            if (stoppingToken.IsCancellationRequested) return;

            int rows = await conn.ExecuteAsync(
                "UPDATE traftiemposrevision SET ffinrevision = NOW(), estatus_final = 0 " +
                "WHERE creferencia = @creferencia AND usuario = @usuario AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)",
                new { creferencia, usuario });
            if (rows == 0) continue;

            await conn.ExecuteAsync("UPDATE fact SET usuedit = '' WHERE usuedit = @usuario", new { usuario });

            string detalle = $"Sin actividad por {minutosInactividad} minutos.";
            await activityService.RegistrarAsync(creferencia, usuario, RevisionActivityTypes.Pausada, detalle: detalle, origen: Sistema);

            await hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync("onRevisionEstadoCambiado", new
            {
                creferencia,
                usuario,
                estado = "PAUSADA",
                fecha = DateTime.Now.ToString("o"),
                estatusFinal = 0
            });
            await hub.Clients.All.SendAsync("onActividadRecibida", "", usuario, "", "");
            await hub.Clients.Group("USER_" + usuario).SendAsync("onCierreForzoso", new
            {
                usuario,
                supervisor = Sistema,
                motivo = detalle,
                mensaje = $"Tu sesión de revisión se cerró sola por {minutosInactividad} minutos sin actividad."
            });

            _logger.LogInformation(
                "RevisionHygieneService: sesión fantasma cerrada — {Cref} / {Usuario}.", creferencia, usuario);
        }
    }

    // ── 2. Facturas huérfanas ───────────────────────────────────────────
    // usuedit marcado pero sin ninguna sesión de revisión abierta para la
    // entrada dueña de esa factura: nadie la está revisando ahora mismo,
    // pero sigue bloqueada para los demás.
    private async Task LiberarFacturasHuerfanasAsync(
        BaseRepository db,
        IHubContext<WarehouseHub> hub,
        CancellationToken stoppingToken)
    {
        using IDbConnection conn = db.CreateConnection();

        // Probado contra un volcado real: sin la ventana de recencia, esta
        // consulta encuentra TODO el historial (aquí, 112 facturas de hasta
        // 2025-05) y las "libera" de un solo golpe al primer arranque -- un
        // aluvión de UPDATE y de onFacturaLiberada por el hub que nadie
        // necesita ver. Acotar a cierres recientes es lo que hace que esto
        // sea "atajar staleness de hoy", no "reescribir el histórico".
        string sqlHuerfanas = @"
            SELECT f.cnofact, f.usuedit, f.cref_agrupada
            FROM fact f
            WHERE f.usuedit IS NOT NULL AND f.usuedit != ''
              AND NOT EXISTS (
                    SELECT 1 FROM traftiemposrevision t
                    WHERE t.creferencia = f.cref_agrupada
                      AND (t.ffinrevision IS NULL OR YEAR(t.ffinrevision) < 2000)
              )
              AND EXISTS (
                    SELECT 1 FROM traftiemposrevision t2
                    WHERE t2.creferencia = f.cref_agrupada
                      AND t2.ffinrevision IS NOT NULL AND YEAR(t2.ffinrevision) >= 2000
                      AND t2.ffinrevision >= DATE_SUB(NOW(), INTERVAL 30 DAY)
              )";
        List<(string Cnofact, string Usuedit, string? CrefAgrupada)> huerfanas =
            (await conn.QueryAsync<(string, string, string?)>(sqlHuerfanas)).ToList();

        if (huerfanas.Count == 0) return;

        foreach ((string cnofact, string usuedit, string? crefAgrupada) in huerfanas)
        {
            if (stoppingToken.IsCancellationRequested) return;

            int rows = await conn.ExecuteAsync(
                "UPDATE fact SET usuedit = '' WHERE cnofact = @cnofact AND usuedit = @usuedit",
                new { cnofact, usuedit });
            if (rows == 0) continue;

            await hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync("onFacturaLiberada", new { cnofact });
            if (!string.IsNullOrWhiteSpace(crefAgrupada))
            {
                await hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync(
                    "onRevisionActualizada",
                    new WarehouseHub.RevisionActualizadaEvent
                    {
                        Entidad = "EDITORES_ENTRADA",
                        Creferencia = crefAgrupada,
                        Usuario = "SISTEMA",
                        Fecha = DateTime.UtcNow.ToString("O"),
                        Cnofacts = new[] { cnofact }
                    });
            }
            _logger.LogInformation(
                "RevisionHygieneService: factura huérfana liberada — {Cnofact} (tenía a {Usuario}).", cnofact, usuedit);
        }
    }

    // ── 3. Auto-finalizar al completar ──────────────────────────────────
    // Misma consulta que AutoFinalizerService.EjecutarCierreAsync, pero
    // corriendo cada minuto en vez de una vez al día -- una entrada que se
    // completa a las 9 AM ya no tiene por qué seguir ocupando el Monitor
    // hasta las 17:00. Marca 'AUTO-INSTANT' (no 'AUTO-17H') para distinguir
    // el origen si algún día hace falta.
    private async Task AutoFinalizarAlCompletarAsync(
        BaseRepository db,
        IHubContext<WarehouseHub> hub,
        RevisionActivityService activityService,
        RevisionNormalCompletionService completionService,
        IRevisionReportService reportService,
        MonitorTvRealtimePublisher monitorTvPublisher,
        CancellationToken stoppingToken)
    {
        using IDbConnection conn = db.CreateConnection();

        // Misma consulta base que AutoFinalizerService (probada en
        // producción, corriendo 1 vez al día). Aquí se agrega el mismo tope
        // de recencia que las otras dos pasadas: corriendo cada minuto en
        // vez de una vez al día, sin él intentaría auto-finalizar cualquier
        // entrada abierta desde hace meses que nunca se cerró por alguna
        // razón, no solo la que se acaba de completar hoy.
        string sqlCandidatas = @"
            SELECT DISTINCT t.creferencia
            FROM traftiemposrevision t
            WHERE (t.estatus_final IS NULL OR t.estatus_final <> 1)
              AND t.finiciorevision IS NOT NULL AND YEAR(t.finiciorevision) >= 2000
              AND t.finiciorevision >= DATE_SUB(NOW(), INTERVAL 30 DAY)
              AND (t.ffinrevision IS NULL OR YEAR(t.ffinrevision) < 2000)
              AND NOT EXISTS (
                    SELECT 1 FROM fact f
                    WHERE f.creferencia = t.creferencia AND (f.status_rev IS NULL OR f.status_rev <> 1)
              )
              AND EXISTS (SELECT 1 FROM fact f2 WHERE f2.creferencia = t.creferencia)";
        List<string> referencias = (await conn.QueryAsync<string>(sqlCandidatas)).ToList();

        foreach (string cref in referencias)
        {
            if (stoppingToken.IsCancellationRequested) return;

            RevisionNormalCompletionState completionState =
                await completionService.ObtenerEstadoAsync(conn, cref);
            if (!completionState.PuedeEnviarCorreo)
            {
                _logger.LogWarning(
                    "RevisionHygieneService: se omitió {Cref} porque todavía tiene facturas incompletas.",
                    cref);
                continue;
            }

            using IDbTransaction transaction = conn.BeginTransaction();
            int rows;
            try
            {
                rows = await conn.ExecuteAsync(@"
                    UPDATE traftiemposrevision
                    SET ffinrevision = NOW(), estatus_final = 1, usurevisa = COALESCE(NULLIF(usurevisa,''), 'AUTO-INSTANT')
                    WHERE creferencia = @cref
                      AND (estatus_final IS NULL OR estatus_final <> 1)
                      AND finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000
                      AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)",
                    new { cref },
                    transaction);
                if (rows > 0)
                {
                    await completionService.SincronizarSistemaLegadoAsync(conn, cref, transaction);
                    await conn.ExecuteAsync(
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
            if (rows == 0) continue;

            long? activityEventId = await activityService.RegistrarAsync(cref, "AUTO-INSTANT", RevisionActivityTypes.Finalizada,
                detalle: "Se completó la última factura y no quedaban discrepancias.", origen: Sistema);

            await hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync("onRevisionEstadoCambiado", new
            {
                creferencia = cref,
                usuario = "AUTO-INSTANT",
                estado = "FINALIZADA",
                fecha = DateTime.Now.ToString("o"),
                estatusFinal = 1
            });
            await hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync(
                "onRevisionCompletada",
                new WarehouseHub.RevisionCompletadaEvent
                {
                    Creferencia = cref,
                    Usuario = "AUTO-INSTANT"
                });
            await monitorTvPublisher.RevisionCompletadaAsync(cref, "AUTO-INSTANT", stoppingToken, activityEventId);
            _ = await reportService.EnviarReporteRevisionNormalAsync(cref, _publicApiBaseUrl);

            _logger.LogInformation("RevisionHygieneService: entrada finalizada al completarse — {Cref}.", cref);
        }
    }
}
