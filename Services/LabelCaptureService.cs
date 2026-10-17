using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Dapper;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using PMMovil.Api.Attributes;
using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Confirma una lectura y su evidencia. La imagen queda durable antes del COMMIT;
/// SQL y disco tienen recuperación/compensación explícita, no una transacción distribuida.
/// </summary>
public sealed class LabelCaptureService(
    BaseRepository database,
    IConfiguration configuration,
    IModulePermissionService permissions,
    RevisionActivityService activity,
    RevisionService revision,
    IHubContext<WarehouseHub> hub,
    ILogger<LabelCaptureService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string basePath = Path.GetFullPath(configuration["PhotoSettings:BasePath"] ?? @"F:\Trafico\fotos");

    private sealed class PartRow
    {
        public int IdRevdet { get; set; }
        public string Cnofact { get; set; } = "";
        public string CrefAgrupada { get; set; } = "";
        public string ClienteId { get; set; } = "";
        public string NumLote { get; set; } = "";
        public string NumSer { get; set; } = "";
        public string Marca { get; set; } = "";
        public string Modelo { get; set; } = "";
        public string CPaisOrigen { get; set; } = "";
        public bool Revisada { get; set; }
        public LabelCaptureSnapshot Snapshot() => new()
        {
            NumLote = NumLote, NumSer = NumSer, Marca = Marca, Modelo = Modelo,
            CPaisOrigen = CPaisOrigen, Revisada = Revisada
        };
    }

    private sealed class FactRow
    {
        public string CrefAgrupada { get; set; } = "";
        public string Creferencia { get; set; } = "";
        public string ClienteId { get; set; } = "";
        public int StatusRev { get; set; }
        public string Editor { get; set; } = "";
    }

    private sealed class SavedCapture
    {
        public string RequestHash { get; set; } = "";
        public string Usuario { get; set; } = "";
        public string ResponseJson { get; set; } = "";
    }

    private sealed class ColumnLimit
    {
        public string ColumnName { get; set; } = "";
        public long? MaxLength { get; set; }
    }

    private sealed class TimerRow
    {
        public bool Closed { get; set; }
    }

    private sealed record RecoveryManifest(string CaptureId, string Cref, int Line, string Extension, string PhotoHash, string FinalPath);

    private sealed class GuardFailure(int statusCode, string code, string message, LabelCaptureSnapshot? snapshot = null)
        : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
        public string Code { get; } = code;
        public LabelCaptureSnapshot? Snapshot { get; } = snapshot;
    }

    public async Task<LabelCaptureResult> GetContextAsync(int id, string cref, string user, CancellationToken cancellationToken)
    {
        try
        {
            using MySqlConnection connection = (MySqlConnection)database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureMigrationAsync(connection);
            await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            (PartRow part, LabelCaptureSnapshot snapshot, string? blocked) =
                await ReadContextAsync(connection, transaction, id, cref.Trim(), user, false);
            bool editable = await permissions.HasPermissionAsync(user, MobileModuleIds.Review, "Modificar", cancellationToken);
            bool canAdd = await permissions.HasPermissionAsync(user, MobileModuleIds.Review, "Agregar", cancellationToken);
            return new(200, new LabelCaptureResponse
            {
                Success = true, IdRevdet = part.IdRevdet, Snapshot = snapshot, Revisada = snapshot.Revisada,
                CanEdit = editable && canAdd && blocked is null, CanMarkReviewed = editable && canAdd && blocked is null,
                Message = blocked
            });
        }
        catch (GuardFailure error) { return Failure(error, id); }
        catch (MySqlException error) when (IsSchemaError(error)) { return MigrationRequired(id); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            logger.LogError(error, "Error obteniendo contexto de captura para partida {IdRevdet}", id);
            return new(500, new() { Code = "CONTEXT_FAILED", IdRevdet = id, Message = "No se pudo consultar la partida. Reintenta la lectura del contexto." });
        }
    }

    public async Task<LabelCaptureResult> ConfirmAsync(ConfirmLabelCaptureRequest request, string user, CancellationToken cancellationToken)
    {
        string? stagedPath = null;
        string? finalPath = null;
        string? manifestPath = null;
        bool ownsFinalFile = false;
        bool ownsManifest = false;
        bool committed = false;
        bool commitStarted = false;
        List<string> heldLocks = new();
        using MySqlConnection connection = (MySqlConnection)database.CreateConnection();
        try
        {
            LabelCaptureRules.Validate(request);
            if (!await permissions.HasPermissionAsync(user, MobileModuleIds.Review, "Modificar", cancellationToken) ||
                !await permissions.HasPermissionAsync(user, MobileModuleIds.Review, "Agregar", cancellationToken))
                throw new GuardFailure(403, "PERMISSION_DENIED", "Necesitas permiso para modificar la revisión y agregar evidencia.");

            byte[] photo = DecodePhoto(request);
            string photoHash = Convert.ToHexString(SHA256.HashData(photo));
            string requestJson = JsonSerializer.Serialize(request with { PhotoBase64 = "" }, JsonOptions);
            string requestHash = LabelCaptureRules.Fingerprint(request, photoHash);
            string captureId = request.CaptureId.ToString("D");
            string cref = request.CrefAgrupada.Trim().ToUpperInvariant();
            string extension = PhotoFileNameResolver.NormalizeExtension(request.Extension);
            string stagingDirectory = Path.Combine(basePath, ".label-captures");
            manifestPath = Path.Combine(stagingDirectory, $"{captureId}.json");

            await connection.OpenAsync(cancellationToken);
            await EnsureMigrationAsync(connection);
            await AcquireLockAsync(connection, $"label_capture_{captureId}", heldLocks);
            SavedCapture? previous = await connection.QuerySingleOrDefaultAsync<SavedCapture>(
                "SELECT request_hash AS RequestHash, usuario AS Usuario, response_json AS ResponseJson FROM revision_label_captures WHERE capture_id=@captureId", new { captureId });
            if (previous is not null)
            {
                if (!string.Equals(previous.Usuario, user, StringComparison.OrdinalIgnoreCase) || previous.RequestHash != requestHash)
                    throw new GuardFailure(409, "CAPTURE_CONFLICT", "El identificador de captura ya corresponde a otro envío. Conserva el envío original o inicia una nueva captura.");
                LabelCaptureResponse saved = JsonSerializer.Deserialize<LabelCaptureResponse>(previous.ResponseJson, JsonOptions)
                    ?? throw new InvalidDataException("No se pudo recuperar la confirmación guardada.");
                DeleteQuietly(manifestPath);
                return new(200, saved with { Replayed = true });
            }

            // El mismo bloqueo que Foto/subir evita colisiones con el consecutivo legacy.
            await AcquireLockAsync(connection, $"lock_foto_{cref}", heldLocks);
            await AcquireLockAsync(connection, $"hazmat_{cref}", heldLocks);
            Directory.CreateDirectory(stagingDirectory);
            await RecoverUncommittedFileAsync(connection, manifestPath, captureId, cref);
            stagedPath = Path.Combine(stagingDirectory, $"{captureId}.staged");
            await WriteDurableAsync(stagedPath, photo, FileMode.Create, cancellationToken);

            await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            (PartRow part, LabelCaptureSnapshot before, string? blocked) =
                await ReadContextAsync(connection, transaction, request.IdRevdet, cref, user, true);
            if (blocked is not null) throw new GuardFailure(409, "REVIEW_LOCKED", blocked, before);
            if (!LabelCaptureRules.SnapshotsMatch(request.ExpectedSnapshot!, before))
                throw new GuardFailure(409, "SNAPSHOT_CONFLICT", "La partida o los datos Hazmat cambiaron. Revisa los datos actuales antes de confirmar otra vez.", before);

            LabelCaptureSnapshot after = LabelCaptureRules.Apply(before, request.Fields, request.Hazmat, request.MarkReviewed);
            await ValidateCatalogsAsync(connection, transaction, request);
            await ValidateColumnLengthsAsync(connection, transaction, after);

            string folder = (await connection.ExecuteScalarAsync<string?>(
                "SELECT COALESCE(NULLIF(carpeta,''),cidcte) FROM clientes WHERE cidcte=@cliente LIMIT 1",
                new { cliente = part.ClienteId }, transaction))?.Trim()
                ?? throw new GuardFailure(409, "CLIENT_NOT_FOUND", "La partida no tiene un cliente válido para guardar la evidencia.");
            string targetDirectory = CheckedDirectory(basePath, folder);
            Directory.CreateDirectory(targetDirectory);
            int line = await connection.ExecuteScalarAsync<int>(
                "SELECT COALESCE(MAX(nlineno),0)+1 FROM fotos WHERE creferencia=@cref AND ntipodocto=107", new { cref }, transaction);
            // Si una ejecución anterior quedó entre disco y SQL, no se sobrescribe su archivo.
            do
            {
                finalPath = Path.Combine(targetDirectory, PhotoFileNameResolver.Build(cref, 107, line, extension));
                if (!File.Exists(finalPath)) break;
                line++;
            } while (true);
            RecoveryManifest manifest = new(captureId, cref, line, extension, photoHash, finalPath);
            await WriteDurableAsync(manifestPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonOptions)), FileMode.Create, cancellationToken);
            ownsManifest = true;
            await WriteDurableAsync(finalPath, photo, FileMode.CreateNew, cancellationToken, () => ownsFinalFile = true);

            await connection.ExecuteAsync(@"
                UPDATE revdet SET numlote=@NumLote,numser=@NumSer,marca=@Marca,modelo=@Modelo,
                    cpaisorigen=@CPaisOrigen,revisada=@Revisada,usumodi=@user,fmodi=NOW()
                WHERE idrevdet=@id", new { after.NumLote, after.NumSer, after.Marca, after.Modelo,
                    after.CPaisOrigen, after.Revisada, user, id = part.IdRevdet }, transaction);
            if (request.Hazmat is not null &&
                (request.Hazmat.Un is not null || request.Hazmat.Clase is not null || request.Hazmat.Grupo is not null))
                await SaveHazmatAsync(connection, transaction, cref, after);

            int photoId = await connection.ExecuteScalarAsync<int>(@"
                INSERT INTO fotos (creferencia,nlineno,ntipodocto,cextension,fcrea,usucrea)
                VALUES (@cref,@line,107,@extension,NOW(),@user); SELECT LAST_INSERT_ID();",
                new { cref, line, extension, user }, transaction);
            LabelCaptureResponse response = new()
            {
                Success = true, CaptureId = request.CaptureId, IdRevdet = part.IdRevdet, PhotoId = photoId,
                Snapshot = after, Revisada = after.Revisada, CanEdit = true, CanMarkReviewed = true,
                Message = request.MarkReviewed ? "Captura guardada y partida marcada como revisada." : "Captura y evidencia guardadas. La partida conserva su estado de revisión."
            };
            await connection.ExecuteAsync(@"
                INSERT INTO revision_label_captures
                    (capture_id,idrevdet,cref_agrupada,cnofact,usuario,request_hash,photo_hash,idfotos,
                     request_json,before_json,after_json,response_json,physical_confirmed,marked_reviewed,created_at)
                VALUES (@captureId,@id,@cref,@cnofact,@user,@requestHash,@photoHash,@photoId,
                    @requestJson,@beforeJson,@afterJson,@responseJson,@physicalConfirmed,@markedReviewed,UTC_TIMESTAMP());",
                new { captureId, id = part.IdRevdet, cref, cnofact = part.Cnofact, user, requestHash, photoHash, photoId,
                    // La foto ya tiene hash y archivo propio; no se duplica el base64 en la bitácora.
                    requestJson,
                    beforeJson = JsonSerializer.Serialize(before, JsonOptions), afterJson = JsonSerializer.Serialize(after, JsonOptions),
                    responseJson = JsonSerializer.Serialize(response, JsonOptions),
                    physicalConfirmed = request.PhysicalReviewConfirmed, markedReviewed = request.MarkReviewed }, transaction);

            // Cancelar una petición después de COMMIT no autoriza a borrar la evidencia.
            commitStarted = true;
            await transaction.CommitAsync(CancellationToken.None);
            committed = true;
            DeleteQuietly(manifestPath);
            await NotifyAsync(cref, part.Cnofact, part.IdRevdet, user, request.MarkReviewed);
            return new(200, response);
        }
        catch (GuardFailure error) { return Failure(error, request.IdRevdet, request.CaptureId); }
        catch (ArgumentException error)
        {
            return new(400, new() { Code = "INVALID_CAPTURE", CaptureId = request.CaptureId, IdRevdet = request.IdRevdet, Message = error.Message });
        }
        catch (MySqlException error) when (IsSchemaError(error)) { return MigrationRequired(request.IdRevdet, request.CaptureId); }
        catch (OperationCanceledException) when (!commitStarted) { throw; }
        catch (Exception error)
        {
            logger.LogError(error, "Error confirmando captura {CaptureId} de partida {IdRevdet}; CommitStarted={CommitStarted}", request.CaptureId, request.IdRevdet, commitStarted);
            return new(503, new()
            {
                Code = commitStarted ? "COMMIT_UNCERTAIN" : "CAPTURE_FAILED", CaptureId = request.CaptureId, IdRevdet = request.IdRevdet,
                Message = "No se pudo confirmar la respuesta. Conserva la captura y reintenta exactamente el mismo envío."
            });
        }
        finally
        {
            DeleteQuietly(stagedPath);
            if (!committed && !commitStarted)
            {
                bool removed = !ownsFinalFile || DeleteQuietly(finalPath);
                if (ownsManifest && removed) DeleteQuietly(manifestPath);
            }
            // Si COMMIT tuvo resultado incierto se conserva el archivo. Reintentar el mismo
            // UUID consulta el resultado persistido; no se elimina evidencia posiblemente válida.
            foreach (string lockName in heldLocks.AsEnumerable().Reverse())
            {
                try { await connection.ExecuteAsync("SELECT RELEASE_LOCK(@lockName)", new { lockName }); }
                catch (Exception error) { logger.LogWarning(error, "No se pudo liberar un bloqueo de captura; se cerrará la conexión."); }
            }
        }
    }

    private static async Task EnsureMigrationAsync(MySqlConnection connection)
    {
        long columns = await connection.ExecuteScalarAsync<long>(@"
            SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND
             ((table_name='revision_label_captures' AND column_name IN
                ('capture_id','idrevdet','cref_agrupada','cnofact','usuario','request_hash','photo_hash','idfotos',
                 'request_json','before_json','after_json','response_json','physical_confirmed','marked_reviewed','created_at'))
              OR (table_name='traf3' AND column_name='hazmat_grupo'))");
        if (columns != 16)
            throw new GuardFailure(503, "MIGRATION_REQUIRED", "La captura segura requiere la migración 20261015_revision_label_captures y la columna Hazmat. Conserva el borrador; solicita actualizar la base de datos.");
        long transactionalTables = await connection.ExecuteScalarAsync<long>(@"
            SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE()
            AND table_name IN ('revdet','traf3','fotos','revision_label_captures') AND engine='InnoDB'");
        if (transactionalTables != 4)
            throw new GuardFailure(503, "MIGRATION_REQUIRED", "Las tablas de captura deben usar InnoDB antes de habilitar la confirmación conjunta.");
    }

    private static async Task<(PartRow, LabelCaptureSnapshot, string?)> ReadContextAsync(
        MySqlConnection connection, MySqlTransaction transaction, int id, string cref, string user, bool lockRows)
    {
        string suffix = lockRows ? " FOR UPDATE" : "";
        PartRow? part = await connection.QuerySingleOrDefaultAsync<PartRow>(@"
            SELECT idrevdet AS IdRevdet,TRIM(cnofact) AS Cnofact,COALESCE(TRIM(cref_agrupada),'') AS CrefAgrupada,
                COALESCE(TRIM(cclienteid),'') AS ClienteId,COALESCE(numlote,'') AS NumLote,COALESCE(numser,'') AS NumSer,
                COALESCE(marca,'') AS Marca,COALESCE(modelo,'') AS Modelo,COALESCE(cpaisorigen,'') AS CPaisOrigen,revisada AS Revisada
            FROM revdet WHERE idrevdet=@id" + suffix, new { id }, transaction);
        if (part is null || (part.CrefAgrupada.Length > 0 && !part.CrefAgrupada.Equals(cref, StringComparison.OrdinalIgnoreCase)))
            throw new GuardFailure(404, "PART_NOT_FOUND", "La partida no pertenece a la referencia seleccionada.");
        List<FactRow> facts = (await connection.QueryAsync<FactRow>(@"
            SELECT COALESCE(TRIM(cref_agrupada),'') AS CrefAgrupada,COALESCE(TRIM(creferencia),'') AS Creferencia,
                   COALESCE(TRIM(cclienteid),'') AS ClienteId,COALESCE(status_rev,0) AS StatusRev,COALESCE(TRIM(usuedit),'') AS Editor
            FROM fact WHERE cnofact=@cnofact" + suffix, new { cnofact = part.Cnofact }, transaction)).ToList();
        if (facts.Count == 0 || facts.Any(f => !f.CrefAgrupada.Equals(cref, StringComparison.OrdinalIgnoreCase) &&
            !f.Creferencia.Equals(cref, StringComparison.OrdinalIgnoreCase)))
            throw new GuardFailure(404, "PART_NOT_FOUND", "La factura de la partida no pertenece a esta referencia.");
        if (part.ClienteId.Length == 0) part.ClienteId = facts.Select(f => f.ClienteId).FirstOrDefault(c => c.Length > 0) ?? "";
        List<LabelCaptureSnapshot> hazmatRows = (await connection.QueryAsync<LabelCaptureSnapshot>(@"
            SELECT COALESCE(hazmat_un,'') AS HazmatUn,COALESCE(hazmat_clase,'') AS HazmatClase,
                   COALESCE(hazmat_grupo,'') AS HazmatGrupo FROM traf3 WHERE creferencia=@cref" + suffix,
            new { cref }, transaction)).ToList();
        LabelCaptureSnapshot snapshot = part.Snapshot();
        if (hazmatRows.Count > 0)
        {
            LabelCaptureSnapshot first = hazmatRows[0];
            if (hazmatRows.Any(row => LabelCaptureRules.Normalize(row.HazmatUn) != LabelCaptureRules.Normalize(first.HazmatUn) ||
                LabelCaptureRules.Normalize(row.HazmatClase) != LabelCaptureRules.Normalize(first.HazmatClase) ||
                LabelCaptureRules.Normalize(row.HazmatGrupo) != LabelCaptureRules.Normalize(first.HazmatGrupo)))
                throw new GuardFailure(409, "HAZMAT_INCONSISTENT", "La referencia tiene datos Hazmat distintos entre registros. Deben revisarse antes de confirmar la captura.");
            snapshot = snapshot with { HazmatUn = first.HazmatUn, HazmatClase = first.HazmatClase, HazmatGrupo = first.HazmatGrupo };
        }
        IEnumerable<int> unlocks = await connection.QueryAsync<int>(@"
            SELECT id_desbloqueo FROM rev_desbloqueos WHERE cnofact=@cnofact AND usuario_solicita=@user AND estatus=1
               AND (fecha_expiracion IS NULL OR fecha_expiracion>NOW())" + suffix, new { cnofact = part.Cnofact, user }, transaction);
        if (unlocks.Any()) return (part, snapshot, null);
        if (facts.Any(f => f.StatusRev == 1)) return (part, snapshot, "La factura está completa. Solicita su desbloqueo antes de modificarla.");
        if (facts.Any(f => f.Editor.Length > 0 && !f.Editor.Equals(user, StringComparison.OrdinalIgnoreCase)))
            return (part, snapshot, "Otro usuario está editando la factura.");
        List<string?> relations = (await connection.QueryAsync<string?>(@"
            SELECT r.STATUS FROM traf2 tf2 LEFT JOIN relacion r ON tf2.nrelacion=r.relno
            WHERE tf2.CREF_ORIGEN=@cref" + suffix, new { cref }, transaction)).ToList();
        if (relations.Count > 0 && relations.All(status => string.Equals(status?.Trim(), "COMPLETA", StringComparison.OrdinalIgnoreCase)))
            return (part, snapshot, "La relación está completa. Solicita su desbloqueo antes de modificarla.");
        TimerRow? timer = await connection.QuerySingleOrDefaultAsync<TimerRow>(@"
            SELECT CASE WHEN ffinrevision IS NOT NULL AND YEAR(ffinrevision)>=2000 AND estatus_final IN (1,2)
                THEN 1 ELSE 0 END AS Closed FROM traftiemposrevision
            WHERE creferencia=@cref ORDER BY idtiemposrevision DESC LIMIT 1" + suffix, new { cref }, transaction);
        List<int> statuses = (await connection.QueryAsync<int>(@"
            SELECT COALESCE(status_rev,0) FROM fact WHERE cref_agrupada=@cref OR creferencia=@cref" + suffix,
            new { cref }, transaction)).ToList();
        return (part, snapshot, timer?.Closed == true || (statuses.Count > 0 && statuses.All(status => status == 1))
            ? "La revisión está finalizada. Solicita su desbloqueo antes de modificarla." : null);
    }

    private static async Task ValidateCatalogsAsync(MySqlConnection connection, MySqlTransaction transaction, ConfirmLabelCaptureRequest request)
    {
        if (request.Hazmat?.Clase is { } clase && await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM clase_hazmat WHERE clase_hazmat=@clase AND inactivo=0", new { clase = clase.Trim() }, transaction) == 0)
            throw new ArgumentException("La clase Hazmat no existe en el catálogo activo.");
        if (request.Fields.CPaisOrigen is { } country && await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM view_catalogo3 WHERE UPPER(Tabla)='PAISES' AND Codigo=@country", new { country = country.Trim().ToUpperInvariant() }, transaction) == 0)
            throw new ArgumentException("El país no existe en el catálogo.");
    }

    private static async Task ValidateColumnLengthsAsync(MySqlConnection connection, MySqlTransaction transaction, LabelCaptureSnapshot snapshot)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["numlote"] = snapshot.NumLote, ["numser"] = snapshot.NumSer, ["marca"] = snapshot.Marca,
            ["modelo"] = snapshot.Modelo, ["cpaisorigen"] = snapshot.CPaisOrigen
        };
        IEnumerable<ColumnLimit> limits = await connection.QueryAsync<ColumnLimit>(@"
            SELECT column_name AS ColumnName,character_maximum_length AS MaxLength FROM information_schema.columns
            WHERE table_schema=DATABASE() AND table_name='revdet' AND column_name IN @names", new { names = values.Keys.ToArray() }, transaction);
        foreach (ColumnLimit limit in limits)
            if (limit.MaxLength is { } length && values[limit.ColumnName]?.Length > length)
                throw new ArgumentException($"El dato {limit.ColumnName} excede los {length} caracteres permitidos. No se guardó la captura.");
    }

    private static async Task SaveHazmatAsync(MySqlConnection connection, MySqlTransaction transaction, string cref, LabelCaptureSnapshot snapshot)
    {
        var args = new { cref, un = snapshot.HazmatUn, clase = snapshot.HazmatClase, grupo = snapshot.HazmatGrupo };
        int exists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM traf3 WHERE creferencia=@cref", new { cref }, transaction);
        if (exists > 0)
            await connection.ExecuteAsync("UPDATE traf3 SET hazmat_un=@un,hazmat_clase=@clase,hazmat_grupo=@grupo WHERE creferencia=@cref", args, transaction);
        else
            await connection.ExecuteAsync(@"INSERT INTO traf3(fk_idtraf,creferencia,hazmat_un,hazmat_clase,hazmat_grupo)
                SELECT COALESCE((SELECT idtraf FROM traf WHERE creferencia=@cref LIMIT 1),0),@cref,@un,@clase,@grupo", args, transaction);
    }

    private async Task NotifyAsync(string cref, string cnofact, int id, string user, bool markedReviewed)
    {
        try
        {
            await activity.RegistrarAsync(cref, user, markedReviewed ? RevisionActivityTypes.PartidaRevisada : RevisionActivityTypes.CampoPartida,
                cnofact, id, markedReviewed ? "Captura de etiqueta confirmada y revisión física aceptada" : "Captura de etiqueta y evidencia guardadas");
            PartidaRevision? part = (await revision.GetPartidasEntradaAsync(cref)).FirstOrDefault(p => p.IdRevdet == id);
            if (part is not null)
                await hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync("onRevisionActualizada", new WarehouseHub.RevisionActualizadaEvent
                { Entidad = "PARTIDA", Creferencia = cref, Usuario = user, Fecha = DateTime.UtcNow.ToString("O"), Cnofact = cnofact, IdRevdet = id, Partida = part });
            await hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync("onRevisionActualizada", new WarehouseHub.RevisionActualizadaEvent
            { Entidad = "FOTOS", Creferencia = cref, Usuario = user, Fecha = DateTime.UtcNow.ToString("O") });
        }
        catch (Exception error) { logger.LogWarning(error, "La captura ya está confirmada; no se pudo publicar su notificación."); }
    }

    private static async Task AcquireLockAsync(MySqlConnection connection, string name, List<string> heldLocks)
    {
        if (await connection.ExecuteScalarAsync<int>("SELECT GET_LOCK(@name,10)", new { name }) != 1)
            throw new GuardFailure(503, "CAPTURE_BUSY", "La captura está ocupada. Reintenta el mismo envío en unos segundos.");
        heldLocks.Add(name);
    }

    private static byte[] DecodePhoto(ConfirmLabelCaptureRequest request)
    {
        string extension = PhotoFileNameResolver.NormalizeExtension(request.Extension);
        if (extension is not ("jpg" or "jpeg" or "png")) throw new ArgumentException("La evidencia debe ser JPG o PNG.");
        string encoded = request.PhotoBase64 ?? "";
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) encoded = encoded[(encoded.IndexOf(',') + 1)..];
        if (encoded.Length == 0 || encoded.Length > 22 * 1024 * 1024) throw new ArgumentException("La foto está vacía o excede el tamaño permitido.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new ArgumentException("La fotografía no contiene base64 válido."); }
        bool valid = extension == "png"
            ? bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            : bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[^2] == 255 && bytes[^1] == 217;
        if (!valid) throw new ArgumentException("El contenido de la foto no coincide con el formato declarado.");
        // El servidor se despliega en Windows (System.Drawing ya es dependencia del API).
        // Validar el encabezado no basta: cuatro bytes FF D8 FF D9 no son una foto.
        if (!OperatingSystem.IsWindows())
            throw new GuardFailure(503, "IMAGE_VALIDATION_UNAVAILABLE", "La validación de evidencia requiere el servidor Windows configurado.");
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using Image image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            if (image.Width < 64 || image.Height < 64 || image.Width > 20000 || image.Height > 20000 || (long)image.Width * image.Height > 100000000)
                throw new ArgumentException("La foto no tiene dimensiones válidas para una evidencia.");
        }
        catch (ArgumentException) { throw new ArgumentException("La imagen está dañada o no tiene dimensiones válidas para una evidencia."); }
        return bytes;
    }

    private static string CheckedDirectory(string root, string folder)
    {
        string directory = Path.GetFullPath(Path.Combine(root, folder));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new GuardFailure(409, "INVALID_PHOTO_FOLDER", "La carpeta del cliente no es válida para guardar la evidencia.");
        return directory;
    }

    private async Task RecoverUncommittedFileAsync(MySqlConnection connection, string path, string captureId, string cref)
    {
        if (!File.Exists(path)) return;
        RecoveryManifest? manifest = JsonSerializer.Deserialize<RecoveryManifest>(await File.ReadAllTextAsync(path), JsonOptions);
        if (manifest is null || manifest.CaptureId != captureId || manifest.Cref != cref)
            throw new GuardFailure(503, "RECOVERY_REQUIRED", "La captura requiere verificar su evidencia auxiliar antes de reintentar.");
        string directory = Path.GetDirectoryName(manifest.FinalPath) ?? "";
        string checkedDirectory = CheckedDirectory(basePath, Path.GetRelativePath(basePath, directory));
        string expectedPath = Path.Combine(checkedDirectory, PhotoFileNameResolver.Build(cref, 107, manifest.Line, manifest.Extension));
        if (!string.Equals(expectedPath, manifest.FinalPath, StringComparison.OrdinalIgnoreCase))
            throw new GuardFailure(503, "RECOVERY_REQUIRED", "La evidencia auxiliar requiere revisión.");
        long exists = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM fotos WHERE creferencia=@cref AND ntipodocto=107 AND nlineno=@line", new { cref, line = manifest.Line });
        if (exists == 0 && File.Exists(expectedPath))
        {
            await using FileStream stream = File.OpenRead(expectedPath);
            string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            await stream.DisposeAsync();
            if (actualHash == manifest.PhotoHash) File.Delete(expectedPath);
            else throw new GuardFailure(503, "RECOVERY_REQUIRED", "La imagen auxiliar cambió y requiere revisión antes de reintentar.");
        }
        DeleteQuietly(path);
    }

    private static async Task WriteDurableAsync(string path, byte[] bytes, FileMode mode, CancellationToken cancellationToken, Action? onCreated = null)
    {
        await using FileStream stream = new(path, mode, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        onCreated?.Invoke();
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private bool DeleteQuietly(string? path)
    {
        if (path is null) return true;
        try { File.Delete(path); return true; }
        catch (Exception error) { logger.LogWarning(error, "No se pudo limpiar un archivo auxiliar de captura."); return false; }
    }

    private static bool IsSchemaError(MySqlException error) => error.Number is 1146 or 1054;
    private static LabelCaptureResult Failure(GuardFailure error, int id, Guid? captureId = null) => new(error.StatusCode,
        new() { Code = error.Code, Message = error.Message, IdRevdet = id, CaptureId = captureId, Snapshot = error.Snapshot });
    private static LabelCaptureResult MigrationRequired(int id, Guid? captureId = null) => new(503,
        new() { Code = "MIGRATION_REQUIRED", IdRevdet = id, CaptureId = captureId,
            Message = "Falta una migración requerida para la captura segura. Conserva el borrador y solicita actualizar la base de datos." });
}
