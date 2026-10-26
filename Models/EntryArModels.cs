using System.Collections.Generic;

namespace PMMovil.Api.Models;

public sealed class EntryArBatchRequest
{
    public List<string> References { get; set; } = new();
}

public sealed class EntryArCardDto
{
    public string Reference { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string? ArrivalDate { get; set; }
    public string ReviewStatus { get; set; } = "SIN_REVISAR";
    public int TotalInvoices { get; set; }
    public int CompletedInvoices { get; set; }
    public int TotalLines { get; set; }
    public int ReviewedLines { get; set; }
    public bool HasHazmat { get; set; }
    public bool HasDamage { get; set; }
    public string DamageDescription { get; set; } = string.Empty;
}
