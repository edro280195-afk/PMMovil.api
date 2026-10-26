using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

using Dapper;

using Microsoft.Extensions.Logging;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>Filtros de la línea de tiempo. Las fechas ya vienen normalizadas como [Start, EndExclusive).</summary>
public sealed record AuditMovementsQuery(
    string? Creferencia,
    string? Usuario,
    DateTime? Start,
    DateTime? EndExclusive,
    bool IncludeDetail,
    long? AfterId,
    int Limit);

/// <summary>
/// Consultas del Historial y auditoría del panel: actividad por revisador y
/// por entrada, sobre la bitácora <c>revision_actividad</c> (pmmutileria) y
/// las tablas de tiempos, asignaciones y desbloqueos (pmmtraf).
///
/// Los errores de la bitácora se propagan a propósito: si falta la tabla o la
/// base no responde, la pantalla debe mostrar el error y no un "sin
/// movimientos" que parezca cierto.
/// </summary>
public sealed class RevisionAuditService
{
    private const int MaxPageSize = 200;

    private readonly BaseRepository _db;
    private readonly ILogger<RevisionAuditService> _logger;

    public RevisionAuditService(BaseRepository db, ILogger<RevisionAuditService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Revisadores ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AuditReviewerDto>> GetReviewersAsync()
    {
        AuditUserDirectory directory = await LoadDirectoryAsync();
        return directory.Reviewers
            .Select(user => new AuditReviewerDto
            {
                Usuario = user.Usuario,
                Nombre = user.Nombre,
                Area = user.Area,
                Activo = user.Activo,
            })
            .ToList();
    }

    // ── Resumen por revisador ────────────────────────────────────────────

    public async Task<AuditSummaryDto> GetSummaryAsync(DateTime start, DateTime endExclusive, string? usuario)
    {
        AuditUserDirectory directory = await LoadDirectoryAsync();
        IReadOnlyList<AuditUserInfo> reviewers = string.IsNullOrWhiteSpace(usuario)
            ? directory.Reviewers
            : directory.Reviewers.Where(user => string.Equals(user.Usuario, RevisionAuditRules.TrimKey(usuario), StringComparison.OrdinalIgnoreCase)).ToList();

        string[] keys = reviewers.Select(user => user.Usuario).ToArray();
        if (keys.Length == 0)
        {
            return NewSummary(start, endExclusive, Array.Empty<AuditUserSummaryDto>(), new AuditUserSummaryDto());
        }

        object parameters = SummaryParameters(start, endExclusive, keys);

        List<SummaryRow> activity;
        SummaryRow? totals;
        using (IDbConnection conn = _db.CreatePmmutileriaConnection())
        {
            activity = (await conn.QueryAsync<SummaryRow>(SummarySql(grouped: true), parameters)).ToList();
            totals = await conn.QueryFirstOrDefaultAsync<SummaryRow>(SummarySql(grouped: false), parameters);
        }

        Dictionary<string, long> seconds = await GetSecondsInReviewAsync(start, endExclusive, keys);

        List<AuditUserSummaryDto> items = new();
        foreach (AuditUserInfo reviewer in reviewers)
        {
            SummaryRow? row = activity.FirstOrDefault(candidate =>
                string.Equals(RevisionAuditRules.TrimKey(candidate.Usuario), reviewer.Usuario, StringComparison.OrdinalIgnoreCase));
            seconds.TryGetValue(reviewer.Usuario, out long reviewerSeconds);

            // Un revisador activo sin movimientos también se lista: ver quién no
            // trabajó es parte de auditar. Uno dado de baja solo aparece si tuvo
            // actividad en el rango, o si se pidió específicamente a él.
            bool hadActivity = row is not null || reviewerSeconds > 0;
            if (!hadActivity && !reviewer.Activo && string.IsNullOrWhiteSpace(usuario))
            {
                continue;
            }

            items.Add(ToSummaryDto(row ?? new SummaryRow(), reviewer, reviewerSeconds));
        }

        AuditUserSummaryDto total = ToSummaryDto(totals ?? new SummaryRow(), null, seconds.Values.Sum());
        return NewSummary(start, endExclusive, items, total);
    }

    // ── Entradas que trabajó un revisador ────────────────────────────────

    public async Task<IReadOnlyList<AuditEntryDto>> GetEntriesAsync(string usuario, DateTime start, DateTime endExclusive)
    {
        string key = RevisionAuditRules.TrimKey(usuario);

        List<EntryRow> rows;
        using (IDbConnection conn = _db.CreatePmmutileriaConnection())
        {
            rows = (await conn.QueryAsync<EntryRow>(@"
                SELECT creferencia AS Creferencia,
                       DATE_FORMAT(fecha, '%Y-%m-%d') AS Dia,
                       MIN(fecha) AS Primera,
                       MAX(fecha) AS Ultima,
                       COUNT(DISTINCT CASE WHEN tipo = @facturaCompletada THEN cnofact END) AS FacturasCompletadas,
                       COUNT(DISTINCT CASE WHEN tipo = @facturaDiscrepancia THEN cnofact END) AS FacturasDiscrepancia,
                       COUNT(DISTINCT CASE WHEN tipo = @partidaRevisada THEN idrevdet END) AS PartidasRevisadas,
                       COUNT(CASE WHEN tipo IN @ediciones THEN 1 END) AS Ediciones,
                       COUNT(CASE WHEN tipo = @lectura THEN 1 END) AS LecturasCodigo,
                       COUNT(CASE WHEN tipo = @pausada AND origen <> @sistema THEN 1 END) AS Pausas,
                       COUNT(CASE WHEN tipo = @inicio THEN 1 END) AS Sesiones,
                       MAX(CASE WHEN tipo = @finalizada THEN 1 ELSE 0 END) AS Finalizada
                FROM revision_actividad
                WHERE TRIM(usuario) = @usuario
                  AND fecha >= @desde AND fecha < @hasta
                  AND tipo NOT IN @tecnicos
                GROUP BY creferencia, DATE_FORMAT(fecha, '%Y-%m-%d')
                ORDER BY Dia DESC, Primera ASC",
                new
                {
                    usuario = key,
                    desde = start,
                    hasta = endExclusive,
                    tecnicos = RevisionAuditRules.TechnicalTypes.ToArray(),
                    ediciones = new[] { RevisionActivityTypes.CampoFactura, RevisionActivityTypes.CampoPartida },
                    facturaCompletada = RevisionActivityTypes.FacturaCompletada,
                    facturaDiscrepancia = RevisionActivityTypes.FacturaDiscrepancia,
                    partidaRevisada = RevisionActivityTypes.PartidaRevisada,
                    lectura = RevisionActivityTypes.LecturaCodigo,
                    pausada = RevisionActivityTypes.Pausada,
                    inicio = RevisionActivityTypes.InicioRevision,
                    finalizada = RevisionActivityTypes.Finalizada,
                    sistema = RevisionAuditRules.SystemOrigin,
                })).ToList();
        }

        Dictionary<string, string> clients = await GetClientNamesAsync(rows.Select(row => row.Creferencia).Distinct().ToArray());

        return rows
            .Select(row => new AuditEntryDto
            {
                Creferencia = row.Creferencia,
                NombreCliente = clients.GetValueOrDefault(row.Creferencia) ?? string.Empty,
                Usuario = key,
                Dia = row.Dia,
                Primera = row.Primera,
                Ultima = row.Ultima,
                FacturasCompletadas = (int)row.FacturasCompletadas,
                FacturasDiscrepancia = (int)row.FacturasDiscrepancia,
                PartidasRevisadas = (int)row.PartidasRevisadas,
                Ediciones = (int)row.Ediciones,
                LecturasCodigo = (int)row.LecturasCodigo,
                Pausas = (int)row.Pausas,
                Sesiones = (int)row.Sesiones,
                Finalizada = row.Finalizada != 0,
            })
            .ToList();
    }

    // ── Línea de tiempo ──────────────────────────────────────────────────

    public async Task<AuditMovementsDto> GetMovementsAsync(AuditMovementsQuery query)
    {
        int limit = Math.Clamp(query.Limit, 1, MaxPageSize);

        List<MovementRow> rows;
        using (IDbConnection conn = _db.CreatePmmutileriaConnection())
        {
            rows = (await conn.QueryAsync<MovementRow>(@"
                SELECT id AS Id,
                       creferencia AS Creferencia,
                       usuario AS Usuario,
                       tipo AS Tipo,
                       cnofact AS Cnofact,
                       idrevdet AS IdRevdet,
                       detalle AS Detalle,
                       fecha AS Fecha,
                       origen AS Origen
                FROM revision_actividad
                WHERE (@creferencia IS NULL OR creferencia = @creferencia)
                  AND (@usuario IS NULL OR TRIM(usuario) = @usuario)
                  AND (@desde IS NULL OR fecha >= @desde)
                  AND (@hasta IS NULL OR fecha < @hasta)
                  AND (@despuesDe IS NULL OR id > @despuesDe)
                  AND tipo NOT IN @tecnicos
                  AND (@incluirDetalle = 1 OR tipo NOT IN @soloDetalle)
                ORDER BY id ASC
                LIMIT @limite",
                new
                {
                    creferencia = NullIfEmpty(query.Creferencia),
                    usuario = NullIfEmpty(query.Usuario),
                    desde = query.Start,
                    hasta = query.EndExclusive,
                    despuesDe = query.AfterId,
                    incluirDetalle = query.IncludeDetail ? 1 : 0,
                    tecnicos = RevisionAuditRules.TechnicalTypes.ToArray(),
                    soloDetalle = RevisionAuditRules.DetailOnlyTypes.ToArray(),
                    // Se pide una de más para saber si hay otra página sin un segundo conteo.
                    limite = limit + 1,
                })).ToList();
        }

        bool hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        AuditUserDirectory directory = await LoadDirectoryAsync();
        List<AuditMovementDto> items = rows
            .Select(row => new AuditMovementDto
            {
                Id = row.Id,
                Creferencia = row.Creferencia,
                Usuario = RevisionAuditRules.TrimKey(row.Usuario),
                Nombre = directory.NameOf(row.Usuario),
                EsSistema = IsSystemAction(row.Usuario, row.Origen),
                Tipo = row.Tipo,
                Cnofact = row.Cnofact,
                IdRevdet = row.IdRevdet,
                Detalle = row.Detalle,
                Fecha = row.Fecha,
                Origen = row.Origen,
            })
            .ToList();

        return new AuditMovementsDto
        {
            Items = items,
            Pagination = new AuditPaginationDto
            {
                Limit = limit,
                HasMore = hasMore,
                NextCursor = items.Count == 0 ? null : items[^1].Id,
            },
        };
    }

    // ── Expediente de una referencia ─────────────────────────────────────

    /// <summary>null cuando la referencia no existe ni en facturas ni en la bitácora ni en tiempos.</summary>
    public async Task<AuditReferenceDto?> GetReferenceAsync(string creferencia)
    {
        string reference = creferencia.Trim();
        AuditUserDirectory directory = await LoadDirectoryAsync();

        ReferenceHeaderRow header;
        List<AuditSessionRow> sessions;
        List<string> invoices;
        using (IDbConnection conn = _db.CreateConnection())
        {
            header = await conn.QueryFirstOrDefaultAsync<ReferenceHeaderRow>(@"
                SELECT MAX(cl.cnombre) AS NombreCliente,
                       COUNT(DISTINCT f.cnofact) AS TotalFacturas,
                       COUNT(DISTINCT CASE WHEN f.status_rev = 1 THEN f.cnofact END) AS FacturasCompletas,
                       COUNT(DISTINCT CASE WHEN f.status_rev = 2 THEN f.cnofact END) AS FacturasDiscrepancia
                FROM fact f
                LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid AND cl.inactivo = 0
                WHERE f.cref_agrupada = @creferencia",
                new { creferencia = reference }) ?? new ReferenceHeaderRow();

            // Las fechas en cero que arrastra el sistema anterior llegan como null.
            sessions = (await conn.QueryAsync<SessionRow>(@"
                SELECT CASE WHEN YEAR(finiciorevision) >= 2000 THEN finiciorevision END AS Inicio,
                       CASE WHEN YEAR(ffinrevision) >= 2000 THEN ffinrevision END AS Fin,
                       CASE WHEN YEAR(ftomadocumento) >= 2000 THEN ftomadocumento END AS TomaDocumento,
                       CAST(COALESCE(estatus_final, 0) AS SIGNED) AS EstatusFinal,
                       COALESCE(NULLIF(TRIM(usurevisa), ''), NULLIF(TRIM(usuario), ''), '') AS Cerro
                FROM traftiemposrevision
                WHERE creferencia = @creferencia",
                new { creferencia = reference }))
                .Select(row => new AuditSessionRow(row.Inicio, row.Fin, row.TomaDocumento, (int)row.EstatusFinal, row.Cerro ?? string.Empty))
                .ToList();

            invoices = (await conn.QueryAsync<string>(
                "SELECT DISTINCT cnofact FROM fact WHERE cref_agrupada = @creferencia AND cnofact IS NOT NULL",
                new { creferencia = reference })).Select(RevisionAuditRules.TrimKey).Where(invoice => invoice.Length > 0).ToList();
        }

        List<ParticipantRow> participantRows;
        using (IDbConnection conn = _db.CreatePmmutileriaConnection())
        {
            participantRows = (await conn.QueryAsync<ParticipantRow>(@"
                SELECT TRIM(usuario) AS Usuario,
                       MIN(fecha) AS Primera,
                       MAX(fecha) AS Ultima,
                       COUNT(DISTINCT CASE WHEN tipo = @facturaCompletada THEN cnofact END) AS FacturasCompletadas,
                       COUNT(DISTINCT CASE WHEN tipo = @facturaDiscrepancia THEN cnofact END) AS FacturasDiscrepancia,
                       COUNT(DISTINCT CASE WHEN tipo = @partidaRevisada THEN idrevdet END) AS PartidasRevisadas,
                       COUNT(*) AS Movimientos
                FROM revision_actividad
                WHERE creferencia = @creferencia
                  AND tipo NOT IN @tecnicos
                GROUP BY TRIM(usuario)
                ORDER BY MIN(fecha) ASC",
                new
                {
                    creferencia = reference,
                    tecnicos = RevisionAuditRules.TechnicalTypes.ToArray(),
                    facturaCompletada = RevisionActivityTypes.FacturaCompletada,
                    facturaDiscrepancia = RevisionActivityTypes.FacturaDiscrepancia,
                    partidaRevisada = RevisionActivityTypes.PartidaRevisada,
                })).ToList();
        }

        if (header.TotalFacturas == 0 && sessions.Count == 0 && participantRows.Count == 0)
        {
            return null;
        }

        IReadOnlyList<AuditAssignmentDto> assignments = await GetAssignmentsAsync(reference, directory);
        IReadOnlyList<AuditUnlockDto> unlocks = await GetUnlocksAsync(reference, invoices, directory);

        AuditSessionSummary summary = RevisionAuditRules.SummarizeSessions(sessions, DateTime.Now);

        List<AuditParticipantDto> participants = participantRows
            .Select(row => new AuditParticipantDto
            {
                Usuario = RevisionAuditRules.TrimKey(row.Usuario),
                Nombre = directory.NameOf(row.Usuario),
                EsRevisor = directory.IsReviewer(row.Usuario),
                EsSistema = RevisionAuditRules.IsSystemUser(row.Usuario),
                Primera = row.Primera,
                Ultima = row.Ultima,
                FacturasCompletadas = (int)row.FacturasCompletadas,
                FacturasDiscrepancia = (int)row.FacturasDiscrepancia,
                PartidasRevisadas = (int)row.PartidasRevisadas,
                Movimientos = (int)row.Movimientos,
            })
            .ToList();

        return new AuditReferenceDto
        {
            Creferencia = reference,
            NombreCliente = header.NombreCliente ?? string.Empty,
            TotalFacturas = (int)header.TotalFacturas,
            FacturasCompletas = (int)header.FacturasCompletas,
            FacturasDiscrepancia = (int)header.FacturasDiscrepancia,
            Estado = summary.Estado,
            Sesiones = summary.Sesiones,
            SegundosEnRevision = summary.Segundos,
            CerradaPor = RevisionAuditRules.TrimKey(summary.CerradaPor),
            CerradaPorNombre = directory.NameOf(summary.CerradaPor),
            CerradaEn = summary.CerradaEn,
            PrimeraActividad = participants.Count == 0 ? null : participants.Min(participant => participant.Primera),
            UltimaActividad = participants.Count == 0 ? null : participants.Max(participant => participant.Ultima),
            Participantes = participants,
            Asignaciones = assignments,
            Desbloqueos = unlocks,
        };
    }

    // Asignaciones y desbloqueos enriquecen el expediente, pero no son la
    // bitácora: si su tabla aún no existe (se crea al primer uso) el
    // expediente se muestra completo sin ellas.
    private async Task<IReadOnlyList<AuditAssignmentDto>> GetAssignmentsAsync(string creferencia, AuditUserDirectory directory)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            IEnumerable<AssignmentRow> rows = await conn.QueryAsync<AssignmentRow>(@"
                SELECT id_asignacion AS Id,
                       TRIM(usuario_asignado) AS UsuarioAsignado,
                       TRIM(usuario_asigna) AS UsuarioAsigna,
                       fecha_asignacion AS FechaAsignacion,
                       fecha_visto AS FechaVisto,
                       fecha_completada AS FechaCompletada,
                       CAST(prioridad AS SIGNED) AS Prioridad,
                       COALESCE(nota, '') AS Nota,
                       CAST(estatus AS SIGNED) AS Estatus
                FROM asignaciones_revision
                WHERE cref_agrupada = @creferencia
                ORDER BY fecha_asignacion ASC, id_asignacion ASC",
                new { creferencia });

            return rows
                .Select(row => new AuditAssignmentDto
                {
                    Id = row.Id,
                    UsuarioAsignado = row.UsuarioAsignado ?? string.Empty,
                    NombreAsignado = directory.NameOf(row.UsuarioAsignado),
                    UsuarioAsigna = row.UsuarioAsigna ?? string.Empty,
                    NombreAsigna = directory.NameOf(row.UsuarioAsigna),
                    FechaAsignacion = row.FechaAsignacion ?? DateTime.MinValue,
                    FechaVisto = row.FechaVisto,
                    FechaCompletada = row.FechaCompletada,
                    Prioridad = (int)row.Prioridad,
                    Nota = row.Nota ?? string.Empty,
                    Estatus = (int)row.Estatus,
                })
                .ToList();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No se pudieron consultar las asignaciones de {Creferencia} para la auditoría.", creferencia);
            return Array.Empty<AuditAssignmentDto>();
        }
    }

    private async Task<IReadOnlyList<AuditUnlockDto>> GetUnlocksAsync(string creferencia, IReadOnlyList<string> invoices, AuditUserDirectory directory)
    {
        try
        {
            // Un permiso se pide por factura o, con el prefijo ENTRY_, por la
            // entrada completa. Las facturas viajan como parámetros para no
            // mezclar la codificación de dos tablas dentro del SQL.
            string[] keys = invoices.Append("ENTRY_" + creferencia).ToArray();

            using IDbConnection conn = _db.CreateConnection();
            IEnumerable<UnlockRow> rows = await conn.QueryAsync<UnlockRow>(@"
                SELECT id_desbloqueo AS Id,
                       TRIM(cnofact) AS Cnofact,
                       TRIM(usuario_solicita) AS UsuarioSolicita,
                       COALESCE(motivo, '') AS Motivo,
                       CAST(COALESCE(estatus, 0) AS SIGNED) AS Estatus,
                       TRIM(COALESCE(usuario_autoriza, '')) AS UsuarioAutoriza,
                       fecha_solicitud AS FechaSolicitud,
                       fecha_autorizacion AS FechaAutorizacion,
                       fecha_expiracion AS FechaExpiracion
                FROM rev_desbloqueos
                WHERE cnofact IN @claves
                ORDER BY fecha_solicitud ASC, id_desbloqueo ASC",
                new { claves = keys });

            return rows
                .Select(row => new AuditUnlockDto
                {
                    Id = row.Id,
                    Cnofact = (row.Cnofact ?? string.Empty).StartsWith("ENTRY_", StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : row.Cnofact ?? string.Empty,
                    UsuarioSolicita = row.UsuarioSolicita ?? string.Empty,
                    NombreSolicita = directory.NameOf(row.UsuarioSolicita),
                    Motivo = row.Motivo ?? string.Empty,
                    Estatus = (int)row.Estatus,
                    UsuarioAutoriza = row.UsuarioAutoriza ?? string.Empty,
                    NombreAutoriza = directory.NameOf(row.UsuarioAutoriza),
                    Vencido = row.Estatus == 2 && string.Equals(row.UsuarioAutoriza, "SYSTEM_EXP", StringComparison.OrdinalIgnoreCase),
                    FechaSolicitud = row.FechaSolicitud,
                    FechaAutorizacion = row.FechaAutorizacion,
                    FechaExpiracion = row.FechaExpiracion,
                })
                .ToList();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No se pudieron consultar los desbloqueos de {Creferencia} para la auditoría.", creferencia);
            return Array.Empty<AuditUnlockDto>();
        }
    }

    // ── Apoyos ───────────────────────────────────────────────────────────

    private async Task<AuditUserDirectory> LoadDirectoryAsync()
    {
        using IDbConnection conn = _db.CreateConnection();
        IEnumerable<UserRow> rows = await conn.QueryAsync<UserRow>(@"
            SELECT TRIM(claveusuario) AS Usuario,
                   TRIM(nombre) AS Nombre,
                   TRIM(apaterno) AS Apaterno,
                   TRIM(amaterno) AS Amaterno,
                   TRIM(COALESCE(area, '')) AS Area,
                   CAST(COALESCE(inactivo, 0) AS SIGNED) AS Inactivo
            FROM usuarios
            WHERE claveusuario IS NOT NULL AND TRIM(claveusuario) <> ''");

        return AuditUserDirectory.Build(
            rows.Select(row => new AuditUserInfo(
                row.Usuario ?? string.Empty,
                RevisionAuditRules.FormatDisplayName(row.Nombre, row.Apaterno, row.Amaterno),
                row.Area ?? string.Empty,
                row.Inactivo == 0)),
            ReviewerRoster.All);
    }

    private async Task<Dictionary<string, long>> GetSecondsInReviewAsync(DateTime start, DateTime endExclusive, string[] keys)
    {
        using IDbConnection conn = _db.CreateConnection();
        IEnumerable<SecondsRow> rows = await conn.QueryAsync<SecondsRow>($@"
            SELECT TRIM(t.usuario) AS Usuario,
                   CAST(COALESCE(SUM(TIMESTAMPDIFF(SECOND, t.finiciorevision, t.ffinrevision)), 0) AS SIGNED) AS Segundos
            FROM traftiemposrevision t
            WHERE TRIM(t.usuario) IN @claves
              AND t.finiciorevision >= @desde AND t.finiciorevision < @hasta
              AND {RevisionTimeRules.ClosedSessionPredicate("t")}
            GROUP BY TRIM(t.usuario)",
            new { claves = keys, desde = start, hasta = endExclusive });

        Dictionary<string, long> seconds = new(StringComparer.OrdinalIgnoreCase);
        foreach (SecondsRow row in rows)
        {
            string key = RevisionAuditRules.TrimKey(row.Usuario);
            seconds[key] = seconds.GetValueOrDefault(key) + row.Segundos;
        }

        return seconds;
    }

    private async Task<Dictionary<string, string>> GetClientNamesAsync(string[] references)
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        if (references.Length == 0)
        {
            return names;
        }

        using IDbConnection conn = _db.CreateConnection();
        IEnumerable<ClientRow> rows = await conn.QueryAsync<ClientRow>(@"
            SELECT f.cref_agrupada AS Creferencia, MAX(cl.cnombre) AS Nombre
            FROM fact f
            LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid AND cl.inactivo = 0
            WHERE f.cref_agrupada IN @referencias
            GROUP BY f.cref_agrupada",
            new { referencias = references });

        foreach (ClientRow row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Creferencia) && !string.IsNullOrWhiteSpace(row.Nombre))
            {
                names[row.Creferencia.Trim()] = row.Nombre.Trim();
            }
        }

        return names;
    }

    // La bitácora guarda la clave tal como viene en el token, o sea con el
    // relleno de usuarios.claveusuario ("alarumbe       "), y la columna puede
    // distinguir los espacios finales (utf8mb4_0900_ai_ci los distingue). Por
    // eso todas las comparaciones por usuario son contra la clave recortada:
    // comparar la clave limpia contra el valor guardado dejaba fuera a los
    // revisores con relleno. El rango de fechas sigue acotando con su índice.
    //
    // Una sola definición de cada métrica, con o sin agrupar por persona.
    private static string SummarySql(bool grouped) => $@"
        SELECT {(grouped ? "TRIM(usuario) AS Usuario," : "'' AS Usuario,")}
               COUNT(DISTINCT creferencia) AS Entradas,
               COUNT(DISTINCT CASE WHEN tipo = @finalizada THEN creferencia END) AS EntradasCerradas,
               COUNT(DISTINCT CASE WHEN tipo = @facturaCompletada THEN CONCAT(creferencia, '|', cnofact) END) AS FacturasCompletadas,
               COUNT(DISTINCT CASE WHEN tipo = @facturaDiscrepancia THEN CONCAT(creferencia, '|', cnofact) END) AS FacturasDiscrepancia,
               COUNT(DISTINCT CASE WHEN tipo = @partidaRevisada THEN idrevdet END) AS PartidasRevisadas,
               COUNT(CASE WHEN tipo IN @ediciones THEN 1 END) AS Ediciones,
               COUNT(CASE WHEN tipo = @lectura THEN 1 END) AS LecturasCodigo,
               COUNT(CASE WHEN tipo = @pausada AND origen <> @sistema THEN 1 END) AS Pausas,
               COUNT(CASE WHEN tipo = @inicio THEN 1 END) AS Sesiones,
               MIN(fecha) AS PrimeraActividad,
               MAX(fecha) AS UltimaActividad
        FROM revision_actividad
        WHERE fecha >= @desde AND fecha < @hasta
          AND TRIM(usuario) IN @claves
          AND tipo NOT IN @tecnicos
        {(grouped ? "GROUP BY TRIM(usuario)" : string.Empty)}";

    private static object SummaryParameters(DateTime start, DateTime endExclusive, string[] keys) => new
    {
        desde = start,
        hasta = endExclusive,
        claves = keys,
        tecnicos = RevisionAuditRules.TechnicalTypes.ToArray(),
        ediciones = new[] { RevisionActivityTypes.CampoFactura, RevisionActivityTypes.CampoPartida },
        facturaCompletada = RevisionActivityTypes.FacturaCompletada,
        facturaDiscrepancia = RevisionActivityTypes.FacturaDiscrepancia,
        partidaRevisada = RevisionActivityTypes.PartidaRevisada,
        lectura = RevisionActivityTypes.LecturaCodigo,
        pausada = RevisionActivityTypes.Pausada,
        inicio = RevisionActivityTypes.InicioRevision,
        finalizada = RevisionActivityTypes.Finalizada,
        sistema = RevisionAuditRules.SystemOrigin,
    };

    private static AuditUserSummaryDto ToSummaryDto(SummaryRow row, AuditUserInfo? reviewer, long seconds) => new()
    {
        Usuario = reviewer?.Usuario ?? string.Empty,
        Nombre = reviewer?.Nombre ?? string.Empty,
        Area = reviewer?.Area ?? string.Empty,
        Activo = reviewer?.Activo ?? true,
        Entradas = (int)row.Entradas,
        EntradasCerradas = (int)row.EntradasCerradas,
        FacturasCompletadas = (int)row.FacturasCompletadas,
        FacturasDiscrepancia = (int)row.FacturasDiscrepancia,
        PartidasRevisadas = (int)row.PartidasRevisadas,
        Ediciones = (int)row.Ediciones,
        LecturasCodigo = (int)row.LecturasCodigo,
        Pausas = (int)row.Pausas,
        Sesiones = (int)row.Sesiones,
        PrimeraActividad = row.PrimeraActividad,
        UltimaActividad = row.UltimaActividad,
        SegundosEnRevision = seconds,
    };

    private static AuditSummaryDto NewSummary(DateTime start, DateTime endExclusive, IReadOnlyList<AuditUserSummaryDto> items, AuditUserSummaryDto totals) => new()
    {
        Desde = start.ToString("yyyy-MM-dd"),
        Hasta = endExclusive.AddDays(-1).ToString("yyyy-MM-dd"),
        Items = items,
        Totales = totals,
    };

    private static bool IsSystemAction(string? usuario, string? origen) =>
        RevisionAuditRules.IsSystemUser(usuario)
        || string.Equals(origen, RevisionAuditRules.SystemOrigin, StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ── Filas de Dapper ──────────────────────────────────────────────────

    private sealed class UserRow
    {
        public string? Usuario { get; set; }
        public string? Nombre { get; set; }
        public string? Apaterno { get; set; }
        public string? Amaterno { get; set; }
        public string? Area { get; set; }
        public long Inactivo { get; set; }
    }

    private sealed class SummaryRow
    {
        public string? Usuario { get; set; }
        public long Entradas { get; set; }
        public long EntradasCerradas { get; set; }
        public long FacturasCompletadas { get; set; }
        public long FacturasDiscrepancia { get; set; }
        public long PartidasRevisadas { get; set; }
        public long Ediciones { get; set; }
        public long LecturasCodigo { get; set; }
        public long Pausas { get; set; }
        public long Sesiones { get; set; }
        public DateTime? PrimeraActividad { get; set; }
        public DateTime? UltimaActividad { get; set; }
    }

    private sealed class SecondsRow
    {
        public string? Usuario { get; set; }
        public long Segundos { get; set; }
    }

    private sealed class EntryRow
    {
        public string Creferencia { get; set; } = string.Empty;
        public string Dia { get; set; } = string.Empty;
        public DateTime Primera { get; set; }
        public DateTime Ultima { get; set; }
        public long FacturasCompletadas { get; set; }
        public long FacturasDiscrepancia { get; set; }
        public long PartidasRevisadas { get; set; }
        public long Ediciones { get; set; }
        public long LecturasCodigo { get; set; }
        public long Pausas { get; set; }
        public long Sesiones { get; set; }
        public long Finalizada { get; set; }
    }

    private sealed class MovementRow
    {
        public long Id { get; set; }
        public string Creferencia { get; set; } = string.Empty;
        public string Usuario { get; set; } = string.Empty;
        public string Tipo { get; set; } = string.Empty;
        public string? Cnofact { get; set; }
        public int? IdRevdet { get; set; }
        public string? Detalle { get; set; }
        public DateTime Fecha { get; set; }
        public string Origen { get; set; } = string.Empty;
    }

    private sealed class ClientRow
    {
        public string Creferencia { get; set; } = string.Empty;
        public string? Nombre { get; set; }
    }

    private sealed class ReferenceHeaderRow
    {
        public string? NombreCliente { get; set; }
        public long TotalFacturas { get; set; }
        public long FacturasCompletas { get; set; }
        public long FacturasDiscrepancia { get; set; }
    }

    private sealed class SessionRow
    {
        public DateTime? Inicio { get; set; }
        public DateTime? Fin { get; set; }
        public DateTime? TomaDocumento { get; set; }
        public long EstatusFinal { get; set; }
        public string? Cerro { get; set; }
    }

    private sealed class ParticipantRow
    {
        public string? Usuario { get; set; }
        public DateTime Primera { get; set; }
        public DateTime Ultima { get; set; }
        public long FacturasCompletadas { get; set; }
        public long FacturasDiscrepancia { get; set; }
        public long PartidasRevisadas { get; set; }
        public long Movimientos { get; set; }
    }

    private sealed class AssignmentRow
    {
        public int Id { get; set; }
        public string? UsuarioAsignado { get; set; }
        public string? UsuarioAsigna { get; set; }
        public DateTime? FechaAsignacion { get; set; }
        public DateTime? FechaVisto { get; set; }
        public DateTime? FechaCompletada { get; set; }
        public long Prioridad { get; set; }
        public string? Nota { get; set; }
        public long Estatus { get; set; }
    }

    private sealed class UnlockRow
    {
        public int Id { get; set; }
        public string? Cnofact { get; set; }
        public string? UsuarioSolicita { get; set; }
        public string? Motivo { get; set; }
        public long Estatus { get; set; }
        public string? UsuarioAutoriza { get; set; }
        public DateTime? FechaSolicitud { get; set; }
        public DateTime? FechaAutorizacion { get; set; }
        public DateTime? FechaExpiracion { get; set; }
    }
}
