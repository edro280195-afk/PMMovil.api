using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dapper;

using Microsoft.Extensions.Caching.Memory;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Las cifras del Tablero del inicio. Es de solo lectura y barato a propósito: son conteos, cada estación se calcula
/// aparte y solo si el usuario puede verla, y el resultado se guarda unos segundos en memoria para que muchos equipos
/// abriendo el inicio a la vez no se traduzcan en la misma consulta repetida.
/// </summary>
public sealed class HomeSummaryService
{
    private readonly BaseRepository _db;
    private readonly IMemoryCache _cache;
    private readonly IModulePermissionService _permissions;
    private readonly ILogger<HomeSummaryService> _logger;

    public HomeSummaryService(
        BaseRepository db,
        IMemoryCache cache,
        IModulePermissionService permissions,
        ILogger<HomeSummaryService> logger)
    {
        _db = db;
        _cache = cache;
        _permissions = permissions;
        _logger = logger;
    }

    private sealed class YardRow
    {
        public long Total { get; set; }
        public long Old { get; set; }
    }

    private sealed class ExitsTodayRow
    {
        public long WithRelease { get; set; }
        public long Manuales { get; set; }
    }

    private sealed class SupervisionRow
    {
        public long Total { get; set; }
        public long Blocked { get; set; }
    }

    /// <summary>Calcula las cinco estaciones en paralelo. Una que falla o que el usuario no ve queda en <c>null</c>.</summary>
    public async Task<HomeSummaryResult> GetAsync(string username, string planta, CancellationToken ct)
    {
        Task<HomeStationSummary?> caseta = StationAsync(TraceStationId.Caseta, username, ct,
            $"caseta:{planta}", () => CasetaAsync(planta, ct));
        Task<HomeStationSummary?> entrada = StationAsync(TraceStationId.Entrada, username, ct,
            "entrada", () => EntradaAsync(ct));
        Task<HomeStationSummary?> revision = StationAsync(TraceStationId.Revision, username, ct,
            $"revision:{username}", () => RevisionAsync(username, ct));
        Task<HomeStationSummary?> relacion = StationAsync(TraceStationId.Relacion, username, ct,
            "relacion", () => RelacionAsync(ct));
        Task<HomeStationSummary?> salida = StationAsync(TraceStationId.Salida, username, ct,
            $"salida:{planta}", () => SalidaAsync(planta, ct));

        await Task.WhenAll(caseta, entrada, revision, relacion, salida);

        return new HomeSummaryResult
        {
            Success = true,
            Caseta = caseta.Result,
            Entrada = entrada.Result,
            Revision = revision.Result,
            Relacion = relacion.Result,
            Salida = salida.Result,
            CacheSeconds = HomeSummaryRules.CacheSeconds
        };
    }

    /// <summary>
    /// Una estación: se comprueba el permiso (nunca se cachea, depende del usuario), luego se busca en memoria y, si no
    /// está, se calcula. Un fallo se registra y deja la estación en <c>null</c>; no se guarda, para reintentar enseguida.
    /// </summary>
    private async Task<HomeStationSummary?> StationAsync(
        string stationId,
        string username,
        CancellationToken ct,
        string cacheKey,
        Func<Task<HomeStationSummary>> compute)
    {
        try
        {
            bool allowed = false;
            foreach (int module in HomeSummaryRules.ModulesOf(stationId))
            {
                if (await _permissions.HasPermissionAsync(username, module, "Lectura", ct))
                {
                    allowed = true;
                    break;
                }
            }

            if (!allowed) return null;

            return await _cache.GetOrCreateAsync($"home:{cacheKey}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(HomeSummaryRules.CacheSeconds);
                return await compute();
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo calcular la cifra de {Station} del Tablero", stationId);
            return null;
        }
    }

    /// <summary>
    /// Caseta: cajas en el patio de la planta (la lista del hub de Caseta: sin fecha de salida y no canceladas) y, como
    /// excepción, las que llevan más de siete días. Las de fecha en cero no cuentan como viejas: no se sabe de cuándo son.
    /// </summary>
    private async Task<HomeStationSummary> CasetaAsync(string planta, CancellationToken ct)
    {
        using IDbConnection conn = _db.CreateConnection();
        YardRow row = await conn.QuerySingleAsync<YardRow>(new CommandDefinition(
            @"SELECT COUNT(*) AS Total,
                     CAST(COALESCE(SUM(CASE WHEN farribo >= '2000-01-01'
                                             AND farribo < NOW() - INTERVAL @days DAY THEN 1 ELSE 0 END), 0) AS SIGNED) AS Old
              FROM caseta
              WHERE (fsalida IS NULL OR fsalida < '2000-01-01')
                AND estatus NOT IN ('CANCELADO', 'CANCELADA')
                AND (@planta = '' OR planta = @planta)",
            new { planta, days = HomeSummaryRules.OldYardDays },
            cancellationToken: ct));

        return HomeSummaryRules.Summary(
            row.Total,
            HomeSummaryRules.ExceptionOf(TraceSignal.Atencion, HomeExceptionCode.OldInYard, row.Old));
    }

    /// <summary>
    /// Entrada: entradas capturadas desde las 00:00 de hoy. «Cuántas esperan» no se puede calcular mientras la entrada no
    /// quede ligada a su caja (traf3.ccasetaid hoy viene vacío), así que no se inventa. Sin excepción.
    /// </summary>
    private async Task<HomeStationSummary> EntradaAsync(CancellationToken ct)
    {
        using IDbConnection conn = _db.CreateConnection();
        long today = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM traf WHERE fcreatrafico >= CURDATE()",
            cancellationToken: ct));
        return HomeSummaryRules.Summary(today);
    }

    /// <summary>
    /// Revisión: lo mío. Las entradas que me asignaron y siguen abiertas (estatus 0 y 1) y las que tomé y dejé sin
    /// cerrar con actividad en los últimos siete días. Como excepción, cuántas de esas entradas tienen una factura con
    /// discrepancias.
    /// </summary>
    private async Task<HomeStationSummary> RevisionAsync(string username, CancellationToken ct)
    {
        using IDbConnection conn = _db.CreateConnection();
        List<string> mine = (await conn.QueryAsync<string>(new CommandDefinition(
            @"SELECT x.ref FROM (
                  SELECT t1.creferencia AS ref
                  FROM traftiemposrevision t1
                  INNER JOIN (
                      SELECT creferencia, MAX(idtiemposrevision) AS maxid
                      FROM traftiemposrevision
                      WHERE usuario = @username
                      GROUP BY creferencia
                  ) latest ON t1.idtiemposrevision = latest.maxid
                  WHERE ((t1.ffinrevision IS NULL OR YEAR(t1.ffinrevision) < 2000)
                         OR COALESCE(t1.estatus_final, 0) NOT IN (1, 2))
                    AND GREATEST(t1.finiciorevision, IFNULL(t1.ffinrevision, t1.finiciorevision))
                        >= NOW() - INTERVAL @days DAY
                  UNION
                  SELECT cref_agrupada AS ref
                  FROM asignaciones_revision
                  WHERE usuario_asignado = @username AND estatus IN (0, 1)
              ) x
              WHERE x.ref IS NOT NULL AND x.ref <> ''",
            new { username, days = HomeSummaryRules.TakenWindowDays },
            cancellationToken: ct))).ToList();

        if (mine.Count == 0) return HomeSummaryRules.Summary(0);

        long withDiscrepancies = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            @"SELECT COUNT(DISTINCT cref_agrupada)
              FROM fact
              WHERE status_rev = 2 AND cref_agrupada IN @refs",
            new { refs = mine },
            cancellationToken: ct));

        return HomeSummaryRules.Summary(
            mine.Count,
            HomeSummaryRules.ExceptionOf(TraceSignal.Alto, HomeExceptionCode.Discrepancies, withDiscrepancies));
    }

    /// <summary>
    /// Relación: la lista que el módulo ofrece al supervisor (estatus por revisar, relación de importación y de IMEX) y,
    /// como excepción, las bloqueadas o con diferencia, que no avanzan sin alguien. CARGANDO no cuenta: la carga sigue en
    /// manos de Bodega.
    /// </summary>
    private async Task<HomeStationSummary> RelacionAsync(CancellationToken ct)
    {
        using IDbConnection conn = _db.CreateConnection();
        SupervisionRow row = await conn.QuerySingleAsync<SupervisionRow>(new CommandDefinition(
            @"SELECT COUNT(*) AS Total,
                     CAST(COALESCE(SUM(CASE WHEN estatus_carga IN @blocked THEN 1 ELSE 0 END), 0) AS SIGNED) AS Blocked
              FROM (
                  SELECT estatus_carga FROM relacion
                  WHERE TIPOP = 'I'
                    AND (estatus_carga IN @statuses OR (estatus_carga = '' AND usucargamcia = 'NO REQUERIDO'))
                  UNION ALL
                  SELECT estatus_carga FROM relacion_imex
                  WHERE TIPOP = 'C'
                    AND (estatus_carga IN @statuses OR (estatus_carga = '' AND usucargamcia = 'NO REQUERIDO'))
              ) u",
            new { statuses = HomeSummaryRules.SupervisionStatuses, blocked = HomeSummaryRules.BlockedStatuses },
            cancellationToken: ct));

        return HomeSummaryRules.Summary(
            row.Total,
            HomeSummaryRules.ExceptionOf(TraceSignal.Alto, HomeExceptionCode.Blocked, row.Blocked));
    }

    /// <summary>
    /// Salida: renglones que salieron hoy de la caseta de la planta (relaciones distintas más cajas sin relación, igual que
    /// el «Hoy» de la pantalla de salida) y, como excepción, cuántas relaciones están listas para salir ahora: autorizadas,
    /// sin nadie editándolas, sin cruce y sin salida registrada, con su caja en el patio.
    /// </summary>
    private async Task<HomeStationSummary> SalidaAsync(string planta, CancellationToken ct)
    {
        using IDbConnection conn = _db.CreateConnection();

        ExitsTodayRow today = await conn.QuerySingleAsync<ExitsTodayRow>(new CommandDefinition(
            @"SELECT CAST(COUNT(DISTINCT CASE WHEN LEFT(IFNULL(relacion, ''), 2) = '01' THEN relacion END) AS SIGNED) AS WithRelease,
                     CAST(IFNULL(SUM(CASE WHEN IFNULL(relacion, '') IN ('', 'NO_RELACION') THEN 1 ELSE 0 END), 0) AS SIGNED) AS Manuales
              FROM caseta
              WHERE fsalida >= CURDATE() AND fsalida < CURDATE() + INTERVAL 1 DAY
                AND estatus = 'SALIDA'
                AND (@planta = '' OR planta = @planta)",
            new { planta },
            cancellationToken: ct));

        long ready = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            @"SELECT COUNT(DISTINCT c.relacion)
              FROM caseta c
              LEFT JOIN relacion r ON r.relno = c.relacion
              LEFT JOIN relacion_imex ri ON ri.relno = c.relacion
              WHERE (c.fsalida IS NULL OR c.fsalida < '2000-01-01')
                AND c.estatus NOT IN ('CANCELADO', 'CANCELADA')
                AND TRIM(IFNULL(c.relacion, '')) <> '' AND TRIM(c.relacion) <> 'NO_RELACION'
                AND (@planta = '' OR c.planta = @planta)
                AND COALESCE(r.estatus_carga, ri.estatus_carga) = 'AUTORIZADA'
                AND IFNULL(COALESCE(r.usuedit, ri.usuedit), '') = ''
                AND IFNULL(YEAR(COALESCE(r.fcruce, ri.fcruce)), 0) < 2000
                AND IFNULL(YEAR(COALESCE(r.salida_caseta, ri.salida_caseta)), 0) < 2000",
            new { planta },
            cancellationToken: ct));

        return HomeSummaryRules.Summary(
            today.WithRelease + today.Manuales,
            HomeSummaryRules.ExceptionOf(TraceSignal.Listo, HomeExceptionCode.ReadyToExit, ready));
    }
}
