using System;
using System.Data;
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
                        WHERE rd.cnofact = f.cnofact
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
    /// Sincroniza el cierre completo con las columnas que utiliza
    /// <c>frmopera_bodega</c>: <c>traf.status = 2</c>,
    /// <c>traf3.finicia_revision</c> y <c>traf3.ffin_revision</c>.
    /// </summary>
    public async Task<LegacyRevisionSyncResult> SincronizarSistemaLegadoAsync(
        IDbConnection connection,
        string creferencia,
        IDbTransaction? transaction = null)
    {
        int trafRows = await connection.ExecuteAsync(
            @"
                UPDATE traf
                SET status = 2
                WHERE creferencia = @cref",
            new { cref = creferencia },
            transaction);

        int traf3Rows = await connection.ExecuteAsync(
            @"
                UPDATE traf3
                SET finicia_revision = (
                        SELECT MIN(t.finiciorevision)
                        FROM traftiemposrevision t
                        WHERE t.creferencia = @cref
                          AND t.finiciorevision IS NOT NULL
                          AND YEAR(t.finiciorevision) >= 2000
                    ),
                    ffin_revision = (
                        SELECT MAX(t.ffinrevision)
                        FROM traftiemposrevision t
                        WHERE t.creferencia = @cref
                          AND t.ffinrevision IS NOT NULL
                          AND YEAR(t.ffinrevision) >= 2000
                    )
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
}

/// <summary>
/// Resultado de las actualizaciones de compatibilidad con el sistema legado.
/// </summary>
public sealed record LegacyRevisionSyncResult(int TrafRows, int Traf3Rows);
