using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PMMovil.Api.Models;

public sealed class EntryPanelFilter
{
    public DateTime From { get; set; }
    public DateTime ToExclusive { get; set; }
    public IReadOnlyList<string> Clients { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Handlings { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Receivers { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Carriers { get; set; } = Array.Empty<string>();
    public string Type { get; set; } = string.Empty;
    public string Photos { get; set; } = string.Empty;
    public bool DamagedOnly { get; set; }
    public string? Text { get; set; }
    public string Sort { get; set; } = "date_desc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed class EntryRow
{
    public string Reference { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public DateTime? CreatedAt { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string HandlingId { get; set; } = string.Empty;
    public string HandlingName { get; set; } = string.Empty;
    public string CarrierId { get; set; } = string.Empty;
    public string CarrierName { get; set; } = string.Empty;
    public string Waybill { get; set; } = string.Empty;
    public string Container { get; set; } = string.Empty;
    public decimal Packages { get; set; }
    public string ReceivedBy { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public bool Damaged { get; set; }
    public bool Hazmat { get; set; }
    public string GateId { get; set; } = string.Empty;
    public int ArrivalPhotos { get; set; }
    public int DamagePhotos { get; set; }
    public bool Incomplete { get; set; }
    public long? CoverPhotoId { get; set; }
    public string CoverPhotoVersion { get; set; } = string.Empty;

    [JsonIgnore]
    public long CoverPhotoUnix { get; set; }
}

public sealed class EntryListResult
{
    public bool Success { get; set; } = true;
    public long Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public IReadOnlyList<EntryRow> Items { get; set; } = Array.Empty<EntryRow>();
}

public sealed class EntryKpis
{
    public long Total { get; set; }
    public decimal Packages { get; set; }
    public long Damaged { get; set; }
    public long WithoutPhotos { get; set; }
    public long Incomplete { get; set; }
}

public sealed class EntryHistogramDay
{
    public string Day { get; set; } = string.Empty;
    public long Total { get; set; }
    public long WithoutPhotos { get; set; }
    public long Damaged { get; set; }
}

public sealed class EntryFacetOption
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Count { get; set; }
}

public sealed class EntryFacets
{
    public IReadOnlyList<EntryFacetOption> Clients { get; set; } = Array.Empty<EntryFacetOption>();
    public IReadOnlyList<EntryFacetOption> Handlings { get; set; } = Array.Empty<EntryFacetOption>();
    public IReadOnlyList<EntryFacetOption> Receivers { get; set; } = Array.Empty<EntryFacetOption>();
    public IReadOnlyList<EntryFacetOption> Carriers { get; set; } = Array.Empty<EntryFacetOption>();
}

public sealed class EntrySummaryResult
{
    public bool Success { get; set; } = true;
    public string WindowFrom { get; set; } = string.Empty;
    public string WindowTo { get; set; } = string.Empty;
    public EntryKpis Kpis { get; set; } = new();
    public IReadOnlyList<EntryHistogramDay> Histogram { get; set; } = Array.Empty<EntryHistogramDay>();
    public EntryFacets Facets { get; set; } = new();
    public DateTime GeneratedAt { get; set; }
}

public sealed class EntryDetailHeader
{
    public string Observations { get; set; } = string.Empty;
    public string Marks1 { get; set; } = string.Empty;
    public string Marks2 { get; set; } = string.Empty;
    public string Warehouse { get; set; } = string.Empty;
    public string Plant { get; set; } = string.Empty;
    public bool Bonded { get; set; }
    public string BondedNumber { get; set; } = string.Empty;
    public string DamageDescription { get; set; } = string.Empty;
    public int WoodSealed { get; set; }
    public int WoodUnsealed { get; set; }
    public int Sawdust { get; set; }
    public int NoSeal { get; set; }
    public DateTime? ArrivalTime { get; set; }
}

public sealed class EntryLine
{
    public int Line { get; set; }
    public string Class { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Bundles { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
}

public sealed class EntryPhoto
{
    public long Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int Line { get; set; }
    public string Extension { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? CreatedAt { get; set; }
    public string Version { get; set; } = string.Empty;
}

public sealed class EntryPending
{
    public string Code { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed class EntryLogItem
{
    public DateTime At { get; set; }
    public string Action { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public long? PhotoId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int Line { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class EntryDetailResult
{
    public bool Success { get; set; } = true;
    public EntryRow Entry { get; set; } = new();
    public EntryDetailHeader Header { get; set; } = new();
    public IReadOnlyList<EntryLine> Lines { get; set; } = Array.Empty<EntryLine>();
    public IReadOnlyList<EntryPhoto> Photos { get; set; } = Array.Empty<EntryPhoto>();
    public IReadOnlyList<EntryPending> Pending { get; set; } = Array.Empty<EntryPending>();
    public IReadOnlyList<EntryLogItem> Log { get; set; } = Array.Empty<EntryLogItem>();
    public bool LogAvailable { get; set; } = true;
}

public sealed class EntryPhotoUploadResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public IReadOnlyList<EntryPhoto> Photos { get; set; } = Array.Empty<EntryPhoto>();
    public int ArrivalPhotos { get; set; }
    public int DamagePhotos { get; set; }
}

public sealed class EntryRealtimeEvent
{
    public string Reference { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string At { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public EntryRow? Entry { get; set; }
}

public sealed class EntryPhotosEvent
{
    public string Reference { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string At { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public int Count { get; set; }
    public int ArrivalPhotos { get; set; }
    public int DamagePhotos { get; set; }
}
