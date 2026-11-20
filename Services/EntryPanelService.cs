using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dapper;

using MySqlConnector;

using PMMovil.Api.Attributes;
using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public sealed class EntryPhotoTarget
{
    public long Id { get; set; }
    public int DocType { get; set; }
    public int Line { get; set; }
    public string? Extension { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public long CreatedUnix { get; set; }
}

public sealed class EntryPanelService
{
    private const int LogLimit = 200;

    private readonly BaseRepository _db;
    private readonly ILogger<EntryPanelService> _logger;

    public EntryPanelService(BaseRepository db, ILogger<EntryPanelService> logger)
    {
        _db = db;
        _logger = logger;
    }

    private sealed class CountRow
    {
        public long Total { get; set; }
    }

    private sealed class KpiRow
    {
        public long Total { get; set; }
        public decimal Packages { get; set; }
        public long Damaged { get; set; }
        public long WithoutPhotos { get; set; }
        public long Incomplete { get; set; }
    }

    private sealed class DayRow
    {
        public DateTime Day { get; set; }
        public long Total { get; set; }
        public long WithoutPhotos { get; set; }
        public long Damaged { get; set; }
    }

    private sealed class DetailHeaderRow
    {
        public string Observations { get; set; } = string.Empty;
        public string Marks1 { get; set; } = string.Empty;
        public string Marks2 { get; set; } = string.Empty;
        public string Warehouse { get; set; } = string.Empty;
        public string Plant { get; set; } = string.Empty;
        public bool Bonded { get; set; }
        public string BondedNumber { get; set; } = string.Empty;
        public DateTime? ArrivalTime { get; set; }
        public string DamageDescription { get; set; } = string.Empty;
        public int WoodSealed { get; set; }
        public int WoodUnsealed { get; set; }
        public int Sawdust { get; set; }
        public int NoSeal { get; set; }
    }

    private sealed class PhotoRow
    {
        public long Id { get; set; }
        public int DocType { get; set; }
        public int Line { get; set; }
        public string? Extension { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public long CreatedUnix { get; set; }
    }

    private sealed class LogRow
    {
        public DateTime At { get; set; }
        public string Action { get; set; } = string.Empty;
        public string User { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public long? PhotoId { get; set; }
        public int DocType { get; set; }
        public int Line { get; set; }
        public string? Detail { get; set; }
    }

    private static CommandDefinition Command(string sql, object? parameters, CancellationToken ct)
    {
        return new CommandDefinition(sql, parameters, cancellationToken: ct);
    }

    private static EntryRow Finish(EntryRow row)
    {
        row.Incomplete = EntryPanelRules.IsIncomplete(row.ArrivalPhotos, row.Damaged, row.DamagePhotos);
        row.CoverPhotoVersion = row.CoverPhotoId is { } coverId ? FotoCache.Version(coverId, row.CoverPhotoUnix) : string.Empty;
        return row;
    }

    public async Task<EntryListResult> ListAsync(EntryPanelFilter filter, CancellationToken ct)
    {
        (string listSql, DynamicParameters listParameters) = EntryPanelRules.ListQuery(filter);
        (string countSql, DynamicParameters countParameters) = EntryPanelRules.CountQuery(filter);

        using IDbConnection listConnection = _db.CreateConnection();
        using IDbConnection countConnection = _db.CreateConnection();
        Task<IEnumerable<EntryRow>> rows = listConnection.QueryAsync<EntryRow>(Command(listSql, listParameters, ct));
        Task<CountRow?> total = countConnection.QueryFirstOrDefaultAsync<CountRow>(Command(countSql, countParameters, ct));
        await Task.WhenAll(rows, total);

        return new EntryListResult
        {
            Total = total.Result?.Total ?? 0,
            Page = filter.Page,
            PageSize = filter.PageSize,
            Items = rows.Result.Select(Finish).ToList()
        };
    }

    public async Task<IReadOnlyList<EntryRow>> ExportRowsAsync(EntryPanelFilter filter, CancellationToken ct)
    {
        (string sql, DynamicParameters parameters) = EntryPanelRules.ExportQuery(filter);
        using IDbConnection connection = _db.CreateConnection();
        IEnumerable<EntryRow> rows = await connection.QueryAsync<EntryRow>(Command(sql, parameters, ct));
        return rows.Select(Finish).ToList();
    }

    public async Task<EntrySummaryResult> SummaryAsync(EntryPanelFilter filter, DateTime today, int windowDays, CancellationToken ct)
    {
        (DateTime windowFrom, DateTime windowToExclusive) = EntryPanelRules.HistogramWindow(filter, today, windowDays);
        EntryPanelFilter windowFilter = CopyWithRange(filter, windowFrom, windowToExclusive);

        Task<EntryKpis> kpis = KpisAsync(filter, ct);
        Task<IReadOnlyList<EntryHistogramDay>> histogram = HistogramAsync(windowFilter, ct);
        Task<IReadOnlyList<EntryFacetOption>> clients = FacetAsync(filter, "clients", ct);
        Task<IReadOnlyList<EntryFacetOption>> handlings = FacetAsync(filter, "handlings", ct);
        Task<IReadOnlyList<EntryFacetOption>> receivers = FacetAsync(filter, "receivers", ct);
        Task<IReadOnlyList<EntryFacetOption>> carriers = FacetAsync(filter, "carriers", ct);
        await Task.WhenAll(kpis, histogram, clients, handlings, receivers, carriers);

        return new EntrySummaryResult
        {
            WindowFrom = windowFrom.ToString("yyyy-MM-dd"),
            WindowTo = windowToExclusive.AddDays(-1).ToString("yyyy-MM-dd"),
            Kpis = kpis.Result,
            Histogram = histogram.Result,
            Facets = new EntryFacets
            {
                Clients = clients.Result,
                Handlings = handlings.Result,
                Receivers = receivers.Result,
                Carriers = carriers.Result
            },
            GeneratedAt = DateTime.Now
        };
    }

    private static EntryPanelFilter CopyWithRange(EntryPanelFilter source, DateTime from, DateTime toExclusive)
    {
        return new EntryPanelFilter
        {
            From = from,
            ToExclusive = toExclusive,
            Clients = source.Clients,
            Handlings = source.Handlings,
            Receivers = source.Receivers,
            Carriers = source.Carriers,
            Type = source.Type,
            Photos = source.Photos,
            DamagedOnly = source.DamagedOnly,
            Text = source.Text,
            Sort = source.Sort,
            Page = 1,
            PageSize = source.PageSize
        };
    }

    private async Task<EntryKpis> KpisAsync(EntryPanelFilter filter, CancellationToken ct)
    {
        (string sql, DynamicParameters parameters) = EntryPanelRules.KpiQuery(filter);
        using IDbConnection connection = _db.CreateConnection();
        KpiRow? row = await connection.QueryFirstOrDefaultAsync<KpiRow>(Command(sql, parameters, ct));
        return new EntryKpis
        {
            Total = row?.Total ?? 0,
            Packages = row?.Packages ?? 0,
            Damaged = row?.Damaged ?? 0,
            WithoutPhotos = row?.WithoutPhotos ?? 0,
            Incomplete = row?.Incomplete ?? 0
        };
    }

    private async Task<IReadOnlyList<EntryHistogramDay>> HistogramAsync(EntryPanelFilter filter, CancellationToken ct)
    {
        (string sql, DynamicParameters parameters) = EntryPanelRules.HistogramQuery(filter);
        using IDbConnection connection = _db.CreateConnection();
        IEnumerable<DayRow> rows = await connection.QueryAsync<DayRow>(Command(sql, parameters, ct));
        return rows.Select(row => new EntryHistogramDay
        {
            Day = row.Day.ToString("yyyy-MM-dd"),
            Total = row.Total,
            WithoutPhotos = row.WithoutPhotos,
            Damaged = row.Damaged
        }).ToList();
    }

    private async Task<IReadOnlyList<EntryFacetOption>> FacetAsync(EntryPanelFilter filter, string facet, CancellationToken ct)
    {
        (string sql, DynamicParameters parameters) = EntryPanelRules.FacetQuery(filter, facet);
        using IDbConnection connection = _db.CreateConnection();
        IEnumerable<EntryFacetOption> rows = await connection.QueryAsync<EntryFacetOption>(Command(sql, parameters, ct));
        return rows.ToList();
    }

    public async Task<EntryRow?> GetRowAsync(string reference, CancellationToken ct)
    {
        using IDbConnection connection = _db.CreateConnection();
        EntryRow? row = await connection.QueryFirstOrDefaultAsync<EntryRow>(Command(EntryPanelRules.RowQuery, new { reference = reference.Trim() }, ct));
        return row is null ? null : Finish(row);
    }

    public async Task<EntryDetailResult?> GetDetailAsync(string reference, CancellationToken ct)
    {
        EntryRow? row = await GetRowAsync(reference, ct);
        if (row is null) return null;

        const string headerSql = @"
SELECT COALESCE(t.mobserv, '') AS Observations,
       TRIM(COALESCE(t.cmarcas1, '')) AS Marks1,
       TRIM(COALESCE(t.cmarcas2, '')) AS Marks2,
       TRIM(COALESCE(t.bodega, '')) AS Warehouse,
       TRIM(COALESCE(t.planta, '')) AS Plant,
       COALESCE(t.linbond, 0) AS Bonded,
       TRIM(COALESCE(t.cinbondno, '')) AS BondedNumber,
       t.thoraarribo AS ArrivalTime,
       COALESCE(t3.danada_descripcion, '') AS DamageDescription,
       CAST(COALESCE(t3.madera_c_sello, 0) AS SIGNED) AS WoodSealed,
       CAST(COALESCE(t3.madera_s_sello, 0) AS SIGNED) AS WoodUnsealed,
       CAST(COALESCE(t3.aserrin, 0) AS SIGNED) AS Sawdust,
       CAST(COALESCE(t3.nosello, 0) AS SIGNED) AS NoSeal
FROM traf t
LEFT JOIN traf3 t3 ON t3.idtraf3 = (SELECT MAX(x.idtraf3) FROM traf3 x WHERE x.creferencia = t.creferencia)
WHERE t.creferencia = @reference
LIMIT 1";
        const string linesSql = @"
SELECT d.nlineno AS Line,
       TRIM(COALESCE(d.cclase, '')) AS Class,
       TRIM(COALESCE(cl.cdescribe, '')) AS ClassName,
       COALESCE(d.ncantidad, 0) AS Quantity,
       TRIM(COALESCE(d.cdescripcion, '')) AS Description,
       TRIM(COALESCE(d.cobservaciones, '')) AS Bundles,
       TRIM(COALESCE(d.ubicacion, '')) AS Location
FROM trafdet d
LEFT JOIN clases cl ON cl.cclaseid = d.cclase
WHERE d.creferencia = @reference
ORDER BY d.nlineno ASC";
        const string photosSql = @"
SELECT f.idfotos AS Id,
       f.ntipodocto AS DocType,
       f.nlineno AS Line,
       f.cextension AS Extension,
       TRIM(COALESCE(f.usucrea, '')) AS CreatedBy,
       f.fcrea AS CreatedAt,
       CAST(IFNULL(UNIX_TIMESTAMP(f.fcrea), 0) AS SIGNED) AS CreatedUnix
FROM fotos f
WHERE f.creferencia = @reference AND f.ntipodocto IN (100, 103)
ORDER BY f.ntipodocto ASC, f.nlineno ASC, f.idfotos ASC";

        var args = new { reference = reference.Trim() };
        using IDbConnection headerConnection = _db.CreateConnection();
        using IDbConnection linesConnection = _db.CreateConnection();
        using IDbConnection photosConnection = _db.CreateConnection();
        Task<DetailHeaderRow?> header = headerConnection.QueryFirstOrDefaultAsync<DetailHeaderRow>(Command(headerSql, args, ct));
        Task<IEnumerable<EntryLine>> lines = linesConnection.QueryAsync<EntryLine>(Command(linesSql, args, ct));
        Task<IEnumerable<PhotoRow>> photos = photosConnection.QueryAsync<PhotoRow>(Command(photosSql, args, ct));
        Task<(IReadOnlyList<LogRow> Items, bool Available)> log = ReadLogAsync(reference.Trim(), ct);
        await Task.WhenAll(header, lines, photos, log);

        DetailHeaderRow headerRow = header.Result ?? new DetailHeaderRow();
        string observations = headerRow.Observations;
        int pipe = observations.IndexOf(" | ", StringComparison.Ordinal);
        if (pipe >= 0) observations = observations[..pipe];

        List<EntryPhoto> photoList = photos.Result.Select(photo => new EntryPhoto
        {
            Id = photo.Id,
            Kind = EntryPanelRules.KindFor(photo.DocType),
            Line = photo.Line,
            Extension = (photo.Extension ?? string.Empty).Trim().TrimStart('.'),
            CreatedBy = photo.CreatedBy ?? string.Empty,
            CreatedAt = photo.CreatedAt,
            Version = FotoCache.Version(photo.Id, photo.CreatedUnix)
        }).ToList();

        var logItems = BuildLog(row, photoList, log.Result.Items);

        return new EntryDetailResult
        {
            Entry = row,
            Header = new EntryDetailHeader
            {
                Observations = observations.Trim(),
                Marks1 = headerRow.Marks1,
                Marks2 = headerRow.Marks2,
                Warehouse = headerRow.Warehouse,
                Plant = headerRow.Plant,
                Bonded = headerRow.Bonded,
                BondedNumber = headerRow.BondedNumber,
                DamageDescription = headerRow.DamageDescription.Trim(),
                WoodSealed = headerRow.WoodSealed,
                WoodUnsealed = headerRow.WoodUnsealed,
                Sawdust = headerRow.Sawdust,
                NoSeal = headerRow.NoSeal,
                ArrivalTime = headerRow.ArrivalTime
            },
            Lines = lines.Result.ToList(),
            Photos = photoList,
            Pending = EntryPanelRules.PendingItems(row.ArrivalPhotos, row.Damaged, row.DamagePhotos, headerRow.DamageDescription),
            Log = logItems,
            LogAvailable = log.Result.Available
        };
    }

    private static IReadOnlyList<EntryLogItem> BuildLog(EntryRow row, IReadOnlyList<EntryPhoto> photos, IReadOnlyList<LogRow> logged)
    {
        var items = new List<EntryLogItem>();
        if (row.CreatedAt is { } createdAt)
        {
            items.Add(new EntryLogItem
            {
                At = createdAt,
                Action = "captured",
                User = string.IsNullOrWhiteSpace(row.ReceivedBy) ? row.CreatedBy : row.ReceivedBy,
                Source = "app",
                Detail = "Entrada capturada"
            });
        }

        var addedInLog = new HashSet<long>(logged.Where(item => item.PhotoId.HasValue && item.Action == "AGREGAR").Select(item => item.PhotoId!.Value));
        foreach (EntryPhoto photo in photos)
        {
            if (addedInLog.Contains(photo.Id) || photo.CreatedAt is not { } at) continue;
            items.Add(new EntryLogItem
            {
                At = at,
                Action = "photo_added",
                User = photo.CreatedBy,
                Source = "app",
                PhotoId = photo.Id,
                Kind = photo.Kind,
                Line = photo.Line,
                Detail = photo.Kind == EntryPanelRules.DamageKind ? "Foto de daño agregada" : "Foto de arribo agregada"
            });
        }

        foreach (LogRow item in logged)
        {
            bool added = item.Action == "AGREGAR";
            items.Add(new EntryLogItem
            {
                At = item.At,
                Action = added ? "photo_added" : "photo_removed",
                User = item.User,
                Source = item.Source == "PANEL" ? "panel" : "app",
                PhotoId = item.PhotoId,
                Kind = EntryPanelRules.KindFor(item.DocType),
                Line = item.Line,
                Detail = string.IsNullOrWhiteSpace(item.Detail)
                    ? (added ? "Foto agregada" : "Foto eliminada")
                    : item.Detail!
            });
        }

        return items.OrderByDescending(item => item.At).ToList();
    }

    private async Task<(IReadOnlyList<LogRow> Items, bool Available)> ReadLogAsync(string reference, CancellationToken ct)
    {
        const string sql = @"
SELECT fecha AS At, accion AS Action, usuario AS User, origen AS Source, idfotos AS PhotoId,
       ntipodocto AS DocType, nlineno AS Line, detalle AS Detail
FROM entrada_fotos_bitacora
WHERE creferencia = @reference
ORDER BY id DESC
LIMIT 200";
        try
        {
            using IDbConnection connection = _db.CreateConnection();
            IEnumerable<LogRow> rows = await connection.QueryAsync<LogRow>(Command(sql, new { reference }, ct));
            return (rows.ToList(), true);
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.NoSuchTable)
        {
            return (Array.Empty<LogRow>(), false);
        }
    }

    public async Task<EntryPhotoTarget?> FindPhotoAsync(string reference, long photoId, CancellationToken ct)
    {
        const string sql = @"
SELECT f.idfotos AS Id,
       f.ntipodocto AS DocType,
       f.nlineno AS Line,
       f.cextension AS Extension,
       TRIM(f.creferencia) AS Reference,
       COALESCE(NULLIF(TRIM(cl.carpeta), ''), TRIM(cl.cidcte)) AS Folder,
       CAST(IFNULL(UNIX_TIMESTAMP(f.fcrea), 0) AS SIGNED) AS CreatedUnix
FROM fotos f
JOIN traf t ON t.creferencia = f.creferencia
JOIN clientes cl ON cl.cidcte = t.cclienteidtraf
WHERE f.idfotos = @photoId AND f.creferencia = @reference AND f.ntipodocto IN (100, 103)
LIMIT 1";
        using IDbConnection connection = _db.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<EntryPhotoTarget>(
            Command(sql, new { photoId, reference = reference.Trim() }, ct));
    }

    public async Task<bool> EntryExistsAsync(string reference, CancellationToken ct)
    {
        const string sql = "SELECT COUNT(*) FROM traf WHERE creferencia = @reference AND tipop IN ('C', 'D')";
        using IDbConnection connection = _db.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(Command(sql, new { reference = reference.Trim() }, ct)) > 0;
    }

    public async Task<long> MaxPhotoIdAsync(CancellationToken ct)
    {
        using IDbConnection connection = _db.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(Command("SELECT COALESCE(MAX(idfotos), 0) FROM fotos", null, ct));
    }

    public async Task<IReadOnlyList<EntryPhoto>> PhotosAddedAfterAsync(string reference, int docType, long afterId, CancellationToken ct)
    {
        const string sql = @"
SELECT f.idfotos AS Id, f.ntipodocto AS DocType, f.nlineno AS Line, f.cextension AS Extension,
       TRIM(COALESCE(f.usucrea, '')) AS CreatedBy, f.fcrea AS CreatedAt,
       CAST(IFNULL(UNIX_TIMESTAMP(f.fcrea), 0) AS SIGNED) AS CreatedUnix
FROM fotos f
WHERE f.creferencia = @reference AND f.ntipodocto = @docType AND f.idfotos > @afterId
ORDER BY f.idfotos ASC";
        using IDbConnection connection = _db.CreateConnection();
        IEnumerable<PhotoRow> rows = await connection.QueryAsync<PhotoRow>(
            Command(sql, new { reference = reference.Trim(), docType, afterId }, ct));
        return rows.Select(photo => new EntryPhoto
        {
            Id = photo.Id,
            Kind = EntryPanelRules.KindFor(photo.DocType),
            Line = photo.Line,
            Extension = (photo.Extension ?? string.Empty).Trim().TrimStart('.'),
            CreatedBy = photo.CreatedBy ?? string.Empty,
            CreatedAt = photo.CreatedAt,
            Version = FotoCache.Version(photo.Id, photo.CreatedUnix)
        }).ToList();
    }

    public async Task<(int Arrival, int Damage)> CountPhotosAsync(string reference, CancellationToken ct)
    {
        const string sql = @"
SELECT CAST(COALESCE(SUM(ntipodocto = 100), 0) AS SIGNED) AS Arrival,
       CAST(COALESCE(SUM(ntipodocto = 103), 0) AS SIGNED) AS Damage
FROM fotos
WHERE creferencia = @reference AND ntipodocto IN (100, 103)";
        using IDbConnection connection = _db.CreateConnection();
        PhotoCounts? counts = await connection.QueryFirstOrDefaultAsync<PhotoCounts>(Command(sql, new { reference = reference.Trim() }, ct));
        return (counts?.Arrival ?? 0, counts?.Damage ?? 0);
    }

    private sealed class PhotoCounts
    {
        public int Arrival { get; set; }
        public int Damage { get; set; }
    }

    public async Task WriteLogAsync(
        string reference,
        string action,
        long? photoId,
        int docType,
        int line,
        string? extension,
        string user,
        string source,
        string? detail,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO entrada_fotos_bitacora
    (creferencia, accion, idfotos, ntipodocto, nlineno, cextension, usuario, origen, detalle, fecha)
VALUES
    (@reference, @action, @photoId, @docType, @line, @extension, @user, @source, @detail, NOW())";
        using IDbConnection connection = _db.CreateConnection();
        await connection.ExecuteAsync(Command(sql, new
        {
            reference = reference.Trim(),
            action,
            photoId,
            docType,
            line,
            extension = (extension ?? string.Empty).Trim().TrimStart('.'),
            user = Limit(user, 30),
            source,
            detail = Limit(detail, 200)
        }, ct));
    }

    public async Task TryWriteLogAsync(
        string reference,
        string action,
        long? photoId,
        int docType,
        int line,
        string? extension,
        string user,
        string source,
        string? detail)
    {
        try
        {
            await WriteLogAsync(reference, action, photoId, docType, line, extension, user, source, detail, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No se pudo registrar en la bitácora de fotos {Action} de {Reference}.", action, reference);
        }
    }

    public async Task<IReadOnlyList<string>> CompletionRecipientsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT DISTINCT TRIM(u.claveusuario) AS Username
FROM accesos a
JOIN modulos mo ON mo.ModuloID = a.Fk_ModuloID AND COALESCE(mo.Inactivo, 0) = 0
JOIN usuarios u ON u.usuarioid = a.Fk_UsuarioID AND u.inactivo = 0
WHERE a.Fk_ModuloID = @moduleId AND a.Agregar = 1";
        using IDbConnection connection = _db.CreateConnection();
        IEnumerable<string> names = await connection.QueryAsync<string>(
            Command(sql, new { moduleId = MobileModuleIds.EntriesPanel }, ct));
        return names.Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
    }

    private static bool _logTableKnown;

    public async Task<bool> LogTableExistsAsync(CancellationToken ct)
    {
        if (_logTableKnown) return true;
        const string sql = @"
SELECT COUNT(*)
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'entrada_fotos_bitacora'";
        using IDbConnection connection = _db.CreateConnection();
        bool exists = await connection.ExecuteScalarAsync<int>(Command(sql, null, ct)) > 0;
        if (exists) _logTableKnown = true;
        return exists;
    }

    public async Task<int> DeletePhotoRowAsync(string reference, long photoId, CancellationToken ct)
    {
        const string sql = "DELETE FROM fotos WHERE idfotos = @photoId AND creferencia = @reference AND ntipodocto IN (100, 103)";
        using IDbConnection connection = _db.CreateConnection();
        return await connection.ExecuteAsync(Command(sql, new { photoId, reference = reference.Trim() }, ct));
    }

    public Task AdjustDamageTrackingAsync(string reference)
    {
        return EntradaDanioSeguimiento.AjustarAlBorrarFotoAsync(_db, _logger, reference);
    }

    private static string Limit(string? value, int length)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= length ? text : text[..length];
    }
}
