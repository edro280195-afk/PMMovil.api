using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ClosedXML.Excel;
using Dapper;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Fila ya derivada: agrega los campos que la vista necesita (EsSubdivision,
/// TieneFactura, FueraVentana, Extraordinaria, OrigenApp) a partir de una
/// <see cref="MetricaRevisionRow"/> cruda. Separado de la fila cruda para que
/// las reglas de negocio (ventana, subdivisión, extraordinaria) vivan en un
/// solo lugar y no se repitan entre Kpis/Tendencia/Adopción/Evidencia.
/// </summary>
internal sealed class FilaDerivada
{
    public required MetricaRevisionRow Row { get; init; }
    public bool TieneFactura { get; init; }
    public bool FueraVentana { get; init; }
    public bool EsSubdivision { get; init; }
    public bool Extraordinaria { get; init; }
    /// <summary>cidmanejo = PIPA: no requiere revisión física y no cuenta en el resumen diario (reunión 1-oct-2026).</summary>
    public bool EsPipa { get; init; }
    /// <summary>La entrada llegó después de las 17:00 del día de arribo (queda fuera de la meta diaria).</summary>
    public bool EntradaTarde { get; init; }
    public bool? OrigenApp { get; init; } // null = no aplica (pendiente/no-requiere/subdivisión)
    public int? TiempoMin { get; init; }
}

/// <summary>
/// Estatus de <c>traf.status</c> (catálogo real de <c>pmmtraf.status</c>,
/// verificado por lectura directa 15-sep-2026): 1 pendiente, 2 revisada,
/// 3 no requiere, 4 "entrada revisada" (excluido, igual que el legado),
/// 5 destrucción, 6 salida especial, 7 salida exportación.
/// </summary>
public static class EstatusRevisionCodigo
{
    public const int Pendiente = 1;
    public const int Revisada = 2;
    public const int NoRequiere = 3;
    public const int EntradaRevisada = 4;
    public const int Destruccion = 5;
    public const int SalidaEspecial = 6;
    public const int SalidaExportacion = 7;
}

public class MetricasRevisionService
{
    private readonly BaseRepository _db;

    private sealed class ImexShiftClosureRow
    {
        public string Reviewer { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
    }

    private sealed class ImexReviewerDirectoryRow
    {
        public string UserKey { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    private sealed class ImexReviewSessionRow
    {
        public string Reviewer { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
        public long ActiveSeconds { get; set; }
    }

    private sealed class ImexInvoiceReviewEventRow
    {
        public string Reviewer { get; set; } = string.Empty;
        public string Invoice { get; set; } = string.Empty;
        public bool IsDiscrepancy { get; set; }
    }

    private sealed class ImexPhotoIdsRow
    {
        public string Creferencia { get; set; } = string.Empty;
        public int? DiscrepancyPhotoId { get; set; }
        public int? EvidencePhotoId { get; set; }
    }

    private sealed class MonitorTvEventOriginRow
    {
        public string Creferencia { get; set; } = string.Empty;
        public string? OficinaOrigen { get; set; }
    }

    private sealed class MonitorTvEventDetailRow
    {
        public string Invoice { get; set; } = string.Empty;
        public string? Detail { get; set; }
    }

    public MetricasRevisionService(BaseRepository db)
    {
        _db = db;
    }

    /// <summary>
    /// Trae las filas crudas de <c>traf</c> en un rango de arribo, con los
    /// mismos filtros de columna simple (planta/cliente/revisor) que ya se
    /// pueden resolver en SQL. Lo que requiere campos derivados (origen,
    /// subdivisión, ventana, cross-filter) se aplica después, en memoria —
    /// mismo patrón que <c>MonitorController.GetVelocidadRevisores</c>.
    /// </summary>
    private async Task<List<MetricaRevisionRow>> ObtenerFilasAsync(
        IDbConnection conn, DateTime desde, DateTime hastaInclusive,
        string? planta, string? cliente, string? revisor, bool incluirRevisadas = false,
        DateTime? completionDate = null, bool includeArrivalRangeWithCompletions = false,
        bool requireInvoice = false,
        bool includeActiveReviews = false,
        CancellationToken cancellationToken = default)
    {
        string plantaWhere = planta switch
        {
            "ATLANTA" => " AND t.planta = '2'",
            "IMEX" => " AND t.planta <> '2'",
            _ => ""
        };
        string clienteWhere = string.IsNullOrWhiteSpace(cliente) ? "" : " AND cl.cnombre LIKE @clienteLike";
        // El nombre "canónico" (ver comentario junto al SELECT) es el mismo
        // que se compara aquí, para que filtrar por revisor no dependa de si
        // esa fila trae el usuario de traftiemposrevision o el de fact.
        string revisorWhere = string.IsNullOrWhiteSpace(revisor) ? "" : " AND COALESCE(NULLIF(TRIM(CONCAT(TRIM(ur.nombre), ' ', TRIM(ur.apaterno))), ''), tr.usuarioActivo, tr.usurevisa, fact.usurev) = @revisor";
        const string effectiveReviewStart = @"COALESCE(
                    CASE WHEN YEAR(tr.finiciorevision) >= 2000 THEN tr.finiciorevision END,
                    CASE WHEN YEAR(t3.finicia_revision) >= 2000 THEN t3.finicia_revision END)";
        const string effectiveReviewEnd = @"CASE
                    WHEN YEAR(tr.finiciorevision) >= 2000 AND tr.ffinrevision IS NULL THEN NULL
                    ELSE COALESCE(
                        CASE WHEN YEAR(tr.ffinrevision) >= 2000 THEN tr.ffinrevision END,
                        CASE WHEN YEAR(t3.ffin_revision) >= 2000 THEN t3.ffin_revision END)
                END";
        string reviewStartSelect = completionDate.HasValue ? effectiveReviewStart : "tr.finiciorevision";
        string reviewEndSelect = completionDate.HasValue ? effectiveReviewEnd : "tr.ffinrevision";
        string completionFilter = completionDate.HasValue
            ? $"(t.status = @revisada AND {effectiveReviewEnd} >= @completionStart AND {effectiveReviewEnd} < @completionEnd)"
            : "";
        List<string> datePredicates = [];
        if (completionDate.HasValue)
        {
            if (includeArrivalRangeWithCompletions)
            {
                datePredicates.Add("t.dfechaarribo BETWEEN @desde AND @hasta");
            }
            datePredicates.Add(completionFilter);
        }
        else
        {
            datePredicates.Add("t.dfechaarribo BETWEEN @desde AND @hasta");
        }
        if (includeActiveReviews)
        {
            datePredicates.Add(@"(t.status = @pendiente AND EXISTS (
                    SELECT 1
                    FROM traftiemposrevision tr_active
                    WHERE tr_active.creferencia = t.creferencia
                      AND tr_active.finiciorevision IS NOT NULL
                      AND YEAR(tr_active.finiciorevision) >= 2000
                      AND (tr_active.ffinrevision IS NULL OR YEAR(tr_active.ffinrevision) < 2000)
                ))");
        }
        string dateFilter = " AND (" + string.Join(" OR ", datePredicates) + ")";
        string invoiceFilter = requireInvoice ? " AND fact.fechaFactura IS NOT NULL" : "";

        string sql = @"
            SELECT
                t.creferencia AS Creferencia,
                t.dfechaarribo AS Arribo,
                t.cclienteidtraf AS ClienteId,
                cl.cnombre AS Cliente,
                t.cidmanejo AS Manejo,
                IF(t.planta = '2', 'ATLANTA', 'IMEX') AS Planta,
                t.status AS Status,
                fact.fechaFactura AS FechaFactura,
                fact.fechaPrimeraFactura AS FechaPrimeraFactura,
                fact.total AS TotalFacturas,
                COALESCE(fact.facturasPendientesRevision, 0) AS FacturasPendientesRevision,
                COALESCE(fact.discrepancias, 0) AS Discrepancias,
                COALESCE(uf.lugar, '') AS OficinaOrigen,
                " + reviewStartSelect + @" AS FInicioRevision,
                " + reviewEndSelect + @" AS FFinRevision,
                COALESCE(NULLIF(TRIM(CONCAT(TRIM(ur.nombre), ' ', TRIM(ur.apaterno))), ''), tr.usuarioActivo, tr.usurevisa, fact.usurev) AS Revisor,
                -- IFNULL obligatorio: si esta creferencia no tiene NINGUNA fila en
                -- traftiemposrevision (revision manual pura), el LEFT JOIN deja
                -- tr.TieneFotoApp en NULL, y 'NULL = 1' da NULL en MySQL (no 0) --
                -- Dapper no puede mapear eso a un bool no-nullable.
                (IFNULL(tr.TieneFotoApp, 0) = 1) AS TieneSesionApp,
                -- Igual que el reporte legado REP-S-0025: finicia_revision/ffin_revision
                -- de traf3 se llenan por CUALQUIER metodo (app o manual), a diferencia
                -- de traftiemposrevision que es exclusiva de la app. Sirve para saber
                -- si ESTA referencia (aunque sea subdivision) tiene su propia revision
                -- registrada, en vez de asumir que toda subdivision hereda del padre
                -- sin trabajo propio (Eduardo, 17-sep-2026).
                (YEAR(t3.ffin_revision) >= 2000) AS TieneTiemposPropios,
                fact.observaciones AS Observaciones,
                t3.detlbs AS Peso,
                tdet.cantidad AS Cantidad
            FROM traf t
            LEFT JOIN clientes cl ON cl.cidcte = t.cclienteidtraf
            LEFT JOIN traf3 t3 ON t3.creferencia = t.creferencia
            LEFT JOIN (SELECT creferencia, SUM(ncantidad) AS cantidad FROM trafdet GROUP BY creferencia) tdet ON tdet.creferencia = t.creferencia
            LEFT JOIN (
                SELECT cref_agrupada,
                       MAX(fcrea) AS fechaFactura,
                       MIN(fcrea) AS fechaPrimeraFactura,
                       COUNT(DISTINCT cnofact) AS total,
                       COUNT(DISTINCT CASE WHEN COALESCE(status_rev, 0) = 0 THEN cnofact END) AS facturasPendientesRevision,
                       COUNT(DISTINCT CASE WHEN status_rev = 2 THEN cnofact END) AS discrepancias,
                       SUBSTRING_INDEX(GROUP_CONCAT(NULLIF(TRIM(usucrea), '') ORDER BY fcrea DESC SEPARATOR ','), ',', 1) AS usuarioFactura,
                       SUBSTRING_INDEX(GROUP_CONCAT(DISTINCT NULLIF(TRIM(usurev), '')), ',', 1) AS usurev,
                       SUBSTRING_INDEX(GROUP_CONCAT(DISTINCT NULLIF(TRIM(obsrev2), '')), ',', 1) AS observaciones
                FROM fact
                GROUP BY cref_agrupada
            ) fact ON fact.cref_agrupada = t.creferencia
            LEFT JOIN usuarios uf ON TRIM(uf.claveusuario) = TRIM(fact.usuarioFactura)
            LEFT JOIN (
                -- traftiemposrevision registra una fila CADA VEZ que alguien abre
                -- la entrada en la app, la haya revisado o solo la haya mirado.
                -- Una fila de 'solo mirar' deja finiciorevision en NULL (no arranca
                -- un timer nuevo) pero SI graba su propio ffinrevision -- tomar
                -- MAX(ffinrevision) sobre todas las filas inflaba el tiempo con
                -- aperturas posteriores de dias despues (caso real Eduardo,
                -- 17-sep-2026: 01C263594110000, Kevin revisa en 5 min, luego el
                -- mismo Kevin y despues Eduardo la abren 4 veces mas solo a
                -- mirarla, y el ultimo vistazo -dos dias despues- se contaba como
                -- el fin de la revision). Por eso ffinrevision/usurevisa solo se
                -- toman de filas que TAMBIEN tengan finiciorevision real -- esas
                -- son las que de verdad representan trabajo, no solo apertura.
                -- En una sesion abierta, el responsable esta en usuario; usurevisa
                -- se llena al cerrar y no permite mostrar quien revisa ahora.
                SELECT creferencia,
                       CASE
                           WHEN MAX(CASE WHEN YEAR(finiciorevision) >= 2000
                                   AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                               THEN 1 ELSE 0 END) = 1
                           THEN MAX(CASE WHEN YEAR(finiciorevision) >= 2000
                                   AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                               THEN finiciorevision END)
                           ELSE MIN(CASE WHEN YEAR(finiciorevision) >= 2000 THEN finiciorevision END)
                       END AS finiciorevision,
                       CASE
                           WHEN MAX(CASE WHEN YEAR(finiciorevision) >= 2000
                                   AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                               THEN 1 ELSE 0 END) = 1 THEN NULL
                           ELSE MAX(CASE WHEN YEAR(finiciorevision) >= 2000 THEN ffinrevision END)
                       END AS ffinrevision,
                       SUBSTRING_INDEX(GROUP_CONCAT(
                           CASE WHEN YEAR(finiciorevision) >= 2000
                                     AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                                THEN NULLIF(TRIM(usuario), '') END
                           ORDER BY finiciorevision DESC SEPARATOR ','
                       ), ',', 1) AS usuarioActivo,
                       SUBSTRING_INDEX(GROUP_CONCAT(DISTINCT CASE WHEN YEAR(finiciorevision) >= 2000 THEN NULLIF(TRIM(usurevisa), '') END ORDER BY ffinrevision DESC), ',', 1) AS usurevisa,
                       -- traftiemposrevision trae varias filas placeholder por
                       -- creferencia (reintentos/eventos internos de la app sin
                       -- ftomadocumento real) -- contar solo si la fila existe
                       -- sobrecontaba el origen APP casi 4x (462 vs 145 reales en
                       -- lo que va de septiembre). Solo cuenta si de verdad se
                       -- tomo una foto (Eduardo, 17-sep-2026).
                       MAX(CASE WHEN YEAR(ftomadocumento) >= 2000 THEN 1 ELSE 0 END) AS TieneFotoApp
                FROM traftiemposrevision
                GROUP BY creferencia
            ) tr ON tr.creferencia = t.creferencia
            -- Resuelve el usuario (de traftiemposrevision o de fact, cualquiera
            -- que traiga la fila) contra el catalogo real de usuarios: sin esto,
            -- la MISMA persona aparecia dos veces, una vez como 'alarumbe'
            -- (clave de traftiemposrevision) y otra como 'ARMANDO LARUMBE'
            -- (nombre libre en fact.usurev), reportado por Eduardo 17-sep-2026.
            LEFT JOIN usuarios ur ON TRIM(ur.claveusuario) = TRIM(COALESCE(tr.usuarioActivo, tr.usurevisa, fact.usurev))
            WHERE t.tipop = 'c'
              AND t.estatus <> '9'
              AND (@incluirRevisadas = 1 OR t.status <> @entradaRevisada)
              " + dateFilter + invoiceFilter + plantaWhere + clienteWhere + revisorWhere;

        string? completionStart = completionDate.HasValue
            ? completionDate.Value.Date.ToString("yyyy-MM-dd 00:00:00")
            : null;
        string? completionEnd = completionDate.HasValue
            ? completionDate.Value.Date.AddDays(1).ToString("yyyy-MM-dd 00:00:00")
            : null;
        var command = new CommandDefinition(sql, new
        {
            desde = desde.ToString("yyyy-MM-dd 00:00:00"),
            hasta = hastaInclusive.ToString("yyyy-MM-dd 23:59:59"),
            entradaRevisada = EstatusRevisionCodigo.EntradaRevisada,
            revisada = EstatusRevisionCodigo.Revisada,
            pendiente = EstatusRevisionCodigo.Pendiente,
            incluirRevisadas,
            completionStart,
            completionEnd,
            clienteLike = "%" + cliente + "%",
            revisor
        }, cancellationToken: cancellationToken);
        var rows = await conn.QueryAsync<MetricaRevisionRow>(command);
        return rows.ToList();
    }

    /// <summary>
    /// Regla de ventana confirmada por Eduardo (16-sep-2026): a tiempo si la
    /// factura se capturó antes del día de arribo, o el mismo día antes de
    /// las 17:00. Fuera de esa ventana y aun así revisada = EXTRAORDINARIA.
    /// </summary>
    private static bool CalcularFueraVentana(DateTime arribo, DateTime? fechaFactura)
    {
        if (!fechaFactura.HasValue) return false;
        DateTime arriboDia = arribo.Date;
        DateTime facturaDia = fechaFactura.Value.Date;
        if (facturaDia < arriboDia) return false;
        if (facturaDia == arriboDia && fechaFactura.Value.TimeOfDay <= new TimeSpan(17, 0, 0)) return false;
        return true;
    }

    /// <summary>
    /// Subdivisión: el cliente pide separar bultos de una entrada ya
    /// dispuesta y mandarlos aparte — genera una referencia hija con sufijo
    /// distinto de "0000" (confirmado por Eduardo 16-sep-2026).
    /// Verificado a fondo contra 90 días reales (17-sep-2026): 162/162 tienen
    /// entrada padre, y el 100% de esos padres ya está en un estatus
    /// "resuelto" — 107 REVISADA, 16 NO REQUIERE REVISION FISICA y 39 SALIDA
    /// ESPECIAL. Nunca un padre PENDIENTE. Los 16/39 no contradicen la regla:
    /// una subdivisión también puede pedirse sobre mercancía que nunca
    /// necesitó revisión física, y SALIDA ESPECIAL es simplemente el estatus
    /// al que pasa el padre una vez que ya se dispuso (se sobreescribe sobre
    /// el mismo renglón, no es un historial). Regla cerrada, sin casos sueltos.
    /// </summary>
    private static bool CalcularEsSubdivision(string creferencia)
        => creferencia.Length >= 4 && creferencia[^4..] != "0000";

    private static FilaDerivada Derivar(MetricaRevisionRow row)
    {
        bool tieneFactura = row.FechaFactura.HasValue;
        bool fueraVentana = tieneFactura && CalcularFueraVentana(row.Arribo, row.FechaFactura);
        bool esSubdivision = CalcularEsSubdivision(row.Creferencia);
        // Confirmado por Eduardo (17-sep-2026) al ver datos reales: una factura
        // tardía por sí sola NO hace extraordinaria una revisión — eso es el
        // flujo normal esperando la factura. Solo es EXTRAORDINARIA si, a pesar
        // de estar fuera de ventana, alguien terminó la revisión física ESE
        // MISMO DÍA de arribo (evidencia real de que se apuraron por una
        // urgencia). Antes de este ajuste, en datos de julio 2026 salía el
        // 100% de las revisadas como "extraordinaria" — la regla anterior
        // confundía atraso normal de facturación con urgencia real.
        bool extraordinaria = row.Status == EstatusRevisionCodigo.Revisada && fueraVentana && !esSubdivision
            && row.FFinRevision.HasValue && row.FFinRevision.Value.Date == row.Arribo.Date;
        // ORIGEN ya no se excluye en bloque para toda subdivision: si la
        // subdivision tiene su propia finicia_revision/ffin_revision en traf3
        // (se re-revisó esa referencia hija en concreto, no solo heredó la
        // disposición del padre), sí se le atribuye APP/MANUAL propio
        // (Eduardo, 17-sep-2026, caso real 01C263591200100).
        bool? origenApp = (row.Status == EstatusRevisionCodigo.Revisada && row.TieneTiemposPropios) ? row.TieneSesionApp : (bool?)null;
        // Mismo acotamiento que ya usa MonitorController.GetVelocidadRevisores:
        // el histórico trae sesiones que alguien dejó abiertas (se han visto
        // duraciones de miles de minutos) y duraciones negativas/cero. Fuera de
        // [1,240] min se descarta la DURACIÓN (no la revisión en sí, que sigue
        // contando en Kpis) para no reventar los promedios.
        int? tiempoMinCrudo = (row.FInicioRevision.HasValue && row.FFinRevision.HasValue)
            ? (int)Math.Round((row.FFinRevision.Value - row.FInicioRevision.Value).TotalMinutes)
            : null;
        int? tiempoMin = (tiempoMinCrudo.HasValue && tiempoMinCrudo.Value >= 1 && tiempoMinCrudo.Value <= 240)
            ? tiempoMinCrudo
            : null;

        bool esPipa = string.Equals(row.Manejo?.Trim(), "PIPA", StringComparison.OrdinalIgnoreCase);
        bool entradaTarde = row.Arribo.TimeOfDay > new TimeSpan(17, 0, 0);

        return new FilaDerivada
        {
            Row = row,
            EsPipa = esPipa,
            EntradaTarde = entradaTarde,
            TieneFactura = tieneFactura,
            FueraVentana = fueraVentana,
            EsSubdivision = esSubdivision,
            Extraordinaria = extraordinaria,
            OrigenApp = origenApp,
            TiempoMin = tiempoMin
        };
    }

    private static EvidenciaRowDto ToEvidenceRowDto(FilaDerivada f) => new()
    {
        Creferencia = f.Row.Creferencia,
        Cliente = f.Row.Cliente,
        Arribo = f.Row.Arribo,
        FechaFactura = f.Row.FechaFactura,
        FechaPrimeraFactura = f.Row.FechaPrimeraFactura,
        TieneFactura = f.TieneFactura,
        FueraVentana = f.FueraVentana,
        Manejo = f.Row.Manejo,
        Estatus = f.Row.Status switch
        {
            EstatusRevisionCodigo.NoRequiere => "NO REQUIERE REVISION FISICA",
            EstatusRevisionCodigo.SalidaExportacion => "SALIDA EXPORTACION",
            EstatusRevisionCodigo.Revisada => "REVISADA FISICAMENTE",
            EstatusRevisionCodigo.Destruccion => "DESTRUCCION",
            EstatusRevisionCodigo.SalidaEspecial => "SALIDA ESPECIAL",
            _ => "PENDIENTE REVISION FISICA"
        },
        Origen = f.EsSubdivision ? "SUBDIVISION" : f.OrigenApp == true ? "APP" : f.OrigenApp == false ? "MANUAL" : null,
        Revisor = f.Row.Revisor,
        FechaInicioRevision = f.Row.FInicioRevision,
        FechaFinRevision = f.Row.FFinRevision,
        TiempoMin = f.TiempoMin,
        Extraordinaria = f.Extraordinaria,
        EsSubdivision = f.EsSubdivision,
        FacturasPendientesRevision = f.Row.FacturasPendientesRevision,
        Discrepancias = f.Row.Discrepancias,
        OficinaOrigen = f.Row.OficinaOrigen
    };

    /// <summary>
    /// Usa la misma bitácora, roster, evento y ventana que RevisionAuditService
    /// para que el total del marcador coincida con "Entradas cerradas" del panel.
    /// No se deriva de traf.status: una entrada puede reabrirse después de cerrarse.
    /// </summary>
    private async Task<List<ImexShiftClosureRow>> GetImexShiftClosuresAsync(
        DateTime shiftDate,
        CancellationToken cancellationToken)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        const string sql = @"
            SELECT TRIM(usuario) AS Reviewer,
                   creferencia AS Reference
            FROM revision_actividad
            WHERE fecha >= @start AND fecha < @end
              AND TRIM(usuario) IN @reviewers
              AND tipo = @finalized
            GROUP BY TRIM(usuario), creferencia";

        IEnumerable<ImexShiftClosureRow> rows = await conn.QueryAsync<ImexShiftClosureRow>(
            new CommandDefinition(sql, new
            {
                start = shiftDate.Date,
                end = shiftDate.Date.AddDays(1),
                reviewers = ReviewerRoster.All,
                finalized = RevisionActivityTypes.Finalizada
            }, cancellationToken: cancellationToken));

        return rows.ToList();
    }

    private async Task<List<ImexReviewSessionRow>> GetImexReviewSessionsAsync(
        DateTime shiftDate,
        CancellationToken cancellationToken)
    {
        using IDbConnection conn = _db.CreateConnection();
        const string sql = @"
            SELECT TRIM(usurevisa) AS Reviewer,
                   TRIM(creferencia) AS Reference,
                   CAST(SUM(TIMESTAMPDIFF(SECOND, finiciorevision, ffinrevision)) AS SIGNED) AS ActiveSeconds
            FROM traftiemposrevision
            WHERE ffinrevision >= @start AND ffinrevision < @end
              AND finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000
              AND ffinrevision IS NOT NULL AND YEAR(ffinrevision) >= 2000
              AND TIMESTAMPDIFF(MINUTE, finiciorevision, ffinrevision) BETWEEN 1 AND 240
              AND usurevisa IS NOT NULL AND TRIM(usurevisa) <> ''
            GROUP BY TRIM(usurevisa), TRIM(creferencia)";

        IEnumerable<ImexReviewSessionRow> rows = await conn.QueryAsync<ImexReviewSessionRow>(
            new CommandDefinition(sql, new
            {
                start = shiftDate.Date,
                end = shiftDate.Date.AddDays(1)
            }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private async Task<List<ImexInvoiceReviewEventRow>> GetImexInvoiceReviewEventsAsync(
        DateTime shiftDate,
        CancellationToken cancellationToken)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        const string sql = @"
            SELECT TRIM(usuario) AS Reviewer,
                   TRIM(cnofact) AS Invoice,
                   MAX(tipo = @discrepancy) AS IsDiscrepancy
            FROM revision_actividad
            WHERE fecha >= @start AND fecha < @end
              AND TRIM(usuario) IN @reviewers
              AND tipo IN (@discrepancy, @completed)
              AND COALESCE(TRIM(cnofact), '') <> ''
            GROUP BY TRIM(usuario), TRIM(cnofact)";

        IEnumerable<ImexInvoiceReviewEventRow> rows = await conn.QueryAsync<ImexInvoiceReviewEventRow>(
            new CommandDefinition(sql, new
            {
                start = shiftDate.Date,
                end = shiftDate.Date.AddDays(1),
                reviewers = ReviewerRoster.All,
                discrepancy = RevisionActivityTypes.FacturaDiscrepancia,
                completed = RevisionActivityTypes.FacturaCompletada
            }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<MonitorTvActivityReplayResponse> ObtenerEventosMonitorAsync(
        long afterId,
        DateTime shiftDate,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        const int maximumPageSize = 200;
        int safeLimit = Math.Clamp(limit, 1, maximumPageSize);
        string[] eventTypes = { RevisionActivityTypes.Finalizada, RevisionActivityTypes.FacturaDiscrepancia };
        using IDbConnection activityConnection = _db.CreatePmmutileriaConnection();
        const string latestIdSql = @"
            SELECT COALESCE(MAX(id), 0)
            FROM revision_actividad
            WHERE fecha >= @start AND fecha < @end AND tipo IN @eventTypes";
        long latestId = await activityConnection.ExecuteScalarAsync<long>(new CommandDefinition(
            latestIdSql,
            new
            {
                start = shiftDate.Date,
                end = shiftDate.Date.AddDays(1),
                eventTypes
            },
            cancellationToken: cancellationToken));

        // Cursor cero inicializa desde el último evento actual; no convierte
        // cierres anteriores en avisos nuevos cuando se enciende una pantalla.
        if (afterId <= 0 || afterId >= latestId)
        {
            return new MonitorTvActivityReplayResponse
            {
                Success = true,
                Cursor = latestId
            };
        }

        const string eventsSql = @"
            SELECT id AS EventId,
                   TRIM(creferencia) AS Creferencia,
                   TRIM(usuario) AS Usuario,
                   tipo AS Tipo,
                   NULLIF(TRIM(cnofact), '') AS Cnofact,
                   NULLIF(TRIM(detalle), '') AS Detalle,
                   fecha AS Fecha
            FROM revision_actividad
            WHERE id > @afterId
              AND fecha >= @start AND fecha < @end
              AND tipo IN @eventTypes
            ORDER BY id
            LIMIT @limitPlusOne";
        IEnumerable<MonitorTvActivityEvent> eventRows = await activityConnection.QueryAsync<MonitorTvActivityEvent>(
            new CommandDefinition(eventsSql, new
            {
                afterId,
                start = shiftDate.Date,
                end = shiftDate.Date.AddDays(1),
                eventTypes,
                limitPlusOne = safeLimit + 1
            }, cancellationToken: cancellationToken));
        List<MonitorTvActivityEvent> events = eventRows.ToList();
        bool hasMore = events.Count > safeLimit;
        if (hasMore) events.RemoveAt(events.Count - 1);

        if (events.Count > 0)
        {
            string[] references = events
                .Select(row => row.Creferencia)
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            using IDbConnection photoConnection = _db.CreateConnection();
            const string photoSql = @"
                SELECT TRIM(creferencia) AS Creferencia,
                       MAX(CASE WHEN ntipodocto = 103 THEN idfotos END) AS DiscrepancyPhotoId,
                       MAX(CASE WHEN ntipodocto = 107 THEN idfotos END) AS EvidencePhotoId
                FROM fotos
                WHERE creferencia IN @references AND ntipodocto IN (103, 107)
                GROUP BY TRIM(creferencia)";
            Dictionary<string, ImexPhotoIdsRow> photos = (await photoConnection.QueryAsync<ImexPhotoIdsRow>(
                new CommandDefinition(photoSql, new { references }, cancellationToken: cancellationToken)))
                .ToDictionary(row => row.Creferencia, StringComparer.OrdinalIgnoreCase);
            const string originSql = @"
                SELECT factura.cref_agrupada AS Creferencia,
                       COALESCE(uf.lugar, '') AS OficinaOrigen
                FROM (
                    SELECT cref_agrupada,
                           SUBSTRING_INDEX(GROUP_CONCAT(NULLIF(TRIM(usucrea), '') ORDER BY fcrea DESC SEPARATOR ','), ',', 1) AS usuarioFactura,
                           SUBSTRING_INDEX(GROUP_CONCAT(DISTINCT NULLIF(TRIM(usurev), '')), ',', 1) AS usuarioRevisor
                    FROM fact
                    WHERE cref_agrupada IN @references
                    GROUP BY cref_agrupada
                ) factura
                LEFT JOIN usuarios uf
                    ON TRIM(uf.claveusuario) = TRIM(COALESCE(factura.usuarioFactura, factura.usuarioRevisor))";
            Dictionary<string, string?> origins = (await photoConnection.QueryAsync<MonitorTvEventOriginRow>(
                    new CommandDefinition(originSql, new { references }, cancellationToken: cancellationToken)))
                .GroupBy(row => row.Creferencia, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().OficinaOrigen, StringComparer.OrdinalIgnoreCase);

            foreach (MonitorTvActivityEvent activity in events)
            {
                if (photos.TryGetValue(activity.Creferencia, out ImexPhotoIdsRow? photo))
                {
                    activity.PhotoId = activity.Tipo == RevisionActivityTypes.FacturaDiscrepancia
                        ? photo.DiscrepancyPhotoId
                        : photo.EvidencePhotoId ?? photo.DiscrepancyPhotoId;
                }
                if (origins.TryGetValue(activity.Creferencia, out string? office)) activity.OficinaOrigen = office;
            }

            string[] invoices = events
                .Where(row => row.Tipo == RevisionActivityTypes.FacturaDiscrepancia && !string.IsNullOrWhiteSpace(row.Cnofact))
                .Select(row => row.Cnofact!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (invoices.Length > 0)
            {
                const string detailSql = @"
                    SELECT TRIM(cnofact) AS Invoice,
                           MAX(NULLIF(TRIM(discrepancias), '')) AS Detail
                    FROM fact
                    WHERE cnofact IN @invoices
                    GROUP BY TRIM(cnofact)";
                Dictionary<string, string?> details = (await photoConnection.QueryAsync<MonitorTvEventDetailRow>(
                        new CommandDefinition(detailSql, new { invoices }, cancellationToken: cancellationToken)))
                    .GroupBy(row => row.Invoice, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Detail, StringComparer.OrdinalIgnoreCase);
                foreach (MonitorTvActivityEvent activity in events.Where(row => row.Tipo == RevisionActivityTypes.FacturaDiscrepancia))
                {
                    if (activity.Cnofact != null && details.TryGetValue(activity.Cnofact, out string? detail) && !string.IsNullOrWhiteSpace(detail))
                    {
                        activity.Detalle = detail;
                    }
                }
            }
        }

        long cursor = events.Count > 0 ? events[^1].EventId : latestId;
        if (!hasMore) cursor = Math.Max(cursor, latestId);
        return new MonitorTvActivityReplayResponse
        {
            Success = true,
            Cursor = cursor,
            HasMore = hasMore,
            Events = events
        };
    }

    private static async Task<Dictionary<string, string>> GetReviewerNamesAsync(
        IDbConnection conn,
        IEnumerable<string> reviewerKeys,
        CancellationToken cancellationToken)
    {
        string[] keys = reviewerKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keys.Length == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        const string sql = @"
            SELECT TRIM(claveusuario) AS UserKey,
                   TRIM(CONCAT_WS(' ', NULLIF(TRIM(nombre), ''), NULLIF(TRIM(apaterno), ''))) AS DisplayName
            FROM usuarios
            WHERE TRIM(claveusuario) IN @reviewers";

        IEnumerable<ImexReviewerDirectoryRow> rows = await conn.QueryAsync<ImexReviewerDirectoryRow>(
            new CommandDefinition(sql, new { reviewers = keys }, cancellationToken: cancellationToken));

        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.UserKey) && !string.IsNullOrWhiteSpace(row.DisplayName))
            .GroupBy(row => row.UserKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().DisplayName.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static ImexShiftMetrics CreateShiftMetrics(
        IReadOnlyCollection<FilaDerivada> completedRows,
        IReadOnlyCollection<ImexShiftClosureRow> closureEvents,
        IReadOnlyCollection<ImexReviewSessionRow> reviewSessions,
        IReadOnlyCollection<ImexInvoiceReviewEventRow> invoiceReviewEvents,
        DateTime shiftDate,
        IReadOnlyDictionary<string, string> reviewerNames)
    {
        Dictionary<string, FilaDerivada> rowsByReference = completedRows
            .GroupBy(f => f.Row.Creferencia.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var closureGroups = closureEvents
            .Where(row => !string.IsNullOrWhiteSpace(row.Reviewer) && !string.IsNullOrWhiteSpace(row.Reference))
            .GroupBy(row => row.Reviewer.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                ReviewerKey = group.Key,
                References = group.Select(row => row.Reference.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .ToList();

        Dictionary<string, string[]> referencesByReviewer = closureGroups
            .ToDictionary(group => group.ReviewerKey, group => group.References, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ImexReviewSessionRow[]> sessionsByReviewer = reviewSessions
            .Where(row => !string.IsNullOrWhiteSpace(row.Reviewer) && !string.IsNullOrWhiteSpace(row.Reference))
            .GroupBy(row => row.Reviewer.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ImexInvoiceReviewEventRow[]> invoicesByReviewer = invoiceReviewEvents
            .Where(row => !string.IsNullOrWhiteSpace(row.Reviewer))
            .GroupBy(row => row.Reviewer.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        string[] reviewerKeys = ReviewerRoster.All
            .Concat(closureGroups.Select(group => group.ReviewerKey))
            .Concat(reviewSessions.Select(row => row.Reviewer))
            .Concat(invoiceReviewEvents.Select(row => row.Reviewer))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        List<ImexReviewerShiftScore> reviewerScores = reviewerKeys
            .Select(reviewerKey =>
            {
                string[] reviewerReferences = referencesByReviewer.GetValueOrDefault(reviewerKey) ?? Array.Empty<string>();
                List<FilaDerivada> reviewerRows = reviewerReferences
                    .Select(reference => rowsByReference.GetValueOrDefault(reference))
                    .Where(row => row is not null)
                    .Select(row => row!)
                    .ToList();
                Dictionary<string, long> timedReferences = (sessionsByReviewer.GetValueOrDefault(reviewerKey) ?? Array.Empty<ImexReviewSessionRow>())
                    .Where(row => reviewerReferences.Contains(row.Reference, StringComparer.OrdinalIgnoreCase))
                    .GroupBy(row => row.Reference, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Sum(row => row.ActiveSeconds), StringComparer.OrdinalIgnoreCase);
                int[] reviewMinutes = timedReferences.Values
                    .Select(seconds => (int)Math.Round(seconds / 60d))
                    .Where(minutes => minutes is >= 1 and <= 240)
                    .ToArray();
                ImexInvoiceReviewEventRow[] reviewerInvoices = invoicesByReviewer.GetValueOrDefault(reviewerKey) ?? Array.Empty<ImexInvoiceReviewEventRow>();
                string reviewerName = reviewerNames.TryGetValue(reviewerKey, out string? displayName)
                    && !string.IsNullOrWhiteSpace(displayName)
                    ? displayName
                    : reviewerKey;

                return new ImexReviewerShiftScore
                {
                    Reviewer = reviewerName,
                    CompletedReviews = reviewerReferences.Length,
                    SameDayArrivalAndInvoiceEntries = reviewerRows.Count(row =>
                        row.Row.Arribo.Date == shiftDate.Date
                        && row.Row.FechaPrimeraFactura?.Date == shiftDate.Date),
                    Discrepancies = reviewerInvoices
                        .Where(row => row.IsDiscrepancy)
                        .Select(row => row.Invoice)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    InvoicesReviewed = reviewerInvoices
                        .Select(row => row.Invoice)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    TimedReviews = reviewMinutes.Length,
                    AverageMinutes = reviewMinutes.Length > 0
                        ? (int)Math.Round(reviewMinutes.Average())
                        : null
                };
            })
            .OrderByDescending(score => score.CompletedReviews)
            .ThenByDescending(score => score.Discrepancies + score.TimedReviews)
            .ThenBy(score => score.AverageMinutes ?? int.MaxValue)
            .ThenBy(score => score.Reviewer, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string[] closureReferences = closureEvents
            .Where(row => !string.IsNullOrWhiteSpace(row.Reference))
            .Select(row => row.Reference.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        List<FilaDerivada> teamRows = closureReferences
            .Select(reference => rowsByReference.GetValueOrDefault(reference))
            .Where(row => row is not null)
            .Select(row => row!)
            .ToList();
        HashSet<string> closedReferences = new(closureReferences, StringComparer.OrdinalIgnoreCase);
        int[] teamReviewMinutes = reviewSessions
            .Where(row => closedReferences.Contains(row.Reference))
            .GroupBy(row => row.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(group => (int)Math.Round(group.Sum(row => row.ActiveSeconds) / 60d))
            .Where(minutes => minutes is >= 1 and <= 240)
            .ToArray();

        return new ImexShiftMetrics
        {
            TotalCompletedReviews = closureReferences.Length,
            SameDayArrivalAndInvoiceEntries = teamRows.Count(f =>
                f.Row.Arribo.Date == shiftDate.Date
                && f.Row.FechaPrimeraFactura?.Date == shiftDate.Date),
            TeamAverageMinutes = teamReviewMinutes.Length > 0
                ? (int)Math.Round(teamReviewMinutes.Average())
                : 0,
            TeamTimedReviews = teamReviewMinutes.Length,
            ReviewerScores = reviewerScores
        };
    }

    /// <summary>
    /// Mismas llaves de criterio que usa el mockup HTML y que usará Angular al
    /// hacer clic en una tarjeta/segmento — un solo lugar define qué significa
    /// cada una, para que el cross-filter nunca se desincronice entre vistas.
    /// </summary>
    private static bool CumpleCriterio(FilaDerivada f, string? criterio)
    {
        if (string.IsNullOrEmpty(criterio)) return true;
        int status = f.Row.Status;
        return criterio switch
        {
            "total" => true,
            "noRequiere" => status == EstatusRevisionCodigo.NoRequiere,
            "requiere" => status != EstatusRevisionCodigo.NoRequiere
                          && status != EstatusRevisionCodigo.SalidaExportacion
                          && status != EstatusRevisionCodigo.Destruccion
                          && status != EstatusRevisionCodigo.SalidaEspecial,
            "conFactura" => f.TieneFactura,
            "fueraVentana" => f.TieneFactura && f.FueraVentana,
            "despuesCinco" => f.EntradaTarde || (f.TieneFactura && f.FueraVentana),
            "comentarios" => !string.IsNullOrWhiteSpace(f.Row.Observaciones),
            "revisadas" => status == EstatusRevisionCodigo.Revisada,
            "pendientes" => status == EstatusRevisionCodigo.Pendiente,
            "extraordinarias" => f.Extraordinaria,
            "viaApp" => f.OrigenApp == true,
            "viaManual" => f.OrigenApp == false,
            "subdivisiones" => f.EsSubdivision,
            _ => true
        };
    }

    private static IEnumerable<FilaDerivada> AplicarOrigenYCrossFilter(
        IEnumerable<FilaDerivada> filas, string? origen, string? crossFilter)
    {
        IEnumerable<FilaDerivada> q = filas;
        if (origen == "app") q = q.Where(f => !f.EsSubdivision && f.OrigenApp == true);
        else if (origen == "manual") q = q.Where(f => !f.EsSubdivision && f.OrigenApp == false);
        if (!string.IsNullOrEmpty(crossFilter)) q = q.Where(f => CumpleCriterio(f, crossFilter));
        return q;
    }

    /// <summary>
    /// El resumen diario no cuenta las entradas PIPA (no requieren revisión
    /// física, reunión 1-oct-2026). El export sí las conserva —trae la columna
    /// MANEJO— para no cambiar el formato que Calidad ya conoce.
    /// </summary>
    private static IEnumerable<FilaDerivada> SinPipa(IEnumerable<FilaDerivada> filas) => filas.Where(f => !f.EsPipa);

    private static KpisDto CalcularKpis(List<FilaDerivada> filas, int pipaExcluidas)
    {
        int total = filas.Count;
        int noRequiere = filas.Count(f => f.Row.Status == EstatusRevisionCodigo.NoRequiere);
        int expo = filas.Count(f => f.Row.Status == EstatusRevisionCodigo.SalidaExportacion);
        int destruccion = filas.Count(f => f.Row.Status == EstatusRevisionCodigo.Destruccion);
        int salidaEspecial = filas.Count(f => f.Row.Status == EstatusRevisionCodigo.SalidaEspecial);
        int requiere = total - noRequiere - expo - destruccion - salidaEspecial;
        int conFactura = filas.Count(f => f.TieneFactura);
        int fueraVentana = filas.Count(f => f.TieneFactura && f.FueraVentana);
        int conComentarios = filas.Count(f => !string.IsNullOrWhiteSpace(f.Row.Observaciones));
        int revisadas = filas.Count(f => f.Row.Status == EstatusRevisionCodigo.Revisada);
        int pendientes = filas.Count(f => f.Row.Status == EstatusRevisionCodigo.Pendiente);
        int extraordinarias = filas.Count(f => f.Extraordinaria);
        int subdivisiones = filas.Count(f => f.EsSubdivision);
        int viaApp = filas.Count(f => f.OrigenApp == true);
        int viaManual = filas.Count(f => f.OrigenApp == false);
        int baseApp = viaApp + viaManual;

        int entradasTarde = filas.Count(f => f.EntradaTarde);
        int despuesCinco = filas.Count(f => f.EntradaTarde || (f.TieneFactura && f.FueraVentana));
        int dentroVentana = conFactura - fueraVentana;
        // Reunión 1-oct-2026 (Eduardo confirmó): el % se mide solo sobre
        // facturas capturadas dentro de ventana; las tardías no cuentan ni a
        // favor ni en contra.
        int facturaVentanaRevisada = filas.Count(f => f.TieneFactura && !f.FueraVentana && f.Row.Status == EstatusRevisionCodigo.Revisada);

        return new KpisDto
        {
            Total = total,
            NoRequiere = noRequiere,
            Requiere = requiere,
            ConFactura = conFactura,
            FueraVentana = fueraVentana,
            ConComentarios = conComentarios,
            Revisadas = revisadas,
            Pendientes = pendientes,
            Extraordinarias = extraordinarias,
            Subdivisiones = subdivisiones,
            ViaApp = viaApp,
            ViaManual = viaManual,
            Pipa = pipaExcluidas,
            EntradasTarde = entradasTarde,
            DespuesDeCinco = despuesCinco,
            ConFacturaEnVentana = dentroVentana,
            RevisadasConFacturaEnVentana = facturaVentanaRevisada,
            PctFacturaRevisada = dentroVentana > 0 ? Math.Round(100.0 * facturaVentanaRevisada / dentroVentana, 1) : 0,
            PctVentana = conFactura > 0 ? Math.Round(100.0 * dentroVentana / conFactura, 1) : 0,
            PctApp = baseApp > 0 ? Math.Round(100.0 * viaApp / baseApp, 1) : 0
        };
    }

    private static AntiguedadDto CalcularAntiguedad(List<FilaDerivada> filas, DateTime ahora)
    {
        var pendientes = filas.Where(f => f.Row.Status == EstatusRevisionCodigo.Pendiente);
        var dto = new AntiguedadDto();
        foreach (var f in pendientes)
        {
            double horas = (ahora - f.Row.Arribo).TotalHours;
            if (horas < 4) dto.Menos4h++;
            else if (horas < 24) dto.Entre4y24h++;
            else dto.Mas24h++;
        }
        return dto;
    }

    private static List<RevisorStatDto> CalcularRevisores(List<FilaDerivada> filas)
    {
        return filas
            .Where(f => f.Row.Status == EstatusRevisionCodigo.Revisada && !string.IsNullOrWhiteSpace(f.Row.Revisor))
            .GroupBy(f => f.Row.Revisor!)
            .Select(g =>
            {
                var conTiempo = g.Where(f => f.TiempoMin.HasValue).ToList();
                var baseApp = g.Where(f => !f.EsSubdivision).ToList();
                double? pctApp = baseApp.Count > 0 ? Math.Round(100.0 * baseApp.Count(f => f.OrigenApp == true) / baseApp.Count, 1) : null;
                return new RevisorStatDto
                {
                    Usuario = g.Key,
                    Cantidad = g.Count(),
                    TiempoPromedioMin = conTiempo.Count > 0 ? Math.Round(conTiempo.Average(f => f.TiempoMin!.Value), 1) : 0,
                    PctApp = pctApp
                };
            })
            .OrderByDescending(r => r.Cantidad)
            .ToList();
    }

    public async Task<MetricasResumenResponse> ObtenerResumenAsync(
        DateTime fechaInicio, DateTime fechaFin, string? planta, string? cliente,
        string? revisor, string? origen, string? crossFilter, DateTime? hoyReferencia = null)
    {
        using IDbConnection conn = _db.CreateConnection();

        // Rango principal (el que el usuario eligió en el filtro de fecha).
        List<MetricaRevisionRow> filasRango = await ObtenerFilasAsync(conn, fechaInicio, fechaFin, planta, cliente, revisor);
        List<FilaDerivada> todasRango = filasRango.Select(Derivar).ToList();
        int pipaExcluidas = todasRango.Count(f => f.EsPipa);
        List<FilaDerivada> derivadasRango = AplicarOrigenYCrossFilter(SinPipa(todasRango), origen, crossFilter).ToList();

        // Ventana rodante de 14 días para tendencia/adopción — independiente
        // del rango elegido, pero con los MISMOS filtros de planta/cliente/
        // revisor/origen/cross-filter, para que "seleccionar algo en un
        // gráfico filtre a los demás" (pedido explícito de Eduardo, estilo
        // Power BI) también alcance a estas dos gráficas.
        // hoyReferencia solo lo usan las pruebas (BD local con datos viejos); el controller nunca lo manda.
        DateTime hoy = (hoyReferencia ?? DateTime.Today).Date;
        DateTime desde14 = hoy.AddDays(-13);
        List<MetricaRevisionRow> filas14 = await ObtenerFilasAsync(conn, desde14, hoy, planta, cliente, revisor);
        List<FilaDerivada> derivadas14 = AplicarOrigenYCrossFilter(SinPipa(filas14.Select(Derivar)), origen, crossFilter).ToList();

        var tendencia = new List<TrendPointDto>();
        var adopcion = new List<AdopcionPointDto>();
        for (int i = 13; i >= 0; i--)
        {
            DateTime dia = hoy.AddDays(-i);
            var filasDia = derivadas14.Where(f => f.Row.Arribo.Date == dia.Date).ToList();
            if (i <= 6)
            {
                tendencia.Add(new TrendPointDto
                {
                    Fecha = dia.ToString("yyyy-MM-dd"),
                    Recibidas = filasDia.Count,
                    Revisadas = filasDia.Count(f => f.Row.Status == EstatusRevisionCodigo.Revisada)
                });
            }
            adopcion.Add(new AdopcionPointDto
            {
                Fecha = dia.ToString("yyyy-MM-dd"),
                ViaApp = filasDia.Count(f => f.OrigenApp == true),
                ViaManual = filasDia.Count(f => f.OrigenApp == false)
            });
        }

        return new MetricasResumenResponse
        {
            FilasConsideradas = derivadasRango.Count,
            Kpis = CalcularKpis(derivadasRango, pipaExcluidas),
            Tendencia = tendencia,
            Adopcion = adopcion,
            Antiguedad = CalcularAntiguedad(derivadasRango, DateTime.Now),
            Revisores = CalcularRevisores(derivadasRango)
        };
    }

    /// <summary>
    /// Carga las entradas de la ventana visible, las revisiones activas de
    /// cualquier arribo y los cierres IMEX del día, aunque su arribo sea anterior.
    /// </summary>
    public async Task<ImexMonitorResponse> GetImexMonitorDataAsync(
        DateTime arrivalStart,
        DateTime arrivalEnd,
        int limitPerDay,
        CancellationToken cancellationToken = default)
    {
        DateTime shiftDate = arrivalEnd.Date;
        using IDbConnection conn = _db.CreateConnection();
        List<MetricaRevisionRow> rows = await ObtenerFilasAsync(
            conn,
            arrivalStart,
            arrivalEnd,
            planta: "IMEX",
            cliente: null,
            revisor: null,
            incluirRevisadas: true,
            completionDate: shiftDate,
            includeArrivalRangeWithCompletions: true,
            requireInvoice: true,
            includeActiveReviews: true,
            cancellationToken: cancellationToken);

        List<FilaDerivada> derivedRows = SinPipa(rows.Select(Derivar)).ToList();
        List<FilaDerivada> arrivalEntries = derivedRows
            .Where(f => f.Row.Arribo.Date >= arrivalStart.Date && f.Row.Arribo.Date <= arrivalEnd.Date)
            .OrderByDescending(f => f.Row.Arribo)
            .ToList();
        List<FilaDerivada> activeEntries = derivedRows
            .Where(f => f.Row.Status == EstatusRevisionCodigo.Pendiente
                && f.Row.FInicioRevision.HasValue
                && !f.Row.FFinRevision.HasValue)
            .ToList();
        List<FilaDerivada> completedTodayEntries = derivedRows
            .Where(f => f.Row.Status == EstatusRevisionCodigo.Revisada
                && f.TieneFactura
                && f.Row.FFinRevision?.Date == shiftDate)
            .ToList();
        List<FilaDerivada> monitorEntries = arrivalEntries
            .Concat(activeEntries)
            .Concat(completedTodayEntries)
            .GroupBy(f => f.Row.Creferencia)
            .Select(group => group.First())
            .ToList();
        List<FilaDerivada> limitedArrivalEntries = arrivalEntries
            .GroupBy(f => f.Row.Arribo.Date)
            .SelectMany(day => day.Take(limitPerDay))
            .ToList();
        List<EvidenciaRowDto> items = limitedArrivalEntries
            .Concat(activeEntries)
            .Concat(completedTodayEntries)
            .GroupBy(f => f.Row.Creferencia)
            .Select(group => group.First())
            .OrderByDescending(f => f.Row.Arribo)
            .Select(ToEvidenceRowDto)
            .ToList();

        List<FilaDerivada> completedRows = derivedRows
            .Where(f => f.Row.Status == EstatusRevisionCodigo.Revisada
                && f.TieneFactura
                && f.Row.FFinRevision?.Date == shiftDate)
            .ToList();
        List<ImexShiftClosureRow> closureEvents = await GetImexShiftClosuresAsync(shiftDate, cancellationToken);
        Task<List<ImexReviewSessionRow>> reviewSessionsTask = GetImexReviewSessionsAsync(shiftDate, cancellationToken);
        Task<List<ImexInvoiceReviewEventRow>> invoiceReviewEventsTask = GetImexInvoiceReviewEventsAsync(shiftDate, cancellationToken);
        await Task.WhenAll(reviewSessionsTask, invoiceReviewEventsTask);
        List<ImexReviewSessionRow> reviewSessions = await reviewSessionsTask;
        List<ImexInvoiceReviewEventRow> invoiceReviewEvents = await invoiceReviewEventsTask;
        Dictionary<string, string> reviewerNames = await GetReviewerNamesAsync(
            conn,
            ReviewerRoster.All
                .Concat(closureEvents.Select(row => row.Reviewer))
                .Concat(reviewSessions.Select(row => row.Reviewer))
                .Concat(invoiceReviewEvents.Select(row => row.Reviewer)),
            cancellationToken);
        ImexShiftMetrics shiftMetrics = CreateShiftMetrics(
            completedRows,
            closureEvents,
            reviewSessions,
            invoiceReviewEvents,
            shiftDate,
            reviewerNames);

        return new ImexMonitorResponse
        {
            Success = true,
            Total = monitorEntries.Count,
            Truncado = items.Count < monitorEntries.Count,
            Items = items,
            ShiftMetrics = shiftMetrics
        };
    }

    public async Task<EvidenciaResponse> ObtenerEvidenciaAsync(
        DateTime fechaInicio, DateTime fechaFin, string? planta, string? cliente,
        string? revisor, string? origen, string? crossFilter, int limite = 1000,
        bool incluirRevisadas = false, bool includeActiveReviews = false,
        CancellationToken cancellationToken = default)
    {
        using IDbConnection conn = _db.CreateConnection();
        List<MetricaRevisionRow> filas = await ObtenerFilasAsync(
            conn, fechaInicio, fechaFin, planta, cliente, revisor, incluirRevisadas,
            includeActiveReviews: includeActiveReviews,
            cancellationToken: cancellationToken);
        List<FilaDerivada> derivadas = AplicarOrigenYCrossFilter(SinPipa(filas.Select(Derivar)), origen, crossFilter)
            .OrderByDescending(f => f.Row.Arribo)
            .ToList();

        bool truncado = derivadas.Count > limite;
        List<EvidenciaRowDto> items = derivadas.Take(limite).Select(ToEvidenceRowDto).ToList();

        return new EvidenciaResponse { Total = derivadas.Count, Truncado = truncado, Items = items };
    }

    /// <summary>
    /// Mismas 20 columnas, mismo orden, que TIEMPOS DE REVISION DE MERCANCIA
    /// (S-0025) — verificadas contra el query real de REP-S-0025 en la BD
    /// "reportes" (15-sep-2026). No se renombra ni se reordena nada de eso:
    /// Calidad ya conoce ese formato. Lo nuevo (ORIGEN, SUBDIVISION,
    /// EXTRAORDINARIA) se agrega solo al final, como columnas extra.
    /// Pedido explícito de Eduardo (16-sep-2026): "no hay que cambiarles nada
    /// porque se van a asustar".
    /// </summary>
    private async Task<List<FilaDerivada>> ObtenerFilasParaExportarAsync(
        DateTime fechaInicio, DateTime fechaFin, string? planta, string? cliente,
        string? revisor, string? origen, string? crossFilter)
    {
        using IDbConnection conn = _db.CreateConnection();
        List<MetricaRevisionRow> filas = await ObtenerFilasAsync(conn, fechaInicio, fechaFin, planta, cliente, revisor);
        return AplicarOrigenYCrossFilter(filas.Select(Derivar), origen, crossFilter)
            .OrderByDescending(f => f.Row.Arribo)
            .ToList();
    }

    private static string EstatusTextoExport(FilaDerivada f) => f.Row.Status switch
    {
        EstatusRevisionCodigo.NoRequiere => "NO REQUIERE REVISION FISICA",
        EstatusRevisionCodigo.SalidaExportacion => "SALIDA EXPORTACION",
        EstatusRevisionCodigo.Revisada => "REVISADA FISICAMENTE",
        EstatusRevisionCodigo.Destruccion => "DESTRUCCION",
        EstatusRevisionCodigo.SalidaEspecial => "SALIDA ESPECIAL",
        _ => "PENDIENTE REVISION FISICA"
    };

    public async Task<string> ExportarCsvAsync(
        DateTime fechaInicio, DateTime fechaFin, string? planta, string? cliente,
        string? revisor, string? origen, string? crossFilter)
    {
        List<FilaDerivada> derivadas = await ObtenerFilasParaExportarAsync(fechaInicio, fechaFin, planta, cliente, revisor, origen, crossFilter);

        static string Csv(string? v) => v is null ? "" : "\"" + v.Replace("\"", "\"\"") + "\"";
        static string Fdt(DateTime? d) => d.HasValue ? d.Value.ToString("dd/MM/yy HH:mm:ss") : "-/-/-";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", new[]
        {
            "ARRIBO","ENTRADA","TRAFICO","FACTURA","FECHA FACTURA","TOTAL","FECHA AGRUPADO","CLAVE","NOMBRE",
            "MANEJO","PESO","CANTIDAD","PLANTA","FINICIA_REVISION","FFIN_REVISION","TIEMPO","TIEMPO TRANSCURRIDO","REVISO",
            "ESTATUS REVISION","OBSERVACIONES DE REVISION",
            "ORIGEN","SUBDIVISION","EXTRAORDINARIA"
        }));

        foreach (FilaDerivada f in derivadas)
        {
            var r = f.Row;
            string estatus = r.Status switch
            {
                EstatusRevisionCodigo.NoRequiere => "NO REQUIERE REVISION FISICA",
                EstatusRevisionCodigo.SalidaExportacion => "SALIDA EXPORTACION",
                EstatusRevisionCodigo.Revisada => "REVISADA FISICAMENTE",
                EstatusRevisionCodigo.Destruccion => "DESTRUCCION",
                EstatusRevisionCodigo.SalidaEspecial => "SALIDA ESPECIAL",
                _ => "PENDIENTE REVISION FISICA"
            };
            string origenCol = f.EsSubdivision ? "" : (f.OrigenApp == true ? "APP" : f.OrigenApp == false ? "MANUAL" : "");
            int diasTranscurridos = Math.Max(0, (int)Math.Round(((r.FFinRevision ?? DateTime.Now) - r.Arribo).TotalDays));
            string tiempo = f.TiempoMin.HasValue ? $"{f.TiempoMin.Value / 60:00}:{f.TiempoMin.Value % 60:00}" : "00:00";

            sb.AppendLine(string.Join(",", new[]
            {
                Fdt(r.Arribo), Csv(r.Creferencia), "", Csv(r.TotalFacturas > 0 ? r.Creferencia : ""),
                r.FechaFactura.HasValue ? Fdt(r.FechaFactura) : "-/-/-",
                r.TotalFacturas.ToString(), "", Csv(r.ClienteId), Csv(r.Cliente),
                Csv(r.Manejo), r.Peso?.ToString() ?? "", r.Cantidad?.ToString() ?? "", Csv(r.Planta),
                Fdt(r.FInicioRevision), Fdt(r.FFinRevision), tiempo, diasTranscurridos.ToString(),
                Csv(r.Revisor), Csv(estatus), Csv(r.Observaciones),
                origenCol, f.EsSubdivision ? "SI" : "NO", f.Extraordinaria ? "SI" : "NO"
            }));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Mismo contenido y mismas 20+3 columnas que <see cref="ExportarCsvAsync"/>,
    /// pero como .xlsx real (ClosedXML, ya usado en <c>ReportService.cs</c>) en
    /// vez de texto separado por comas. Pedido de Eduardo (17-sep-2026): el
    /// CSV se presta a que Excel en configuración regional en español lo abra
    /// mal (usa punto y coma como separador de listas, no coma) y a que las
    /// fechas/números lleguen como texto plano en vez de celdas reales.
    /// </summary>
    public async Task<byte[]> ExportarXlsxAsync(
        DateTime fechaInicio, DateTime fechaFin, string? planta, string? cliente,
        string? revisor, string? origen, string? crossFilter)
    {
        List<FilaDerivada> derivadas = await ObtenerFilasParaExportarAsync(fechaInicio, fechaFin, planta, cliente, revisor, origen, crossFilter);

        using var wb = new XLWorkbook();
        wb.Properties.Author = "PMMovil";
        var ws = wb.Worksheets.Add("Metricas Revision");

        string[] columnas =
        {
            "ARRIBO","ENTRADA","TRAFICO","FACTURA","FECHA FACTURA","TOTAL","FECHA AGRUPADO","CLAVE","NOMBRE",
            "MANEJO","PESO","CANTIDAD","PLANTA","FINICIA_REVISION","FFIN_REVISION","TIEMPO","TIEMPO TRANSCURRIDO","REVISO",
            "ESTATUS REVISION","OBSERVACIONES DE REVISION","ORIGEN","SUBDIVISION","EXTRAORDINARIA"
        };
        for (int c = 0; c < columnas.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = columnas[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0x00, 0x33, 0x8D);
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        const string fmtFecha = "dd/mm/yy hh:mm";
        int fila = 2;
        foreach (FilaDerivada f in derivadas)
        {
            var r = f.Row;
            int diasTranscurridos = Math.Max(0, (int)Math.Round(((r.FFinRevision ?? DateTime.Now) - r.Arribo).TotalDays));
            string tiempo = f.TiempoMin.HasValue ? $"{f.TiempoMin.Value / 60:00}:{f.TiempoMin.Value % 60:00}" : "00:00";
            string origenCol = f.EsSubdivision ? "" : (f.OrigenApp == true ? "APP" : f.OrigenApp == false ? "MANUAL" : "");

            int col = 1;
            ws.Cell(fila, col++).Value = r.Arribo; ws.Cell(fila, col - 1).Style.DateFormat.Format = fmtFecha;
            ws.Cell(fila, col++).Value = r.Creferencia;
            ws.Cell(fila, col++).Value = "";
            ws.Cell(fila, col++).Value = r.TotalFacturas > 0 ? r.Creferencia : "";
            if (r.FechaFactura.HasValue) { ws.Cell(fila, col).Value = r.FechaFactura.Value; ws.Cell(fila, col).Style.DateFormat.Format = fmtFecha; }
            else ws.Cell(fila, col).Value = "-/-/-";
            col++;
            ws.Cell(fila, col++).Value = r.TotalFacturas;
            ws.Cell(fila, col++).Value = "";
            ws.Cell(fila, col++).Value = r.ClienteId ?? "";
            ws.Cell(fila, col++).Value = r.Cliente ?? "";
            ws.Cell(fila, col++).Value = r.Manejo ?? "";
            ws.Cell(fila, col++).Value = r.Peso.HasValue ? (double)r.Peso.Value : 0;
            ws.Cell(fila, col++).Value = r.Cantidad.HasValue ? (double)r.Cantidad.Value : 0;
            ws.Cell(fila, col++).Value = r.Planta;
            if (r.FInicioRevision.HasValue) { ws.Cell(fila, col).Value = r.FInicioRevision.Value; ws.Cell(fila, col).Style.DateFormat.Format = fmtFecha; }
            else ws.Cell(fila, col).Value = "-/-/-";
            col++;
            if (r.FFinRevision.HasValue) { ws.Cell(fila, col).Value = r.FFinRevision.Value; ws.Cell(fila, col).Style.DateFormat.Format = fmtFecha; }
            else ws.Cell(fila, col).Value = "-/-/-";
            col++;
            ws.Cell(fila, col++).Value = tiempo;
            ws.Cell(fila, col++).Value = diasTranscurridos;
            ws.Cell(fila, col++).Value = r.Revisor ?? "";
            ws.Cell(fila, col++).Value = EstatusTextoExport(f);
            ws.Cell(fila, col++).Value = r.Observaciones ?? "";
            ws.Cell(fila, col++).Value = origenCol;
            ws.Cell(fila, col++).Value = f.EsSubdivision ? "SI" : "NO";
            ws.Cell(fila, col++).Value = f.Extraordinaria ? "SI" : "NO";

            if (fila % 2 == 0) ws.Row(fila).Style.Fill.BackgroundColor = XLColor.FromArgb(0xF8, 0xFA, 0xFE);
            fila++;
        }

        ws.Columns().AdjustToContents(1, 60);
        ws.SheetView.FreezeRows(1);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
