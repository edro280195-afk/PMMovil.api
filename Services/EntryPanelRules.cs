using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Dapper;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public static class EntryPanelRules
{
    public const int DefaultPageSize = 50;
    public const int MinPageSize = 10;
    public const int MaxPageSize = 200;
    public const int MaxExportRows = 10000;
    public const int DefaultWindowDays = 45;
    public const int MinWindowDays = 7;
    public const int MaxWindowDays = 180;
    public const int MaxRangeDays = 400;
    public const int DefaultRangeDays = 7;
    public const int MaxListItems = 60;
    public const int MaxItemLength = 40;
    public const int MaxTextLength = 60;
    public const int ArrivalPhotoType = 100;
    public const int DamagePhotoType = 103;

    public const string ArrivalKind = "arrival";
    public const string DamageKind = "damage";

    public const string PendingArrivalPhotos = "arrival_photos";
    public const string PendingDamagePhotos = "damage_photos";
    public const string PendingDamageDescription = "damage_description";

    public const string ArrivalCount = "(SELECT COUNT(*) FROM fotos pa WHERE pa.creferencia = t.creferencia AND pa.ntipodocto = 100)";
    public const string DamageCount = "(SELECT COUNT(*) FROM fotos pd WHERE pd.creferencia = t.creferencia AND pd.ntipodocto = 103)";
    public const string DamagedFlag = "COALESCE((SELECT MAX(td.danada) FROM traf3 td WHERE td.creferencia = t.creferencia), 0)";
    public const string GateValue = "COALESCE((SELECT MAX(tg.ccasetaid) FROM traf3 tg WHERE tg.creferencia = t.creferencia), '')";

    public const string FromClause = @"
FROM traf t
LEFT JOIN clientes c ON c.cidcte = t.cclienteidtraf
LEFT JOIN transpor tr ON tr.cidtranspor = t.cidtransportraf
LEFT JOIN manejo m ON m.cidmanejo = t.cidmanejo";

    public const string RowColumns = $@"
    TRIM(t.creferencia) AS Reference,
    COALESCE(t.tipop, '') AS Type,
    t.fcreatrafico AS CreatedAt,
    TRIM(COALESCE(t.cclienteidtraf, '')) AS ClientId,
    TRIM(COALESCE(c.cnombre, '')) AS ClientName,
    TRIM(COALESCE(t.cidmanejo, '')) AS HandlingId,
    TRIM(COALESCE(m.cdescmanejo, '')) AS HandlingName,
    TRIM(COALESCE(t.cidtransportraf, '')) AS CarrierId,
    TRIM(COALESCE(tr.cnombre, '')) AS CarrierName,
    TRIM(COALESCE(t.cnoguia, '')) AS Waybill,
    TRIM(COALESCE(t.cnocaja, '')) AS Container,
    COALESCE(t.nbultosin, 0) AS Packages,
    TRIM(COALESCE(t.crecibio, '')) AS ReceivedBy,
    TRIM(COALESCE(t.usucreatrafico, '')) AS CreatedBy,
    {DamagedFlag} AS Damaged,
    COALESCE(t.lhazmat, 0) AS Hazmat,
    TRIM({GateValue}) AS GateId,
    CAST({ArrivalCount} AS SIGNED) AS ArrivalPhotos,
    CAST({DamageCount} AS SIGNED) AS DamagePhotos";

    public const string CoverColumns = @",
    (SELECT pc.idfotos FROM fotos pc WHERE pc.creferencia = t.creferencia AND pc.ntipodocto = 100 ORDER BY pc.nlineno ASC, pc.idfotos ASC LIMIT 1) AS CoverPhotoId,
    CAST(COALESCE((SELECT UNIX_TIMESTAMP(pu.fcrea) FROM fotos pu WHERE pu.creferencia = t.creferencia AND pu.ntipodocto = 100 ORDER BY pu.nlineno ASC, pu.idfotos ASC LIMIT 1), 0) AS SIGNED) AS CoverPhotoUnix";

    public static DateTime? ParseDay(string? value)
    {
        return DateTime.TryParseExact(
            (value ?? string.Empty).Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTime day)
            ? day.Date
            : null;
    }

    public static IReadOnlyList<string> SplitList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Length > MaxItemLength ? value[..MaxItemLength] : value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxListItems)
            .ToList();
    }

    public static EntryPanelFilter NormalizeFilter(
        DateTime today,
        string? from,
        string? to,
        string? clients,
        string? handlings,
        string? receivers,
        string? carriers,
        string? type,
        string? photos,
        bool damagedOnly,
        string? text,
        string? sort,
        int page,
        int pageSize)
    {
        DateTime end = ParseDay(to) ?? today.Date;
        DateTime start = ParseDay(from) ?? end.AddDays(-(DefaultRangeDays - 1));
        if (start > end) (start, end) = (end, start);
        if ((end - start).TotalDays >= MaxRangeDays) start = end.AddDays(-(MaxRangeDays - 1));

        string normalizedType = (type ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "D" => "D",
            "TODAS" or "ALL" => string.Empty,
            _ => "C"
        };
        string normalizedPhotos = (photos ?? string.Empty).Trim().ToLowerInvariant();
        string? normalizedText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (normalizedText is { Length: > MaxTextLength }) normalizedText = normalizedText[..MaxTextLength];

        return new EntryPanelFilter
        {
            From = start,
            ToExclusive = end.AddDays(1),
            Clients = SplitList(clients),
            Handlings = SplitList(handlings),
            Receivers = SplitList(receivers),
            Carriers = SplitList(carriers),
            Type = normalizedType,
            Photos = normalizedPhotos is "with" or "without" or "incomplete" ? normalizedPhotos : string.Empty,
            DamagedOnly = damagedOnly,
            Text = normalizedText,
            Sort = NormalizeSort(sort),
            Page = Math.Max(1, page),
            PageSize = pageSize <= 0 ? DefaultPageSize : Math.Clamp(pageSize, MinPageSize, MaxPageSize)
        };
    }

    public static string NormalizeSort(string? sort)
    {
        return (sort ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "date_asc" => "date_asc",
            "client" => "client",
            "packages_desc" => "packages_desc",
            "reference" => "reference",
            _ => "date_desc"
        };
    }

    public static string OrderByClause(string sort)
    {
        return NormalizeSort(sort) switch
        {
            "date_asc" => "t.fcreatrafico ASC, t.idtraf ASC",
            "client" => "c.cnombre ASC, t.fcreatrafico DESC",
            "packages_desc" => "t.nbultosin DESC, t.fcreatrafico DESC",
            "reference" => "t.creferencia ASC",
            _ => "t.fcreatrafico DESC, t.idtraf DESC"
        };
    }

    public static string EscapeLike(string value)
    {
        return value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }

    public static (string Sql, DynamicParameters Parameters) BuildWhere(EntryPanelFilter filter, string? exclude = null)
    {
        var clauses = new List<string>();
        var parameters = new DynamicParameters();

        if (filter.Type is "C" or "D")
        {
            clauses.Add("t.tipop = @type");
            parameters.Add("type", filter.Type);
        }
        else
        {
            clauses.Add("t.tipop IN ('C', 'D')");
        }

        clauses.Add("t.fcreatrafico >= @from AND t.fcreatrafico < @toExclusive");
        parameters.Add("from", filter.From);
        parameters.Add("toExclusive", filter.ToExclusive);

        AddList(clauses, parameters, "clients", "TRIM(t.cclienteidtraf)", filter.Clients, exclude);
        AddList(clauses, parameters, "handlings", "TRIM(t.cidmanejo)", filter.Handlings, exclude);
        AddList(clauses, parameters, "receivers", "TRIM(t.crecibio)", filter.Receivers, exclude);
        AddList(clauses, parameters, "carriers", "TRIM(t.cidtransportraf)", filter.Carriers, exclude);

        switch (filter.Photos)
        {
            case "with":
                clauses.Add($"{ArrivalCount} > 0");
                break;
            case "without":
                clauses.Add($"{ArrivalCount} = 0");
                break;
            case "incomplete":
                clauses.Add($"({ArrivalCount} = 0 OR ({DamagedFlag} = 1 AND {DamageCount} = 0))");
                break;
        }

        if (filter.DamagedOnly) clauses.Add($"{DamagedFlag} = 1");

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            clauses.Add("(t.creferencia LIKE @text OR t.cnocaja LIKE @text OR t.cnoguia LIKE @text OR c.cnombre LIKE @text)");
            parameters.Add("text", "%" + EscapeLike(filter.Text.Trim()) + "%");
        }

        return (string.Join("\n  AND ", clauses), parameters);
    }

    private static void AddList(
        List<string> clauses,
        DynamicParameters parameters,
        string name,
        string column,
        IReadOnlyList<string> values,
        string? exclude)
    {
        if (values.Count == 0 || string.Equals(exclude, name, StringComparison.Ordinal)) return;
        clauses.Add($"{column} IN @{name}");
        parameters.Add(name, values.ToList());
    }

    public static (DateTime From, DateTime ToExclusive) HistogramWindow(EntryPanelFilter filter, DateTime today, int windowDays)
    {
        int days = Math.Clamp(windowDays <= 0 ? DefaultWindowDays : windowDays, MinWindowDays, MaxWindowDays);
        DateTime end = filter.ToExclusive.AddDays(-1).Date;
        if (today.Date > end) end = today.Date;
        DateTime start = end.AddDays(-(days - 1));
        if (filter.From.Date < start) start = filter.From.Date;
        DateTime earliest = end.AddDays(-(MaxRangeDays - 1));
        if (start < earliest) start = earliest;
        return (start, end.AddDays(1));
    }

    public static bool IsIncomplete(int arrivalPhotos, bool damaged, int damagePhotos)
    {
        return arrivalPhotos == 0 || (damaged && damagePhotos == 0);
    }

    public static IReadOnlyList<EntryPending> PendingItems(int arrivalPhotos, bool damaged, int damagePhotos, string? damageDescription)
    {
        var items = new List<EntryPending>();
        if (arrivalPhotos == 0)
            items.Add(new EntryPending { Code = PendingArrivalPhotos, Label = "Faltan las fotos de arribo" });
        if (damaged && damagePhotos == 0)
            items.Add(new EntryPending { Code = PendingDamagePhotos, Label = "Falta documentar el daño con fotos" });
        if (damaged && string.IsNullOrWhiteSpace(damageDescription))
            items.Add(new EntryPending { Code = PendingDamageDescription, Label = "Falta describir el daño" });
        return items;
    }

    public static string KindFor(int docType)
    {
        return docType == DamagePhotoType ? DamageKind : ArrivalKind;
    }

    public static int DocTypeFor(string? kind)
    {
        return string.Equals(kind?.Trim(), DamageKind, StringComparison.OrdinalIgnoreCase) ? DamagePhotoType : ArrivalPhotoType;
    }

    public static string FormatPackages(decimal packages)
    {
        decimal rounded = Math.Round(packages, 3);
        string number = rounded == Math.Truncate(rounded)
            ? ((long)rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.###", CultureInfo.InvariantCulture);
        return rounded == 1 ? "1 bulto" : $"{number} bultos";
    }

    public static string ShortPending(string code)
    {
        return code switch
        {
            PendingArrivalPhotos => "fotos de arribo",
            PendingDamagePhotos => "fotos del daño",
            PendingDamageDescription => "descripción del daño",
            _ => code
        };
    }

    public static (string Title, string Body, string Severity) CapturedNotification(EntryRow row, IReadOnlyList<EntryPending> pending)
    {
        string client = string.IsNullOrWhiteSpace(row.ClientName) ? "Cliente sin nombre" : row.ClientName.Trim();
        if (client.Length > 38) client = client[..37].TrimEnd() + "…";
        string receivedBy = string.IsNullOrWhiteSpace(row.ReceivedBy) ? "sin responsable" : row.ReceivedBy.Trim();
        string summary = $"{client} · {FormatPackages(row.Packages)} · {receivedBy}";

        if (pending.Count == 0)
            return ("Entrada capturada", $"{summary}\nCompleta: no falta nada.", "info");

        string missing = string.Join(", ", pending.Select(item => ShortPending(item.Code)));
        string severity = row.Damaged ? "warning" : "info";
        return ("Entrada por completar", $"{summary}\nFalta: {missing}.", severity);
    }

    public static string EntryUrl(string reference)
    {
        return "/panel/entradas/" + Uri.EscapeDataString(reference.Trim());
    }

    public const string RowQuery = @"
SELECT" + RowColumns + CoverColumns + FromClause + @"
WHERE t.creferencia = @reference AND t.tipop IN ('C', 'D')
LIMIT 1";

    public static (string Sql, DynamicParameters Parameters) ListQuery(EntryPanelFilter filter)
    {
        (string where, DynamicParameters parameters) = BuildWhere(filter);
        parameters.Add("offset", (filter.Page - 1) * filter.PageSize);
        parameters.Add("limit", filter.PageSize);
        string sql = $@"
SELECT{RowColumns}{CoverColumns}{FromClause}
WHERE {where}
ORDER BY {OrderByClause(filter.Sort)}
LIMIT @offset, @limit";
        return (sql, parameters);
    }

    public static (string Sql, DynamicParameters Parameters) CountQuery(EntryPanelFilter filter)
    {
        (string where, DynamicParameters parameters) = BuildWhere(filter);
        string sql = $@"
SELECT COUNT(*) AS Total{FromClause}
WHERE {where}";
        return (sql, parameters);
    }

    public static (string Sql, DynamicParameters Parameters) ExportQuery(EntryPanelFilter filter)
    {
        (string where, DynamicParameters parameters) = BuildWhere(filter);
        parameters.Add("limit", MaxExportRows);
        string sql = $@"
SELECT{RowColumns}{FromClause}
WHERE {where}
ORDER BY {OrderByClause(filter.Sort)}
LIMIT @limit";
        return (sql, parameters);
    }

    public static (string Sql, DynamicParameters Parameters) KpiQuery(EntryPanelFilter filter)
    {
        (string where, DynamicParameters parameters) = BuildWhere(filter);
        string sql = $@"
SELECT COUNT(*) AS Total,
       CAST(COALESCE(SUM(t.nbultosin), 0) AS DECIMAL(18, 3)) AS Packages,
       CAST(COALESCE(SUM(CASE WHEN {DamagedFlag} = 1 THEN 1 ELSE 0 END), 0) AS SIGNED) AS Damaged,
       CAST(COALESCE(SUM(CASE WHEN {ArrivalCount} = 0 THEN 1 ELSE 0 END), 0) AS SIGNED) AS WithoutPhotos,
       CAST(COALESCE(SUM(CASE WHEN {ArrivalCount} = 0 OR ({DamagedFlag} = 1 AND {DamageCount} = 0) THEN 1 ELSE 0 END), 0) AS SIGNED) AS Incomplete{FromClause}
WHERE {where}";
        return (sql, parameters);
    }

    public static (string Sql, DynamicParameters Parameters) HistogramQuery(EntryPanelFilter filter)
    {
        (string where, DynamicParameters parameters) = BuildWhere(filter);
        string sql = $@"
SELECT DATE(t.fcreatrafico) AS Day,
       COUNT(*) AS Total,
       CAST(SUM(CASE WHEN {ArrivalCount} = 0 THEN 1 ELSE 0 END) AS SIGNED) AS WithoutPhotos,
       CAST(SUM(CASE WHEN {DamagedFlag} = 1 THEN 1 ELSE 0 END) AS SIGNED) AS Damaged{FromClause}
WHERE {where}
GROUP BY DATE(t.fcreatrafico)
ORDER BY Day ASC";
        return (sql, parameters);
    }

    public static readonly string[] FacetNames = { "clients", "handlings", "receivers", "carriers" };

    public static (string Sql, DynamicParameters Parameters) FacetQuery(EntryPanelFilter filter, string facet)
    {
        (string id, string name, string? extraGroup) = facet switch
        {
            "clients" => ("TRIM(t.cclienteidtraf)", "COALESCE(NULLIF(TRIM(c.cnombre), ''), TRIM(t.cclienteidtraf))", "TRIM(c.cnombre)"),
            "handlings" => ("TRIM(t.cidmanejo)", "COALESCE(NULLIF(TRIM(m.cdescmanejo), ''), TRIM(t.cidmanejo))", "TRIM(m.cdescmanejo)"),
            "receivers" => ("TRIM(t.crecibio)", "TRIM(t.crecibio)", null),
            "carriers" => ("TRIM(t.cidtransportraf)", "COALESCE(NULLIF(TRIM(tr.cnombre), ''), TRIM(t.cidtransportraf))", "TRIM(tr.cnombre)"),
            _ => throw new ArgumentException("Faceta desconocida.", nameof(facet))
        };
        (string where, DynamicParameters parameters) = BuildWhere(filter, exclude: facet);
        string groupBy = extraGroup is null ? id : $"{id}, {extraGroup}";
        string sql = $@"
SELECT {id} AS Id, {name} AS Name, COUNT(*) AS `Count`{FromClause}
WHERE {where}
GROUP BY {groupBy}
HAVING Id <> ''
ORDER BY `Count` DESC, Name ASC
LIMIT {FacetLimit}";
        return (sql, parameters);
    }

    public const int FacetLimit = 300;
}
