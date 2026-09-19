using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dapper;

using Microsoft.Extensions.Logging;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Consulta el agregado de solo lectura usado por la pantalla pública de TV.
/// Las consultas están separadas de MonitorController para que el enlace no
/// herede accidentalmente permisos ni acciones del monitor administrativo.
/// </summary>
public sealed class MonitorTvPublicService
{
    private readonly BaseRepository _db;
    private readonly ILogger<MonitorTvPublicService> _logger;

    public MonitorTvPublicService(
        BaseRepository db,
        ILogger<MonitorTvPublicService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MonitorTvSnapshot> ObtenerSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IDbConnection connection = _db.CreateConnection();

            List<MonitorTvRevisor> reviewers = await ObtenerRevisoresAsync(connection, cancellationToken);
            MonitorTvJornada shift = await ObtenerJornadaAsync(connection, cancellationToken);
            List<MonitorTvSolicitud> requests = await ObtenerSolicitudesAsync(connection, cancellationToken);

            return new MonitorTvSnapshot
            {
                GeneradoEn = DateTime.Now,
                Revisores = reviewers,
                Jornada = shift,
                Solicitudes = requests,
                // La cola pendiente no forma parte del tablero TV: puede crecer
                // durante toda la jornada y bloquear la rotación de escenas.
                ColaSinAsignar = Array.Empty<MonitorTvEntrada>(),
            };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error al construir el snapshot público de Monitor TV");
            throw;
        }
    }

    private async Task<List<MonitorTvRevisor>> ObtenerRevisoresAsync(IDbConnection connection, CancellationToken cancellationToken)
    {
        string sql = @"
            SELECT
                t.creferencia AS Creferencia,
                DATE_FORMAT(MAX(CASE WHEN t.finiciorevision >= '2000-01-01' THEN t.finiciorevision END), '%Y-%m-%d %H:%i:%s') AS InicioRevision,
                DATE_FORMAT(MAX(CASE WHEN t.ffinrevision >= '2000-01-01' THEN t.ffinrevision END), '%Y-%m-%d %H:%i:%s') AS FinRevision,
                DATE_FORMAT(MAX(CASE WHEN t.ftomadocumento >= '2000-01-01' THEN t.ftomadocumento END), '%Y-%m-%d %H:%i:%s') AS TomaDocumento,
                MAX(COALESCE(t.estatus_final, 0)) AS EstatusFinal,
                COALESCE(
                    (SELECT usuario FROM traftiemposrevision
                     WHERE creferencia = t.creferencia
                       AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                     ORDER BY finiciorevision DESC, ftomadocumento DESC LIMIT 1),
                    (SELECT usuario FROM traftiemposrevision
                     WHERE creferencia = t.creferencia AND usuario IS NOT NULL AND usuario != ''
                     ORDER BY idtiemposrevision DESC LIMIT 1)
                ) AS Usuario,
                MAX(c.cnombre) AS NombreCliente,
                MAX(td.ubicacion) AS Ubicacion,
                COUNT(DISTINCT f.cnofact) AS TotalFacturas,
                COUNT(DISTINCT CASE WHEN f.status_rev = 1 THEN f.cnofact END) AS FacturasCompletas,
                COUNT(DISTINCT CASE WHEN f.status_rev = 2 THEN f.cnofact END) AS FacturasDiscrepancia,
                (SELECT discrepancias FROM fact
                 WHERE cref_agrupada = t.creferencia AND status_rev = 2
                 ORDER BY dfecha DESC LIMIT 1) AS UltimaDiscrepancia,
                (SELECT MAX(CASE WHEN rd.lhazmat = 1 THEN 1 ELSE 0 END)
                 FROM revdet rd WHERE rd.cref = t.creferencia) AS Hazmat,
                (SELECT MAX(CASE WHEN r2.STATUS != 'COMPLETA' THEN 1 ELSE 0 END)
                 FROM traf2 t2
                 INNER JOIN relacion r2 ON t2.nrelacion = r2.relno
                 WHERE t2.CREF_ORIGEN = t.creferencia) AS HasIncompleteRelations,
                (SELECT COALESCE(SUM(TIMESTAMPDIFF(SECOND, finiciorevision, ffinrevision)), 0)
                 FROM traftiemposrevision t2
                 WHERE t2.creferencia = t.creferencia
                   AND t2.finiciorevision IS NOT NULL AND YEAR(t2.finiciorevision) >= 2000
                   AND t2.ffinrevision IS NOT NULL AND YEAR(t2.ffinrevision) >= 2000
                   AND t2.ffinrevision >= t2.finiciorevision
                   AND t2.ffinrevision <= NOW()
                   AND TIMESTAMPDIFF(MINUTE, t2.finiciorevision, t2.ffinrevision) BETWEEN 1 AND 240
                ) AS SegundosAcumulados,
                (SELECT DATE_FORMAT(MAX(t3.finiciorevision), '%Y-%m-%d %H:%i:%s')
                 FROM traftiemposrevision t3
                 WHERE t3.creferencia = t.creferencia
                   AND t3.finiciorevision IS NOT NULL AND YEAR(t3.finiciorevision) >= 2000
                   AND (t3.ffinrevision IS NULL OR YEAR(t3.ffinrevision) < 2000)
                   AND t3.finiciorevision >= DATE_SUB(NOW(), INTERVAL 18 HOUR)
                ) AS InicioSesionActual
            FROM traftiemposrevision t
            LEFT JOIN fact f ON t.creferencia = f.cref_agrupada
            LEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0
            LEFT JOIN trafdet td ON td.creferencia = t.creferencia
            WHERE (
                (t.ffinrevision IS NULL OR YEAR(t.ffinrevision) < 2000)
                AND t.usuario IS NOT NULL AND t.usuario != ''
                AND (
                    (t.finiciorevision IS NOT NULL AND YEAR(t.finiciorevision) >= 2000
                     AND t.finiciorevision >= DATE_SUB(NOW(), INTERVAL 18 HOUR))
                    OR
                    (t.ftomadocumento IS NOT NULL AND YEAR(t.ftomadocumento) >= 2000
                     AND t.ftomadocumento >= DATE_SUB(NOW(), INTERVAL 1 HOUR))
                )
            )
            OR (
                t.ffinrevision IS NOT NULL AND YEAR(t.ffinrevision) >= 2000
                AND t.ffinrevision >= DATE_SUB(NOW(), INTERVAL 15 MINUTE)
                AND t.estatus_final = 1
            )
            GROUP BY t.creferencia
            ORDER BY COALESCE(MAX(t.finiciorevision), MAX(t.ftomadocumento)) DESC";

        IEnumerable<MonitorTvRevisorRow> rows = await connection.QueryAsync<MonitorTvRevisorRow>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
        List<MonitorTvRevisorRow> rowList = rows.ToList();
        List<string> references = rowList
            .Select(row => row.Creferencia)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Dictionary<string, string> states = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (int Total, int Reviewed)> parts = new(StringComparer.OrdinalIgnoreCase);
        if (references.Count > 0)
        {
            IEnumerable<MonitorTvStateRow> stateRows = await connection.QueryAsync<MonitorTvStateRow>(new CommandDefinition(
                @"SELECT creferencia AS Creferencia,
                         CASE
                           WHEN EXISTS (
                             SELECT 1 FROM traftiemposrevision abierta
                             WHERE abierta.creferencia = historial.creferencia
                               AND (abierta.ffinrevision IS NULL OR YEAR(abierta.ffinrevision) < 2000)
                               AND abierta.finiciorevision IS NOT NULL
                               AND YEAR(abierta.finiciorevision) >= 2000
                           ) THEN 'EN_REVISION'
                           WHEN EXISTS (
                             SELECT 1 FROM traftiemposrevision abierta
                             WHERE abierta.creferencia = historial.creferencia
                               AND (abierta.ffinrevision IS NULL OR YEAR(abierta.ffinrevision) < 2000)
                               AND abierta.ftomadocumento IS NOT NULL
                               AND YEAR(abierta.ftomadocumento) >= 2000
                           ) THEN 'DOCUMENTOS_TOMADOS'
                           WHEN EXISTS (
                             SELECT 1 FROM traftiemposrevision cerrada
                             WHERE cerrada.creferencia = historial.creferencia
                               AND cerrada.ffinrevision IS NOT NULL
                               AND YEAR(cerrada.ffinrevision) >= 2000
                               AND cerrada.ffinrevision >= DATE_SUB(NOW(), INTERVAL 15 MINUTE)
                               AND cerrada.estatus_final = 1
                           ) THEN 'FINALIZADA'
                           ELSE 'INACTIVA'
                         END AS Estado
                  FROM traftiemposrevision historial
                  WHERE creferencia IN @references
                  GROUP BY creferencia",
                new { references },
                cancellationToken: cancellationToken));
            foreach (MonitorTvStateRow row in stateRows)
            {
                states[row.Creferencia] = row.Estado;
            }

            IEnumerable<MonitorTvPartsRow> partRows = await connection.QueryAsync<MonitorTvPartsRow>(new CommandDefinition(
                @"SELECT f.cref_agrupada AS Creferencia,
                         COUNT(DISTINCT rd.idrevdet) AS Total,
                         COUNT(DISTINCT CASE WHEN rd.revisada = 1 THEN rd.idrevdet END) AS Reviewed
                  FROM fact f
                  LEFT JOIN revdet rd ON rd.cnofact = f.cnofact
                  WHERE f.cref_agrupada IN @references
                  GROUP BY f.cref_agrupada",
                new { references },
                cancellationToken: cancellationToken));
            foreach (MonitorTvPartsRow row in partRows)
            {
                parts[row.Creferencia] = (row.Total, row.Reviewed);
            }
        }

        return rowList.Select(row => new MonitorTvRevisor
        {
            Creferencia = row.Creferencia,
            Usuario = row.Usuario ?? string.Empty,
            NombreCliente = row.NombreCliente ?? string.Empty,
            Ubicacion = row.Ubicacion ?? string.Empty,
            InicioRevision = ParseDate(row.InicioRevision),
            FinRevision = ParseDate(row.FinRevision),
            TomaDocumento = ParseDate(row.TomaDocumento),
            EstatusFinal = row.EstatusFinal,
            TotalFacturas = row.TotalFacturas,
            FacturasCompletas = row.FacturasCompletas,
            TotalPartidas = parts.TryGetValue(row.Creferencia, out (int Total, int Reviewed) partCount) ? partCount.Total : 0,
            PartidasRevisadas = parts.TryGetValue(row.Creferencia, out partCount) ? partCount.Reviewed : 0,
            FacturasDiscrepancia = row.FacturasDiscrepancia,
            UltimaDiscrepancia = row.UltimaDiscrepancia ?? string.Empty,
            Hazmat = row.Hazmat,
            RelacionCerrada = row.HasIncompleteRelations is null or 0,
            SegundosAcumulados = row.SegundosAcumulados,
            InicioSesionActual = ParseDate(row.InicioSesionActual),
            Estado = states.TryGetValue(row.Creferencia, out string? state) ? state : "INACTIVA",
            RecienTerminada = states.TryGetValue(row.Creferencia, out string? recentState) && recentState == "FINALIZADA",
        }).ToList();
    }

    private static async Task<MonitorTvJornada> ObtenerJornadaAsync(IDbConnection connection, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            @"CREATE TABLE IF NOT EXISTS monitor_config (
                clave VARCHAR(50) PRIMARY KEY,
                valor VARCHAR(200) NOT NULL,
                fecha_mod DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
              )",
            cancellationToken: cancellationToken));
        IEnumerable<MonitorTvConfigRow> configRows = await connection.QueryAsync<MonitorTvConfigRow>(new CommandDefinition(
            "SELECT clave AS Clave, valor AS Valor FROM monitor_config WHERE clave IN ('meta_diaria', 'turno_inicio_hora', 'turno_fin_hora')",
            cancellationToken: cancellationToken));
        Dictionary<string, string> config = configRows.ToDictionary(row => row.Clave, row => row.Valor, StringComparer.OrdinalIgnoreCase);
        int meta = ReadConfig(config, "meta_diaria", 50);
        int shiftStart = ReadConfig(config, "turno_inicio_hora", 8);
        int shiftEnd = ReadConfig(config, "turno_fin_hora", 17);
        string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        IEnumerable<MonitorTvClosureRow> closureRows = await connection.QueryAsync<MonitorTvClosureRow>(new CommandDefinition(
            @"SELECT t.creferencia AS Creferencia,
                     DATE_FORMAT(t.ffinrevision, '%Y-%m-%dT%H:%i:%s') AS Fecha,
                     COALESCE(NULLIF(TRIM(t.usurevisa), ''), NULLIF(TRIM(t.usuario), ''), 'N/D') AS Usuario,
                     MAX(c.cnombre) AS NombreCliente
              FROM traftiemposrevision t
              LEFT JOIN fact f ON f.cref_agrupada = t.creferencia
              LEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0
              WHERE t.ffinrevision IS NOT NULL AND YEAR(t.ffinrevision) >= 2000
                AND t.estatus_final = 1
                AND DATE(t.ffinrevision) = @today
              GROUP BY t.creferencia, t.ffinrevision, t.usurevisa, t.usuario
              ORDER BY t.ffinrevision ASC",
            new { today },
            cancellationToken: cancellationToken));

        return new MonitorTvJornada
        {
            Fecha = today,
            TurnoInicioHora = shiftStart,
            TurnoFinHora = shiftEnd,
            Meta = meta,
            Cierres = closureRows.Select(row => new MonitorTvCierre
            {
                Creferencia = row.Creferencia,
                Fecha = row.Fecha,
                Usuario = row.Usuario ?? "N/D",
                NombreCliente = row.NombreCliente ?? string.Empty,
                Automatico = (row.Usuario ?? string.Empty).StartsWith("AUTO", StringComparison.OrdinalIgnoreCase),
            }).ToList(),
        };
    }

    private static async Task<List<MonitorTvSolicitud>> ObtenerSolicitudesAsync(IDbConnection connection, CancellationToken cancellationToken)
    {
        IEnumerable<MonitorTvRequestRow> rows = await connection.QueryAsync<MonitorTvRequestRow>(new CommandDefinition(
            @"SELECT id_desbloqueo AS Id,
                     cnofact AS Cnofact,
                     usuario_solicita AS Usuario,
                     motivo AS Motivo,
                     fecha_solicitud AS FechaSolicitud
              FROM rev_desbloqueos
              WHERE estatus = 0
              ORDER BY fecha_solicitud DESC",
            cancellationToken: cancellationToken));

        return rows.Select(row => new MonitorTvSolicitud
        {
            Id = row.Id,
            Cnofact = row.Cnofact.StartsWith("ENTRY_", StringComparison.OrdinalIgnoreCase)
                ? row.Cnofact[6..]
                : row.Cnofact,
            Usuario = row.Usuario,
            Motivo = row.Motivo,
            FechaSolicitud = row.FechaSolicitud,
        }).ToList();
    }

    private static int ReadConfig(IReadOnlyDictionary<string, string> config, string key, int fallback)
    {
        return config.TryGetValue(key, out string? value) && int.TryParse(value, out int result)
            ? result
            : fallback;
    }

    private static DateTime? ParseDate(string? value)
    {
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result)
            ? result
            : null;
    }

    private sealed class MonitorTvRevisorRow
    {
        public string Creferencia { get; set; } = string.Empty;
        public string? InicioRevision { get; set; }
        public string? FinRevision { get; set; }
        public string? TomaDocumento { get; set; }
        public int EstatusFinal { get; set; }
        public string? Usuario { get; set; }
        public string? NombreCliente { get; set; }
        public string? Ubicacion { get; set; }
        public int TotalFacturas { get; set; }
        public int FacturasCompletas { get; set; }
        public int FacturasDiscrepancia { get; set; }
        public string? UltimaDiscrepancia { get; set; }
        public int Hazmat { get; set; }
        public int? HasIncompleteRelations { get; set; }
        public long SegundosAcumulados { get; set; }
        public string? InicioSesionActual { get; set; }
    }

    private sealed class MonitorTvStateRow
    {
        public string Creferencia { get; set; } = string.Empty;
        public string Estado { get; set; } = "INACTIVA";
    }

    private sealed class MonitorTvPartsRow
    {
        public string Creferencia { get; set; } = string.Empty;
        public int Total { get; set; }
        public int Reviewed { get; set; }
    }

    private sealed class MonitorTvConfigRow
    {
        public string Clave { get; set; } = string.Empty;
        public string Valor { get; set; } = string.Empty;
    }

    private sealed class MonitorTvClosureRow
    {
        public string Creferencia { get; set; } = string.Empty;
        public string Fecha { get; set; } = string.Empty;
        public string? Usuario { get; set; }
        public string? NombreCliente { get; set; }
    }

    private sealed class MonitorTvRequestRow
    {
        public int Id { get; set; }
        public string Cnofact { get; set; } = string.Empty;
        public string Usuario { get; set; } = string.Empty;
        public string Motivo { get; set; } = string.Empty;
        public DateTime? FechaSolicitud { get; set; }
    }
}
