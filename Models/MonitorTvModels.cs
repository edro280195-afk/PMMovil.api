using System;
using System.Collections.Generic;

namespace PMMovil.Api.Models;

public sealed class MonitorTvLinkItem
{
    public long Id { get; init; }
    public string Nombre { get; init; } = string.Empty;
    public string TipoPantalla { get; init; } = "OPERACION_NLD";
    public string TokenHint { get; init; } = string.Empty;
    public bool CanCopy { get; init; }
    public string UsuarioCrea { get; init; } = string.Empty;
    public DateTime FechaCreacion { get; init; }
    public DateTime? FechaExpiracion { get; init; }
    public bool Revocado { get; init; }
}

public sealed class CrearMonitorTvLinkRequest
{
    public string? Nombre { get; init; }
    public string? TipoPantalla { get; init; }
    /// <summary>0 significa sin fecha de expiración; el enlace se revoca manualmente.</summary>
    public int ExpiraEnDias { get; init; }
}

public sealed class CrearMonitorTvLinkResult
{
    public long Id { get; init; }
    public string Token { get; init; } = string.Empty;
    public string TipoPantalla { get; init; } = "OPERACION_NLD";
    public string TokenHint { get; init; } = string.Empty;
    public string Nombre { get; init; } = string.Empty;
    public DateTime? FechaExpiracion { get; init; }
}

public sealed class MonitorTvRevisionRow
{
    public string Creferencia { get; init; } = string.Empty;
    public string? Cliente { get; init; }
    public DateTime Arribo { get; init; }
    public DateTime? FechaFactura { get; init; }
    public bool TieneFactura { get; init; }
    public string? Manejo { get; init; }
    public string Estatus { get; init; } = string.Empty;
    public string? Origen { get; init; }
    public string? Revisor { get; init; }
    public DateTime? FechaInicioRevision { get; init; }
    public DateTime? FechaFinRevision { get; init; }
}

public sealed class MonitorTvRevisionesResponse
{
    public bool Success { get; init; }
    public int Total { get; init; }
    public bool Truncado { get; init; }
    public List<MonitorTvRevisionRow> Items { get; init; } = new();
}

/// <summary>Entradas visibles y métricas diarias del turno para el monitor IMEX.</summary>
public sealed class ImexMonitorResponse : EvidenciaResponse
{
    public ImexShiftMetrics ShiftMetrics { get; init; } = new();
}

/// <summary>Totales de revisiones de hoy en IMEX, aunque las entradas hayan arribado antes.</summary>
public sealed class ImexShiftMetrics
{
    public int TotalCompletedReviews { get; init; }
    public int SameDayArrivalAndInvoiceEntries { get; init; }
    public int TeamAverageMinutes { get; init; }
    public int TeamTimedReviews { get; init; }
    public List<ImexReviewerShiftScore> ReviewerScores { get; init; } = new();
}

/// <summary>Trabajo cerrado por un revisor durante el día actual.</summary>
public sealed class ImexReviewerShiftScore
{
    public string Reviewer { get; init; } = string.Empty;
    public int CompletedReviews { get; init; }
    public int SameDayArrivalAndInvoiceEntries { get; init; }
    public int Discrepancies { get; init; }
    public int InvoicesReviewed { get; init; }
    public int TimedReviews { get; init; }
    public int? AverageMinutes { get; init; }
}

public sealed class MonitorTvActivityEvent
{
    public long EventId { get; init; }
    public string Creferencia { get; init; } = string.Empty;
    public string Usuario { get; init; } = string.Empty;
    public string Tipo { get; init; } = string.Empty;
    public string? Cnofact { get; init; }
    public string? Detalle { get; set; }
    public DateTime Fecha { get; init; }
    public int? PhotoId { get; set; }
    public string? OficinaOrigen { get; set; }
}

public sealed class MonitorTvActivityReplayResponse
{
    public bool Success { get; init; }
    public long Cursor { get; init; }
    public bool HasMore { get; init; }
    public List<MonitorTvActivityEvent> Events { get; init; } = new();
}
