using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using PMMovil.Api.Attributes;
using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Censo de racks: abrir con instantánea, registrar observaciones por etiqueta
/// y conciliar. Idempotente por UUID de sesión y de lectura.
///
/// El censo **registra observaciones y hallazgos**; no mueve mercancía. Lo
/// único que escribe fuera de sus propias tablas es `etiqueta_mcia.time_inv` /
/// `usu_inv`, la «última observación» que ya usaba el inventario legacy. No
/// toca `estatus` ni `trafdet.ubicacion`: esa columna es de la partida completa
/// y no puede representar un solo bulto movido.
/// </summary>
public sealed class RackCensusService(
    BaseRepository database,
    IModulePermissionService permissions,
    ILogger<RackCensusService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class LabelRow
    {
        public long LabelId { get; set; }
        public string Textoetiqueta { get; set; } = "";
        public string Creferencia { get; set; } = "";
        public int Nlineno { get; set; }
        public string Estatus { get; set; } = "";
        public string Ubicacion { get; set; } = "";
        public string Descripcion { get; set; } = "";
        public string Cliente { get; set; } = "";
    }

    private sealed class SessionRow
    {
        public string SessionId { get; set; } = "";
        public string WarehouseId { get; set; } = "";
        public string Usuario { get; set; } = "";
        public string ScopeJson { get; set; } = "";
        public long SnapshotVersion { get; set; }
        public long CapturedAtMillis { get; set; }
        public bool ForeignCatalogTruncated { get; set; }
        public string DataWarningsJson { get; set; } = "";
        public string Status { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string? ReconcileHash { get; set; }
        public string? ResponseJson { get; set; }
    }

    private sealed class SnapshotItemRow
    {
        public string PackageId { get; set; } = "";
        public long LabelId { get; set; }
        public string Creferencia { get; set; } = "";
        public int Nlineno { get; set; }
        public string ExpectedLocation { get; set; } = "";
        public string Estatus { get; set; } = "";
        public string Cliente { get; set; } = "";
        public string Descripcion { get; set; } = "";
        public bool InScope { get; set; }
    }

    private sealed class ObservationStateRow
    {
        public string ObservationId { get; set; } = "";
        public string NormalizedCode { get; set; } = "";
        public string? PackageId { get; set; }
        public long? LabelId { get; set; }
        public string ObservedLocation { get; set; } = "";
        public string Resolution { get; set; } = "";
    }

    private sealed class MovementCandidateRow
    {
        public string PackageId { get; set; } = "";
        public string Creferencia { get; set; } = "";
        public string ExpectedLocation { get; set; } = "";
        public string SnapshotEstatus { get; set; } = "";
        public string CurrentEstatus { get; set; } = "";
        public int Removed { get; set; }
        public string CurrentLocation { get; set; } = "";
        public long LabelChangedMillis { get; set; }
        public long LineChangedMillis { get; set; }
    }

    private const string StatusOpen = "ABIERTA";
    private const string StatusReconciled = "CONCILIADA";

    private sealed class GuardFailure(int statusCode, string code, string message) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
        public string Code { get; } = code;
    }

    // ── Abrir ────────────────────────────────────────────────────────────────

    public async Task<RackCensusResult> OpenAsync(RackCensusOpenRequest request, string user, CancellationToken cancellationToken)
    {
        List<string> heldLocks = new();
        using MySqlConnection connection = (MySqlConnection)database.CreateConnection();
        try
        {
            RackCensusRules.ValidateOpen(request);
            await RequirePermissionAsync(user, "Agregar", cancellationToken);
            List<string> scope = RackCensusRules.NormalizeScope(request.LocationIds);
            string sessionId = request.SessionId.ToString("D");
            string warehouseId = request.WarehouseId.Trim().ToUpperInvariant();
            string requestHash = Fingerprint(sessionId, warehouseId, scope);

            await connection.OpenAsync(cancellationToken);
            await EnsureMigrationAsync(connection);
            await AcquireLockAsync(connection, $"rack_census_{sessionId}", heldLocks);

            SessionRow? existing = await ReadSessionAsync(connection, null, sessionId);
            if (existing is not null)
            {
                if (!string.Equals(existing.Usuario, user, StringComparison.OrdinalIgnoreCase) ||
                    existing.RequestHash != requestHash)
                    throw new GuardFailure(409, "SESSION_CONFLICT",
                        "El identificador de censo ya corresponde a otro recorrido. Abre uno nuevo.");
                return new(200, await BuildOpenResponseAsync(connection, existing, replayed: true));
            }

            long now = await ServerMillisAsync(connection, null);
            List<LabelRow> rows = (await connection.QueryAsync<LabelRow>(ScopeLabelsSql, new { scope })).ToList();
            List<LabelRow> foreignRows = (await connection.QueryAsync<LabelRow>(
                ForeignLabelsSql, new { scope, limit = RackCensusRules.MaxForeignCatalog + 1 })).ToList();
            bool truncated = foreignRows.Count > RackCensusRules.MaxForeignCatalog;
            if (truncated) foreignRows = foreignRows.Take(RackCensusRules.MaxForeignCatalog).ToList();

            (List<SnapshotItemRow> items, List<string> warnings) =
                BuildSnapshotItems(rows, foreignRows, scope.ToHashSet(StringComparer.Ordinal));

            await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await connection.ExecuteAsync(@"
                INSERT INTO rack_census_sessions
                    (session_id,warehouse_id,usuario,scope_json,snapshot_version,snapshot_captured_at,
                     foreign_catalog_truncated,data_warnings_json,status,request_hash,created_at)
                VALUES (@sessionId,@warehouseId,@user,@scopeJson,@version,FROM_UNIXTIME(@version/1000),
                     @truncated,@warningsJson,@status,@requestHash,UTC_TIMESTAMP());",
                new
                {
                    sessionId, warehouseId, user, scopeJson = JsonSerializer.Serialize(scope, JsonOptions),
                    version = now, truncated, warningsJson = JsonSerializer.Serialize(warnings, JsonOptions),
                    status = StatusOpen, requestHash
                }, transaction);
            foreach (IEnumerable<SnapshotItemRow> batch in items.Chunk(500))
            {
                await connection.ExecuteAsync(@"
                    INSERT INTO rack_census_snapshot_items
                        (session_id,package_id,id_etiqueta_mcia,creferencia,nlineno,expected_location,estatus,
                         cliente,descripcion,in_scope)
                    VALUES (@sessionId,@PackageId,@LabelId,@Creferencia,@Nlineno,@ExpectedLocation,@Estatus,
                         @Cliente,@Descripcion,@InScope);",
                    batch.Select(item => new
                    {
                        sessionId, item.PackageId, item.LabelId, item.Creferencia, item.Nlineno,
                        item.ExpectedLocation, item.Estatus, item.Cliente, item.Descripcion, item.InScope
                    }), transaction);
            }
            await transaction.CommitAsync(CancellationToken.None);

            SessionRow saved = await ReadSessionAsync(connection, null, sessionId)
                ?? throw new InvalidOperationException("No se pudo leer la sesión recién abierta.");
            return new(200, await BuildOpenResponseAsync(connection, saved, replayed: false));
        }
        catch (GuardFailure error) { return Failure(error, request.SessionId); }
        catch (ArgumentException error)
        {
            return new(400, new RackCensusOpenResponse
            { Code = "INVALID_CENSUS", SessionId = request.SessionId, Message = error.Message });
        }
        catch (MySqlException error) when (IsSchemaError(error)) { return MigrationRequired(request.SessionId); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            logger.LogError(error, "Error abriendo el censo {SessionId}", request.SessionId);
            return new(503, new RackCensusOpenResponse
            {
                Code = "CENSUS_FAILED", SessionId = request.SessionId,
                Message = "No se pudo abrir el censo. Reintenta con el mismo identificador."
            });
        }
        finally { await ReleaseLocksAsync(connection, heldLocks); }
    }

    public async Task<RackCensusResult> GetSessionAsync(Guid sessionId, string user, CancellationToken cancellationToken)
    {
        using MySqlConnection connection = (MySqlConnection)database.CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            await EnsureMigrationAsync(connection);
            SessionRow session = await RequireSessionAsync(connection, sessionId, user);
            return new(200, await BuildOpenResponseAsync(connection, session, replayed: true));
        }
        catch (GuardFailure error) { return Failure(error, sessionId); }
        catch (MySqlException error) when (IsSchemaError(error)) { return MigrationRequired(sessionId); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            logger.LogError(error, "Error consultando el censo {SessionId}", sessionId);
            return new(503, new RackCensusOpenResponse
            { Code = "CENSUS_FAILED", SessionId = sessionId, Message = "No se pudo consultar el censo." });
        }
    }

    // ── Barrer ───────────────────────────────────────────────────────────────

    public async Task<RackCensusResult> ObserveAsync(
        Guid routeSessionId, RackCensusObserveRequest request, string user, CancellationToken cancellationToken)
    {
        List<string> heldLocks = new();
        using MySqlConnection connection = (MySqlConnection)database.CreateConnection();
        try
        {
            RackCensusRules.ValidateObserve(request);
            if (routeSessionId != request.SessionId)
                throw new GuardFailure(400, "INVALID_CENSUS", "La ruta y el cuerpo indican sesiones distintas.");
            await RequirePermissionAsync(user, "Agregar", cancellationToken);
            string sessionId = request.SessionId.ToString("D");

            await connection.OpenAsync(cancellationToken);
            await EnsureMigrationAsync(connection);
            await AcquireLockAsync(connection, $"rack_census_{sessionId}", heldLocks);
            SessionRow session = await RequireSessionAsync(connection, request.SessionId, user);
            if (session.Status != StatusOpen)
                throw new GuardFailure(409, "SESSION_CLOSED", "El censo ya se concilió y no admite más lecturas.");

            HashSet<string> scope = ReadScope(session);
            Dictionary<string, RackCensusPackage> byCode = await ReadPackagesAsync(connection, sessionId);
            Dictionary<string, ObservationStateRow> saved = (await connection.QueryAsync<ObservationStateRow>(@"
                SELECT observation_id AS ObservationId,normalized_code AS NormalizedCode,package_id AS PackageId,
                       id_etiqueta_mcia AS LabelId,observed_location AS ObservedLocation,resolution AS Resolution
                  FROM rack_census_observations
                 WHERE session_id=@sessionId AND observation_id IN @ids",
                new { sessionId, ids = request.Observations.Select(o => o.ObservationId.ToString("D")).ToArray() }))
                .ToDictionary(row => row.ObservationId, StringComparer.OrdinalIgnoreCase);

            List<RackCensusObservationResult> results = new();
            await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            foreach (RackCensusObservationInput input in request.Observations)
            {
                string observationId = input.ObservationId.ToString("D");
                string location = RackCensusRules.NormalizeLocation(input.ObservedLocationId);
                string code = RackCensusRules.NormalizeCode(input.RawCode);
                if (saved.TryGetValue(observationId, out ObservationStateRow? previous))
                {
                    // Reintento: se devuelve el resultado guardado si es la misma
                    // lectura; otra lectura con el mismo identificador se rechaza.
                    bool same = previous.NormalizedCode == code && previous.ObservedLocation == location;
                    results.Add(new RackCensusObservationResult
                    {
                        ObservationId = input.ObservationId,
                        Accepted = same,
                        Resolution = previous.Resolution,
                        PackageId = previous.PackageId,
                        LabelId = previous.LabelId,
                        Message = same ? "Lectura ya registrada." : "El identificador de lectura ya corresponde a otro código."
                    });
                    continue;
                }
                if (!scope.Contains(location))
                {
                    results.Add(new RackCensusObservationResult
                    {
                        ObservationId = input.ObservationId, Accepted = false,
                        Resolution = RackCensusRules.ResolutionMalformed,
                        Message = "La ubicación leída está fuera del alcance del censo."
                    });
                    continue;
                }
                (string resolution, string? packageId, long? labelId) = RackCensusRules.Resolve(byCode, input.RawCode);
                // El reloj del equipo puede ir atrasado: una observación nunca
                // queda antes del snapshot con el que se compara.
                long observedAt = Math.Max(input.ObservedAtMillis, session.CapturedAtMillis);
                await connection.ExecuteAsync(@"
                    INSERT INTO rack_census_observations
                        (observation_id,session_id,usuario,raw_code,normalized_code,package_id,id_etiqueta_mcia,
                         observed_location,observed_at,resolution,created_at)
                    VALUES (@observationId,@sessionId,@user,@rawCode,@code,@packageId,@labelId,
                         @location,FROM_UNIXTIME(@observedAt/1000),@resolution,UTC_TIMESTAMP());",
                    new
                    {
                        observationId, sessionId, user,
                        rawCode = Truncate(input.RawCode, 255), code, packageId, labelId, location,
                        observedAt, resolution
                    }, transaction);
                results.Add(new RackCensusObservationResult
                {
                    ObservationId = input.ObservationId, Accepted = true, Resolution = resolution,
                    PackageId = packageId, LabelId = labelId,
                    Message = resolution == RackCensusRules.ResolutionResolved ? null
                        : "La lectura se conservó sin identidad física; se revisa en la conciliación."
                });
            }
            await transaction.CommitAsync(CancellationToken.None);
            return new(200, new RackCensusObserveResponse
            { Success = true, SessionId = request.SessionId, Results = results });
        }
        catch (GuardFailure error)
        {
            return new(error.StatusCode, new RackCensusObserveResponse
            { Code = error.Code, Message = error.Message, SessionId = request.SessionId });
        }
        catch (ArgumentException error)
        {
            return new(400, new RackCensusObserveResponse
            { Code = "INVALID_CENSUS", Message = error.Message, SessionId = request.SessionId });
        }
        catch (MySqlException error) when (IsSchemaError(error))
        {
            return new(503, new RackCensusObserveResponse
            { Code = "MIGRATION_REQUIRED", SessionId = request.SessionId, Message = MigrationMessage });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            logger.LogError(error, "Error registrando lecturas del censo {SessionId}", request.SessionId);
            return new(503, new RackCensusObserveResponse
            {
                Code = "CENSUS_FAILED", SessionId = request.SessionId,
                Message = "No se pudieron registrar las lecturas. Conserva el recorrido y reintenta."
            });
        }
        finally { await ReleaseLocksAsync(connection, heldLocks); }
    }

    // ── Movimientos ──────────────────────────────────────────────────────────

    public async Task<RackCensusResult> GetMovementsAsync(Guid sessionId, string user, CancellationToken cancellationToken)
    {
        using MySqlConnection connection = (MySqlConnection)database.CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            await EnsureMigrationAsync(connection);
            SessionRow session = await RequireSessionAsync(connection, sessionId, user);
            long now = await ServerMillisAsync(connection, null);
            List<RackCensusMovement> movements = await DetectMovementsAsync(connection, null, session, now);
            return new(200, new RackCensusMovementsResponse
            {
                Success = true, SessionId = sessionId, SnapshotVersion = session.SnapshotVersion,
                CheckedAtMillis = now, Movements = movements
            });
        }
        catch (GuardFailure error)
        {
            return new(error.StatusCode, new RackCensusMovementsResponse
            { Code = error.Code, Message = error.Message, SessionId = sessionId });
        }
        catch (MySqlException error) when (IsSchemaError(error))
        {
            return new(503, new RackCensusMovementsResponse
            { Code = "MIGRATION_REQUIRED", SessionId = sessionId, Message = MigrationMessage });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            logger.LogError(error, "Error consultando movimientos del censo {SessionId}", sessionId);
            return new(503, new RackCensusMovementsResponse
            {
                Code = "CENSUS_FAILED", SessionId = sessionId,
                Message = "No se pudieron consultar los movimientos del recorrido."
            });
        }
    }

    // ── Conciliar ────────────────────────────────────────────────────────────

    public async Task<RackCensusResult> ReconcileAsync(
        Guid routeSessionId, RackCensusReconcileRequest request, string user, CancellationToken cancellationToken)
    {
        List<string> heldLocks = new();
        bool commitStarted = false;
        using MySqlConnection connection = (MySqlConnection)database.CreateConnection();
        try
        {
            RackCensusRules.ValidateReconcile(request);
            if (routeSessionId != request.SessionId)
                throw new GuardFailure(400, "INVALID_CENSUS", "La ruta y el cuerpo indican sesiones distintas.");
            // Conciliar escribe la última observación en las etiquetas: exige
            // modificar, además de agregar los hallazgos del censo.
            await RequirePermissionAsync(user, "Agregar", cancellationToken);
            await RequirePermissionAsync(user, "Modificar", cancellationToken);
            string sessionId = request.SessionId.ToString("D");
            string reconcileHash = Fingerprint(sessionId, request.SnapshotVersion.ToString(), BuildDecisionKey(request));

            await connection.OpenAsync(cancellationToken);
            await EnsureMigrationAsync(connection);
            await AcquireLockAsync(connection, $"rack_census_{sessionId}", heldLocks);
            SessionRow session = await RequireSessionAsync(connection, request.SessionId, user);
            if (session.Status == StatusReconciled)
            {
                if (session.ReconcileHash != reconcileHash || session.ResponseJson is null)
                    throw new GuardFailure(409, "SESSION_CONFLICT",
                        "El censo ya se concilió con otras decisiones. Consulta el resultado guardado.");
                RackCensusReconcileResponse saved =
                    JsonSerializer.Deserialize<RackCensusReconcileResponse>(session.ResponseJson, JsonOptions)
                    ?? throw new InvalidDataException("No se pudo recuperar la conciliación guardada.");
                return new(200, saved with { Replayed = true });
            }
            if (session.SnapshotVersion != request.SnapshotVersion)
                throw new GuardFailure(409, "SNAPSHOT_CONFLICT",
                    "El inventario de referencia cambió. Vuelve a abrir el censo antes de conciliar.");

            await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            long now = await ServerMillisAsync(connection, transaction);
            List<RackCensusPackage> packages = (await ReadPackagesAsync(connection, sessionId, transaction)).Values.ToList();
            List<RackCensusRules.ObservationRow> observations = (await connection.QueryAsync<ObservationStateRow>(@"
                SELECT observation_id AS ObservationId,normalized_code AS NormalizedCode,package_id AS PackageId,
                       id_etiqueta_mcia AS LabelId,observed_location AS ObservedLocation,resolution AS Resolution
                  FROM rack_census_observations WHERE session_id=@sessionId", new { sessionId }, transaction))
                .Select(row => new RackCensusRules.ObservationRow(
                    row.NormalizedCode, row.PackageId, row.ObservedLocation, row.Resolution))
                .ToList();
            List<RackCensusMovement> movements = await DetectMovementsAsync(connection, transaction, session, now);

            HashSet<string> acknowledged = new(
                request.AcknowledgedMovementPackageIds.Select(RackCensusRules.NormalizeCode), StringComparer.Ordinal);
            HashSet<string> confirmedMissing = new(
                request.MissingConfirmations.Select(c => RackCensusRules.NormalizeCode(c.PackageId)), StringComparer.Ordinal);
            HashSet<string> keptPending = new(
                request.KeptPendingPackageIds.Select(RackCensusRules.NormalizeCode), StringComparer.Ordinal);

            RackCensusRules.ReconcileOutcome outcome = RackCensusRules.Reconcile(
                packages, observations, movements, acknowledged, confirmedMissing, keptPending, ReadScope(session));
            if (!outcome.CanConfirm)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(409, new RackCensusReconcileResponse
                {
                    Code = "MOVEMENTS_PENDING", SessionId = request.SessionId, Counts = outcome.Counts,
                    Movements = movements, BlockReasons = outcome.BlockReasons,
                    Message = "Faltan decisiones antes de conciliar. Revisa los movimientos y los bultos no vistos."
                });
            }

            foreach (IEnumerable<RackCensusRules.PackageOutcome> batch in outcome.Packages.Chunk(500))
            {
                await connection.ExecuteAsync(@"
                    INSERT INTO rack_census_findings
                        (session_id,package_id,bucket,expected_location,observed_locations,decision,
                         confirmed_by,confirmed_at,created_at)
                    VALUES (@sessionId,@packageId,@bucket,@expected,@observed,@decision,
                         @confirmedBy,@confirmedAt,UTC_TIMESTAMP())
                    ON DUPLICATE KEY UPDATE bucket=VALUES(bucket),expected_location=VALUES(expected_location),
                         observed_locations=VALUES(observed_locations),decision=VALUES(decision),
                         confirmed_by=VALUES(confirmed_by),confirmed_at=VALUES(confirmed_at);",
                    batch.Select(item =>
                    {
                        bool missing = confirmedMissing.Contains(item.PackageId);
                        string decision = item.Bucket != RackCensusRules.BucketNotSeen ? "OBSERVADO"
                            : missing ? "FALTANTE_CONFIRMADO" : "PENDIENTE";
                        return new
                        {
                            sessionId, packageId = item.PackageId, bucket = item.Bucket,
                            expected = item.ExpectedLocationId,
                            observed = string.Join(",", item.ObservedLocationIds),
                            decision,
                            confirmedBy = missing ? user : null,
                            confirmedAt = missing
                                ? request.MissingConfirmations
                                    .Where(c => RackCensusRules.NormalizeCode(c.PackageId) == item.PackageId)
                                    .Select(c => (DateTime?)DateTimeOffset.FromUnixTimeMilliseconds(c.ConfirmedAtMillis).UtcDateTime)
                                    .FirstOrDefault()
                                : null
                        };
                    }), transaction);
            }

            // Única escritura fuera del censo: la última observación física.
            // Se limita a lo que se vio y no está en revisión por movimiento,
            // y jamás cambia `estatus` ni `trafdet.ubicacion`.
            HashSet<string> observedPackages = outcome.Packages
                .Where(p => p.Bucket is RackCensusRules.BucketMatched or RackCensusRules.BucketFoundElsewhere)
                .Select(p => p.PackageId).ToHashSet(StringComparer.Ordinal);
            long[] stampedLabels = packages
                .Where(p => observedPackages.Contains(p.PackageId))
                .SelectMany(p => p.LabelIds)
                .Distinct().ToArray();
            foreach (long[] batch in stampedLabels.Chunk(500))
            {
                await connection.ExecuteAsync(
                    "UPDATE etiqueta_mcia SET time_inv=NOW(),usu_inv=@user WHERE id_etiqueta_mcia IN @ids",
                    new { user, ids = batch }, transaction);
            }

            RackCensusReconcileResponse response = new()
            {
                Success = true, SessionId = request.SessionId, ClosedAtMillis = now,
                Counts = outcome.Counts, Movements = movements
            };
            await connection.ExecuteAsync(@"
                UPDATE rack_census_sessions
                   SET status=@status,reconcile_hash=@reconcileHash,reconcile_request_json=@requestJson,
                       response_json=@responseJson,note=@note,closed_by=@user,closed_at=UTC_TIMESTAMP()
                 WHERE session_id=@sessionId AND status=@open",
                new
                {
                    status = StatusReconciled, reconcileHash,
                    requestJson = JsonSerializer.Serialize(request, JsonOptions),
                    responseJson = JsonSerializer.Serialize(response, JsonOptions),
                    note = Truncate(request.Note, 2048), user, sessionId, open = StatusOpen
                }, transaction);

            commitStarted = true;
            await transaction.CommitAsync(CancellationToken.None);
            return new(200, response);
        }
        catch (GuardFailure error)
        {
            return new(error.StatusCode, new RackCensusReconcileResponse
            { Code = error.Code, Message = error.Message, SessionId = request.SessionId });
        }
        catch (ArgumentException error)
        {
            return new(400, new RackCensusReconcileResponse
            { Code = "INVALID_CENSUS", Message = error.Message, SessionId = request.SessionId });
        }
        catch (MySqlException error) when (IsSchemaError(error))
        {
            return new(503, new RackCensusReconcileResponse
            { Code = "MIGRATION_REQUIRED", SessionId = request.SessionId, Message = MigrationMessage });
        }
        catch (OperationCanceledException) when (!commitStarted) { throw; }
        catch (Exception error)
        {
            logger.LogError(error, "Error conciliando el censo {SessionId}; CommitStarted={CommitStarted}",
                request.SessionId, commitStarted);
            return new(503, new RackCensusReconcileResponse
            {
                Code = commitStarted ? "COMMIT_UNCERTAIN" : "CENSUS_FAILED", SessionId = request.SessionId,
                Message = "No se pudo confirmar la conciliación. Conserva el recorrido y reintenta el mismo envío."
            });
        }
        finally { await ReleaseLocksAsync(connection, heldLocks); }
    }

    // ── Consultas ────────────────────────────────────────────────────────────

    /// <summary>
    /// Etiquetas pendientes cuya partida está en una ubicación del alcance. Los
    /// alias son explícitos porque Dapper no normaliza el guion bajo.
    /// </summary>
    private const string ScopeLabelsSql = @"
        SELECT e.id_etiqueta_mcia                        AS LabelId,
               COALESCE(e.textoetiqueta,'')              AS Textoetiqueta,
               COALESCE(TRIM(e.creferencia),'')          AS Creferencia,
               COALESCE(e.nlineno,0)                     AS Nlineno,
               COALESCE(TRIM(e.estatus),'')              AS Estatus,
               COALESCE(TRIM(d.ubicacion),'')            AS Ubicacion,
               COALESCE(NULLIF(TRIM(d.cdescripcion),''),'') AS Descripcion,
               COALESCE(TRIM(c.cnombre),'')              AS Cliente
          FROM etiqueta_mcia e
          JOIN trafdet d ON d.creferencia = e.creferencia AND d.nlineno = e.nlineno
          LEFT JOIN traf t ON t.creferencia = e.creferencia
          LEFT JOIN clientes c ON c.cidcte = t.cclienteidtraf
         WHERE COALESCE(TRIM(e.textoetiqueta),'') <> ''
           AND COALESCE(TRIM(e.estatus),'') NOT IN ('CARGADO','SALIDA','ELIMINAR')
           AND UPPER(TRIM(d.ubicacion)) IN @scope";

    /// <summary>
    /// Catálogo de otras ubicaciones. Sirve para nombrar un bulto ajeno que
    /// aparece dentro del rack en lugar de reportarlo como no identificado.
    /// </summary>
    private const string ForeignLabelsSql = @"
        SELECT e.id_etiqueta_mcia                        AS LabelId,
               COALESCE(e.textoetiqueta,'')              AS Textoetiqueta,
               COALESCE(TRIM(e.creferencia),'')          AS Creferencia,
               COALESCE(e.nlineno,0)                     AS Nlineno,
               COALESCE(TRIM(e.estatus),'')              AS Estatus,
               COALESCE(TRIM(d.ubicacion),'')            AS Ubicacion,
               COALESCE(NULLIF(TRIM(d.cdescripcion),''),'') AS Descripcion,
               COALESCE(TRIM(c.cnombre),'')              AS Cliente
          FROM etiqueta_mcia e
          JOIN trafdet d ON d.creferencia = e.creferencia AND d.nlineno = e.nlineno
          LEFT JOIN traf t ON t.creferencia = e.creferencia
          LEFT JOIN clientes c ON c.cidcte = t.cclienteidtraf
         WHERE COALESCE(TRIM(e.textoetiqueta),'') <> ''
           AND COALESCE(TRIM(e.estatus),'') NOT IN ('CARGADO','SALIDA','ELIMINAR')
           AND TRIM(d.ubicacion) <> ''
           AND UPPER(TRIM(d.ubicacion)) NOT IN @scope
         ORDER BY e.id_etiqueta_mcia
         LIMIT @limit";

    /// <summary>
    /// Agrupa las filas por código impreso. Un código cuyas filas no coinciden
    /// en ubicación esperada se excluye: elegir una sería adivinar.
    /// </summary>
    private static (List<SnapshotItemRow>, List<string>) BuildSnapshotItems(
        List<LabelRow> scopeRows, List<LabelRow> foreignRows, HashSet<string> scope)
    {
        List<SnapshotItemRow> items = new();
        List<string> warnings = new();
        IEnumerable<(LabelRow Row, bool InScope)> all =
            scopeRows.Select(r => (r, true)).Concat(foreignRows.Select(r => (r, false)));
        foreach (IGrouping<string, (LabelRow Row, bool InScope)> group in all
            .Where(entry => RackCensusRules.NormalizeCode(entry.Row.Textoetiqueta).Length > 0)
            .GroupBy(entry => RackCensusRules.NormalizeCode(entry.Row.Textoetiqueta), StringComparer.Ordinal))
        {
            string[] locations = group
                .Select(entry => RackCensusRules.NormalizeLocation(entry.Row.Ubicacion))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (locations.Length != 1)
            {
                warnings.Add($"El código {group.Key} aparece con {locations.Length} ubicaciones esperadas distintas; queda fuera del censo.");
                continue;
            }
            bool inScope = scope.Contains(locations[0]);
            foreach ((LabelRow row, bool _) in group)
            {
                items.Add(new SnapshotItemRow
                {
                    PackageId = group.Key, LabelId = row.LabelId, Creferencia = row.Creferencia,
                    Nlineno = row.Nlineno, ExpectedLocation = locations[0], Estatus = row.Estatus,
                    Cliente = Truncate(row.Cliente, 120), Descripcion = Truncate(row.Descripcion, 160),
                    InScope = inScope
                });
            }
        }
        return (items, warnings);
    }

    private async Task<RackCensusOpenResponse> BuildOpenResponseAsync(MySqlConnection connection, SessionRow session, bool replayed)
    {
        Dictionary<string, RackCensusPackage> packages = await ReadPackagesAsync(connection, session.SessionId);
        return new RackCensusOpenResponse
        {
            Success = true, SessionId = Guid.Parse(session.SessionId), WarehouseId = session.WarehouseId,
            SnapshotVersion = session.SnapshotVersion, CapturedAtMillis = session.CapturedAtMillis,
            ScopeLocationIds = ReadScope(session).OrderBy(l => l, StringComparer.Ordinal).ToArray(),
            Packages = packages.Values.OrderBy(p => p.ExpectedLocationId, StringComparer.Ordinal)
                .ThenBy(p => p.PackageId, StringComparer.Ordinal).ToArray(),
            ForeignCatalogTruncated = session.ForeignCatalogTruncated,
            DataWarnings = JsonSerializer.Deserialize<string[]>(
                string.IsNullOrWhiteSpace(session.DataWarningsJson) ? "[]" : session.DataWarningsJson, JsonOptions)
                ?? Array.Empty<string>(),
            Replayed = replayed
        };
    }

    private static async Task<Dictionary<string, RackCensusPackage>> ReadPackagesAsync(
        MySqlConnection connection, string sessionId, MySqlTransaction? transaction = null)
    {
        List<SnapshotItemRow> rows = (await connection.QueryAsync<SnapshotItemRow>(@"
            SELECT package_id AS PackageId,id_etiqueta_mcia AS LabelId,creferencia AS Creferencia,
                   nlineno AS Nlineno,expected_location AS ExpectedLocation,estatus AS Estatus,
                   cliente AS Cliente,descripcion AS Descripcion,in_scope AS InScope
              FROM rack_census_snapshot_items WHERE session_id=@sessionId",
            new { sessionId }, transaction)).ToList();
        return rows
            .GroupBy(row => row.PackageId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                SnapshotItemRow first = group.First();
                RackCensusRules.ParsedCode? parsed = RackCensusRules.Parse(group.Key);
                return new RackCensusPackage
                {
                    PackageId = group.Key,
                    LabelIds = group.Select(row => row.LabelId).Distinct().OrderBy(id => id).ToArray(),
                    ExpectedLocationId = first.ExpectedLocation,
                    Reference = first.Creferencia, Line = first.Nlineno, Estatus = first.Estatus,
                    Cliente = first.Cliente, Descripcion = first.Descripcion,
                    DeclaredTotal = parsed?.DeclaredTotal, PrintedOrdinal = parsed?.PrintedOrdinal,
                    InScope = first.InScope
                };
            }, StringComparer.Ordinal);
    }

    /// <summary>
    /// Compara el snapshot con el estado actual. Si el cambio no se puede
    /// fechar, se toma el reloj actual: un movimiento sin fecha confiable cuenta
    /// como posterior al snapshot, que es el lado seguro.
    /// </summary>
    private static async Task<List<RackCensusMovement>> DetectMovementsAsync(
        MySqlConnection connection, MySqlTransaction? transaction, SessionRow session, long now)
    {
        List<MovementCandidateRow> rows = (await connection.QueryAsync<MovementCandidateRow>(@"
            SELECT s.package_id                                AS PackageId,
                   s.creferencia                               AS Creferencia,
                   s.expected_location                         AS ExpectedLocation,
                   COALESCE(s.estatus,'')                      AS SnapshotEstatus,
                   COALESCE(TRIM(e.estatus),'')                AS CurrentEstatus,
                   CASE WHEN e.id_etiqueta_mcia IS NULL THEN 1 ELSE 0 END AS Removed,
                   COALESCE(UPPER(TRIM(d.ubicacion)),'')       AS CurrentLocation,
                   CASE WHEN YEAR(e.fmodi) >= 2000
                        THEN CAST(UNIX_TIMESTAMP(e.fmodi)*1000 AS SIGNED) ELSE 0 END AS LabelChangedMillis,
                   CASE WHEN YEAR(d.fmodi) >= 2000
                        THEN CAST(UNIX_TIMESTAMP(d.fmodi)*1000 AS SIGNED) ELSE 0 END AS LineChangedMillis
              FROM rack_census_snapshot_items s
              LEFT JOIN etiqueta_mcia e ON e.id_etiqueta_mcia = s.id_etiqueta_mcia
              LEFT JOIN trafdet d ON d.creferencia = s.creferencia AND d.nlineno = s.nlineno
             WHERE s.session_id = @sessionId",
            new { sessionId = session.SessionId }, transaction)).ToList();

        List<RackCensusMovement> movements = new();
        foreach (IGrouping<string, MovementCandidateRow> group in rows.GroupBy(r => r.PackageId, StringComparer.Ordinal))
        {
            MovementCandidateRow first = group.First();
            bool removed = group.All(r => r.Removed == 1);
            bool released = group.Any(r => r.Removed == 0 && RackCensusRules.IsReleased(r.CurrentEstatus));
            string? movedTo = group
                .Where(r => r.Removed == 0 && r.CurrentLocation.Length > 0 && r.CurrentLocation != first.ExpectedLocation)
                .Select(r => r.CurrentLocation).FirstOrDefault();
            bool statusChanged = group.Any(r => r.Removed == 0 &&
                !string.Equals(r.CurrentEstatus, r.SnapshotEstatus, StringComparison.OrdinalIgnoreCase));
            string? kind = removed ? RackCensusRules.MovementLabelRemoved
                : released ? RackCensusRules.MovementLeftWarehouse
                : movedTo is not null ? RackCensusRules.MovementLocationChanged
                : statusChanged ? RackCensusRules.MovementStatusChanged
                : null;
            if (kind is null) continue;
            long dated = group.Max(r => Math.Max(r.LabelChangedMillis, r.LineChangedMillis));
            movements.Add(new RackCensusMovement
            {
                PackageId = first.PackageId, Reference = first.Creferencia,
                ExpectedLocationId = first.ExpectedLocation, Kind = kind,
                FromLocationId = first.ExpectedLocation,
                ToLocationId = kind == RackCensusRules.MovementLocationChanged ? movedTo : null,
                DetectedVersion = dated > session.SnapshotVersion ? dated : now
            });
        }
        return movements;
    }

    private static async Task<SessionRow?> ReadSessionAsync(
        MySqlConnection connection, MySqlTransaction? transaction, string sessionId) =>
        await connection.QuerySingleOrDefaultAsync<SessionRow>(@"
            SELECT session_id AS SessionId,warehouse_id AS WarehouseId,usuario AS Usuario,scope_json AS ScopeJson,
                   snapshot_version AS SnapshotVersion,
                   CAST(UNIX_TIMESTAMP(snapshot_captured_at)*1000 AS SIGNED) AS CapturedAtMillis,
                   foreign_catalog_truncated AS ForeignCatalogTruncated,data_warnings_json AS DataWarningsJson,
                   status AS Status,request_hash AS RequestHash,reconcile_hash AS ReconcileHash,
                   response_json AS ResponseJson
              FROM rack_census_sessions WHERE session_id=@sessionId",
            new { sessionId }, transaction);

    private async Task<SessionRow> RequireSessionAsync(MySqlConnection connection, Guid sessionId, string user)
    {
        SessionRow session = await ReadSessionAsync(connection, null, sessionId.ToString("D"))
            ?? throw new GuardFailure(404, "SESSION_NOT_FOUND", "No se encontró el censo solicitado.");
        if (!string.Equals(session.Usuario, user, StringComparison.OrdinalIgnoreCase))
            throw new GuardFailure(403, "PERMISSION_DENIED", "El censo pertenece a otro usuario.");
        return session;
    }

    private static HashSet<string> ReadScope(SessionRow session) =>
        (JsonSerializer.Deserialize<string[]>(
            string.IsNullOrWhiteSpace(session.ScopeJson) ? "[]" : session.ScopeJson, JsonOptions)
         ?? Array.Empty<string>())
        .Select(RackCensusRules.NormalizeLocation).ToHashSet(StringComparer.Ordinal);

    // ── Infraestructura ──────────────────────────────────────────────────────

    private const string MigrationMessage =
        "El censo de racks requiere la migración sql/2026-10-15-censo-racks.sql. Conserva el recorrido; solicita actualizar la base de datos.";

    private static async Task EnsureMigrationAsync(MySqlConnection connection)
    {
        long columns = await connection.ExecuteScalarAsync<long>(@"
            SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND
             ((table_name='rack_census_sessions' AND column_name IN
                ('session_id','warehouse_id','usuario','scope_json','snapshot_version','snapshot_captured_at',
                 'foreign_catalog_truncated','data_warnings_json','status','request_hash','reconcile_hash',
                 'reconcile_request_json','response_json','note','closed_by','closed_at','created_at'))
              OR (table_name='rack_census_snapshot_items' AND column_name IN
                ('session_id','package_id','id_etiqueta_mcia','creferencia','nlineno','expected_location',
                 'estatus','cliente','descripcion','in_scope'))
              OR (table_name='rack_census_observations' AND column_name IN
                ('observation_id','session_id','usuario','raw_code','normalized_code','package_id',
                 'id_etiqueta_mcia','observed_location','observed_at','resolution','created_at'))
              OR (table_name='rack_census_findings' AND column_name IN
                ('session_id','package_id','bucket','expected_location','observed_locations','decision',
                 'confirmed_by','confirmed_at','created_at')))");
        if (columns != 47) throw new GuardFailure(503, "MIGRATION_REQUIRED", MigrationMessage);
        long transactionalTables = await connection.ExecuteScalarAsync<long>(@"
            SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE()
            AND table_name IN ('etiqueta_mcia','trafdet','rack_census_sessions','rack_census_snapshot_items',
                'rack_census_observations','rack_census_findings') AND engine='InnoDB'");
        if (transactionalTables != 6)
            throw new GuardFailure(503, "MIGRATION_REQUIRED",
                "Las tablas del censo deben usar InnoDB antes de habilitar la conciliación.");
    }

    private async Task RequirePermissionAsync(string user, string action, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(user, MobileModuleIds.Inventory, action, cancellationToken))
            throw new GuardFailure(403, "PERMISSION_DENIED", $"Necesitas permiso de {action} en Inventario para el censo de racks.");
    }

    private static async Task<long> ServerMillisAsync(MySqlConnection connection, MySqlTransaction? transaction) =>
        await connection.ExecuteScalarAsync<long>(
            "SELECT CAST(UNIX_TIMESTAMP(NOW(3))*1000 AS SIGNED)", transaction: transaction);

    private static async Task AcquireLockAsync(MySqlConnection connection, string name, List<string> heldLocks)
    {
        if (await connection.ExecuteScalarAsync<int>("SELECT GET_LOCK(@name,10)", new { name }) != 1)
            throw new GuardFailure(503, "CENSUS_BUSY", "El censo está ocupado. Reintenta el mismo envío en unos segundos.");
        heldLocks.Add(name);
    }

    private async Task ReleaseLocksAsync(MySqlConnection connection, List<string> heldLocks)
    {
        foreach (string lockName in heldLocks.AsEnumerable().Reverse())
        {
            try { await connection.ExecuteAsync("SELECT RELEASE_LOCK(@lockName)", new { lockName }); }
            catch (Exception error) { logger.LogWarning(error, "No se pudo liberar un bloqueo de censo; se cerrará la conexión."); }
        }
    }

    /// <summary>Huella estable para detectar un reintento con otro contenido.</summary>
    private static string Fingerprint(params object[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\u001F', parts.Select(part => part is IEnumerable<string> list
                ? string.Join(',', list) : part?.ToString() ?? "")))));

    private static string BuildDecisionKey(RackCensusReconcileRequest request) => string.Join('|',
        string.Join(',', request.AcknowledgedMovementPackageIds.Select(RackCensusRules.NormalizeCode).OrderBy(id => id, StringComparer.Ordinal)),
        string.Join(',', request.MissingConfirmations.Select(c => RackCensusRules.NormalizeCode(c.PackageId)).OrderBy(id => id, StringComparer.Ordinal)),
        string.Join(',', request.KeptPendingPackageIds.Select(RackCensusRules.NormalizeCode).OrderBy(id => id, StringComparer.Ordinal)));

    private static string Truncate(string? value, int max)
    {
        string trimmed = (value ?? "").Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static bool IsSchemaError(MySqlException error) => error.Number is 1146 or 1054;

    private static RackCensusResult Failure(GuardFailure error, Guid sessionId) => new(error.StatusCode,
        new RackCensusOpenResponse { Code = error.Code, Message = error.Message, SessionId = sessionId });

    private static RackCensusResult MigrationRequired(Guid sessionId) => new(503,
        new RackCensusOpenResponse { Code = "MIGRATION_REQUIRED", SessionId = sessionId, Message = MigrationMessage });
}
