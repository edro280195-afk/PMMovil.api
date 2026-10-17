using System;
using System.Collections.Generic;

namespace PMMovil.Api.Models;

/// <summary>Valores vistos al confirmar. No se reemplaza por una lectura más reciente al enviar.</summary>
public sealed record LabelCaptureSnapshot
{
    public string? NumLote { get; init; }
    public string? NumSer { get; init; }
    public string? Marca { get; init; }
    public string? Modelo { get; init; }
    public string? CPaisOrigen { get; init; }
    public bool Revisada { get; init; }
    public string? HazmatUn { get; init; }
    public string? HazmatClase { get; init; }
    public string? HazmatGrupo { get; init; }
}

/// <summary>Null u omitido conserva el dato. Este flujo no permite borrados implícitos.</summary>
public sealed record LabelCaptureFields
{
    public string? NumLote { get; init; }
    public string? NumSer { get; init; }
    public string? Marca { get; init; }
    public string? Modelo { get; init; }
    public string? CPaisOrigen { get; init; }
}

public sealed record LabelCaptureHazmat
{
    public string? Un { get; init; }
    public string? Clase { get; init; }
    public string? Grupo { get; init; }
}

public sealed record LabelCaptureObservation
{
    public string Field { get; init; } = "";
    public string Value { get; init; } = "";
    public string? RawValue { get; init; }
    public string Source { get; init; } = "";
    public bool Accepted { get; init; }
}

public sealed record ConfirmLabelCaptureRequest
{
    public Guid CaptureId { get; init; }
    public string CrefAgrupada { get; init; } = "";
    public int IdRevdet { get; init; }
    public LabelCaptureSnapshot? ExpectedSnapshot { get; init; }
    public LabelCaptureFields Fields { get; init; } = new();
    public LabelCaptureHazmat? Hazmat { get; init; }
    public string PhotoBase64 { get; init; } = "";
    public string Extension { get; init; } = "jpg";
    public bool MarkReviewed { get; init; }
    public bool PhysicalReviewConfirmed { get; init; }
    public IReadOnlyList<LabelCaptureObservation>? Observations { get; init; }
    public string? RawText { get; init; }
    public IReadOnlyList<string>? RawCodes { get; init; }
}

public sealed record LabelCaptureResponse
{
    public bool Success { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public Guid? CaptureId { get; init; }
    public int IdRevdet { get; init; }
    public int? PhotoId { get; init; }
    public bool Revisada { get; init; }
    public bool Replayed { get; init; }
    public bool CanEdit { get; init; }
    public bool CanMarkReviewed { get; init; }
    public LabelCaptureSnapshot? Snapshot { get; init; }
}

public sealed record LabelCaptureResult(int StatusCode, LabelCaptureResponse Body);
