using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

using Dapper;

using PMMovil.Api.Data;

namespace PMMovil.Api.Services;

/// <summary>
/// Centraliza la validación de cierre de una revisión normal y la escritura de
/// los campos que todavía consume el sistema legado de PMM.
/// </summary>
public sealed class RevisionNormalCompletionService
{
    private sealed class CompletionRow
    {
        public long TotalFacturas { get; set; }
        public long FacturasIncompletas { get; set; }
        public long FacturasConDiscrepancia { get; set; }
        public long FacturasConPartidasIncompletas { get; set; }
    }

    private sealed class CompletionSummaryRow
    {
        public string Reference { get; set; } = string.Empty;
        public long TotalFacturas { get; set; }
        public long FacturasCompletas { get; set; }
        public long TotalPartidas { get; set; }
        public long PartidasRevisadas { get; set; }
        public long FacturasIncompletas { get; set; }
        public long FacturasConDiscrepancia { get; set; }
        public long FacturasConPartidasIncompletas { get; set; }
    }

    /// <summary>
    /// Obtiene el estado real de las facturas directamente de la base de datos.
    /// Los estados distintos de 1 (completa) y 2 (completa con discrepancia)
    /// se consideran incompletos, incluso si el cliente envía otro estado.
    /// </summary>
    public async Task<RevisionNormalCompletionState> ObtenerEstadoAsync(
        IDbConnection connection,
        string creferencia,
        IDbTransaction? transaction = null)
    {
        const string sql = @"
            SELECT
                COUNT(*) AS TotalFacturas,
                SUM(CASE WHEN COALESCE(f.status_rev, 0) NOT IN (1, 2) THEN 1 ELSE 0 END) AS FacturasIncompletas,
                SUM(CASE WHEN f.status_rev = 2 THEN 1 ELSE 0 END) AS FacturasConDiscrepancia,
                SUM(CASE WHEN EXISTS (
                        SELECT 1
                        FROM revdet rd
                        WHERE rd.idfactura = f.idfactura
                          AND COALESCE(rd.revisada, 0) <> 1
                    ) THEN 1 ELSE 0 END) AS FacturasConPartidasIncompletas
            FROM fact f
            WHERE f.cref_agrupada = @cref OR f.creferencia = @cref";

        CompletionRow row = await connection.QuerySingleAsync<CompletionRow>(
            sql,
            new { cref = creferencia },
            transaction);

        return new RevisionNormalCompletionState(
            checked((int)row.TotalFacturas),
            checked((int)row.FacturasIncompletas),
            checked((int)row.FacturasConDiscrepancia),
            checked((int)row.FacturasConPartidasIncompletas));
    }

    /// <summary>
    /// Calcula los mismos indicadores del cierre normal para varias entradas.
    /// El conjunto de referencias llega acotado por el controlador AR.
    /// </summary>
    public async Task<Dictionary<string, RevisionNormalCompletionSummary>> ObtenerResumenesAsync(
        IDbConnection connection,
        IReadOnlyCollection<string> referencias,
        IDbTransaction? transaction = null)
    {
        const string sql = @"
            SELECT
                f.cref_agrupada AS Reference,
                COUNT(DISTINCT f.cnofact) AS TotalFacturas,
                COUNT(DISTINCT CASE WHEN f.status_rev IN (1, 2) THEN f.cnofact END) AS FacturasCompletas,
                COUNT(DISTINCT rd.idrevdet) AS TotalPartidas,
                COUNT(DISTINCT CASE WHEN COALESCE(rd.revisada, 0) = 1 THEN rd.idrevdet END) AS PartidasRevisadas,
                COUNT(DISTINCT CASE WHEN COALESCE(f.status_rev, 0) NOT IN (1, 2) THEN f.cnofact END) AS FacturasIncompletas,
                COUNT(DISTINCT CASE WHEN f.status_rev = 2 THEN f.cnofact END) AS FacturasConDiscrepancia,
                COUNT(DISTINCT CASE WHEN EXISTS (
                    SELECT 1
                    FROM revdet pending
                    WHERE pending.idfactura = f.idfactura
                      AND COALESCE(pending.revisada, 0) <> 1
                ) THEN f.cnofact END) AS FacturasConPartidasIncompletas
            FROM fact f
            LEFT JOIN revdet rd ON rd.idfactura = f.idfactura
            WHERE f.cref_agrupada IN @Referencias
            GROUP BY f.cref_agrupada";

        IEnumerable<CompletionSummaryRow> rows = await connection.QueryAsync<CompletionSummaryRow>(
            sql,
            new { Referencias = referencias },
            transaction);

        return rows.ToDictionary(
            row => row.Reference,
            row => new RevisionNormalCompletionSummary(
                State: new RevisionNormalCompletionState(
                    checked((int)row.TotalFacturas),
                    checked((int)row.FacturasIncompletas),
                    checked((int)row.FacturasConDiscrepancia),
                    checked((int)row.FacturasConPartidasIncompletas)),
                TotalInvoices: checked((int)row.TotalFacturas),
                CompletedInvoices: checked((int)row.FacturasCompletas),
                TotalLines: checked((int)row.TotalPartidas),
                ReviewedLines: checked((int)row.PartidasRevisadas)),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Estado actual de la entrada en el sistema legado (<c>traf.status</c>); 2 o más = ya se revisó físicamente.
    /// Dentro de una transacción se bloquea la fila para que dos "Terminar" simultáneos (doble toque, dos
    /// dispositivos) se serialicen y solo el primero dispare el correo.
    /// </summary>
    public async Task<int> ObtenerStatusEntradaAsync(
        IDbConnection connection,
        string creferencia,
        IDbTransaction? transaction = null)
    {
        string bloqueo = transaction == null ? string.Empty : " FOR UPDATE";
        return await connection.ExecuteScalarAsync<int>(
            $"SELECT COALESCE(MAX(status), 0) FROM traf WHERE creferencia = @cref{bloqueo}",
            new { cref = creferencia },
            transaction);
    }

    /// <summary>
    /// Sincroniza el cierre completo con las columnas que utiliza
    /// <c>frmopera_bodega</c>: <c>traf.status = 2</c>,
    /// <c>traf3.finicia_revision</c> y <c>traf3.ffin_revision</c>.
    /// Es idempotente: se puede ejecutar las veces que haga falta y converge siempre al mismo resultado.
    /// </summary>
    public async Task<LegacyRevisionSyncResult> SincronizarSistemaLegadoAsync(
        IDbConnection connection,
        string creferencia,
        IDbTransaction? transaction = null)
    {
        // Solo se sube a 2: una entrada que ya avanzó (3 en adelante: con relación, en salida...) no debe regresar a
        // "revisada" porque alguien volvió a terminar su revisión después de un desbloqueo.
        int trafRows = await connection.ExecuteAsync(
            @"
                UPDATE traf
                SET status = 2
                WHERE creferencia = @cref
                  AND COALESCE(status, 0) < 2",
            new { cref = creferencia },
            transaction);

        // El inicio cae a la toma de documentos cuando la sesión nunca arrancó el cronómetro (finiciorevision vacío):
        // antes esas entradas quedaban con traf3.finicia_revision en blanco aunque sí se revisaron. Y si no hay
        // ninguna sesión válida se conserva lo que ya tenía traf3 en vez de borrarlo.
        int traf3Rows = await connection.ExecuteAsync(
            @"
                UPDATE traf3
                SET finicia_revision = COALESCE((
                        SELECT MIN(CASE
                                     WHEN t.finiciorevision IS NOT NULL AND YEAR(t.finiciorevision) >= 2000
                                         THEN t.finiciorevision
                                     WHEN t.ftomadocumento IS NOT NULL AND YEAR(t.ftomadocumento) >= 2000
                                         THEN t.ftomadocumento
                                   END)
                        FROM traftiemposrevision t
                        WHERE t.creferencia = @cref
                    ), finicia_revision),
                    ffin_revision = COALESCE((
                        SELECT MAX(t.ffinrevision)
                        FROM traftiemposrevision t
                        WHERE t.creferencia = @cref
                          AND t.ffinrevision IS NOT NULL
                          AND YEAR(t.ffinrevision) >= 2000
                    ), ffin_revision)
                WHERE creferencia = @cref",
            new { cref = creferencia },
            transaction);

        return new LegacyRevisionSyncResult(trafRows, traf3Rows);
    }
}

/// <summary>
/// Estado de cierre calculado con los datos actuales de la base.
/// </summary>
public sealed record RevisionNormalCompletionState(
    int TotalFacturas,
    int FacturasIncompletas,
    int FacturasConDiscrepancia,
    int FacturasConPartidasIncompletas)
{
    public bool TieneFacturas => TotalFacturas > 0;

    public bool PuedeEnviarCorreo =>
        TieneFacturas &&
        FacturasIncompletas == 0 &&
        FacturasConPartidasIncompletas == 0;

    public int EstatusFinal =>
        !TieneFacturas ||
        FacturasIncompletas > 0 ||
        FacturasConPartidasIncompletas > 0 ||
        FacturasConDiscrepancia > 0
            ? 2
            : 1;

    /// <summary>
    /// Por qué la entrada no se puede marcar como revisada físicamente, en el lenguaje del revisor, o
    /// <c>null</c> si sí se puede. La app lo muestra: terminar sin que la entrada quede cerrada no debe verse
    /// igual que terminar bien.
    /// </summary>
    public string? MotivoIncompleto
    {
        get
        {
            if (PuedeEnviarCorreo)
            {
                return null;
            }

            if (!TieneFacturas)
            {
                return "La entrada no tiene facturas para revisar.";
            }

            var partes = new List<string>();
            if (FacturasIncompletas > 0)
            {
                partes.Add($"{FacturasIncompletas} factura(s) sin completar");
            }

            if (FacturasConPartidasIncompletas > 0)
            {
                partes.Add($"{FacturasConPartidasIncompletas} factura(s) con partidas sin revisar");
            }

            return "La revisión se cerró, pero la entrada no quedó marcada como revisada físicamente: faltan " +
                   string.Join(" y ", partes) + ".";
        }
    }
}

/// <summary>Indicadores de avance más el estado calculado con la regla de cierre normal.</summary>
public sealed record RevisionNormalCompletionSummary(
    RevisionNormalCompletionState State,
    int TotalInvoices,
    int CompletedInvoices,
    int TotalLines,
    int ReviewedLines)
{
    public string ReviewStatus => !State.TieneFacturas
        ? "SIN_REVISAR"
        : State.FacturasIncompletas == 0 && State.FacturasConPartidasIncompletas == 0
            ? State.FacturasConDiscrepancia > 0
                ? "COMPLETA_CON_DISCREPANCIAS"
                : "COMPLETA"
            : CompletedInvoices > 0 || ReviewedLines > 0
                ? "EN_PROGRESO"
                : "SIN_REVISAR";
}

/// <summary>
/// Resultado de las actualizaciones de compatibilidad con el sistema legado.
/// </summary>
public sealed record LegacyRevisionSyncResult(int TrafRows, int Traf3Rows);
