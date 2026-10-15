using System;
using System.Collections.Generic;

namespace PMMovil.Api.Models;

/// <summary>
/// Censo de racks por QR. El identificador físico de un bulto es el código
/// impreso en su etiqueta (<c>etiqueta_mcia.textoetiqueta</c> normalizado); el
/// ordinal impreso («14/17») es dato de la etiqueta, nunca un conteo.
/// </summary>
public sealed record RackCensusOpenRequest
{
    public Guid SessionId { get; init; }
    public string WarehouseId { get; init; } = "";
    public IReadOnlyList<string> LocationIds { get; init; } = Array.Empty<string>();
}

/// <summary>Un bulto esperado. <c>LabelIds</c> lleva todas las filas con ese código impreso.</summary>
public sealed record RackCensusPackage
{
    public string PackageId { get; init; } = "";
    public IReadOnlyList<long> LabelIds { get; init; } = Array.Empty<long>();
    public string ExpectedLocationId { get; init; } = "";
    public string Reference { get; init; } = "";
    public int Line { get; init; }
    public string Estatus { get; init; } = "";
    public string Cliente { get; init; } = "";
    public string Descripcion { get; init; } = "";
    public int? DeclaredTotal { get; init; }
    public int? PrintedOrdinal { get; init; }
    public bool InScope { get; init; } = true;
}

public sealed record RackCensusOpenResponse
{
    public bool Success { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public Guid? SessionId { get; init; }
    public string WarehouseId { get; init; } = "";
    /// <summary>Reloj del servidor al abrir, en milisegundos UTC. Ordena los movimientos.</summary>
    public long SnapshotVersion { get; init; }
    public long CapturedAtMillis { get; init; }
    public IReadOnlyList<string> ScopeLocationIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<RackCensusPackage> Packages { get; init; } = Array.Empty<RackCensusPackage>();
    /// <summary>true cuando el catálogo de otras ubicaciones no cupo completo en el snapshot.</summary>
    public bool ForeignCatalogTruncated { get; init; }
    /// <summary>Etiquetas excluidas por datos contradictorios; no entran al censo.</summary>
    public IReadOnlyList<string> DataWarnings { get; init; } = Array.Empty<string>();
    public bool Replayed { get; init; }
}

public sealed record RackCensusObservationInput
{
    public Guid ObservationId { get; init; }
    public string RawCode { get; init; } = "";
    public string ObservedLocationId { get; init; } = "";
    public long ObservedAtMillis { get; init; }
}

public sealed record RackCensusObserveRequest
{
    public Guid SessionId { get; init; }
    public IReadOnlyList<RackCensusObservationInput> Observations { get; init; } =
        Array.Empty<RackCensusObservationInput>();
}

public sealed record RackCensusObservationResult
{
    public Guid ObservationId { get; init; }
    public bool Accepted { get; init; }
    /// <summary>RESOLVED | MALFORMED | FOLDER_LABEL | UNKNOWN_CODE</summary>
    public string Resolution { get; init; } = "";
    public string? PackageId { get; init; }
    public long? LabelId { get; init; }
    public string? Message { get; init; }
}

public sealed record RackCensusObserveResponse
{
    public bool Success { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public Guid? SessionId { get; init; }
    public IReadOnlyList<RackCensusObservationResult> Results { get; init; } =
        Array.Empty<RackCensusObservationResult>();
}

public sealed record RackCensusMovement
{
    public string PackageId { get; init; } = "";
    public string Reference { get; init; } = "";
    public string ExpectedLocationId { get; init; } = "";
    /// <summary>LOCATION_CHANGED | LEFT_WAREHOUSE | LABEL_REMOVED | STATUS_CHANGED</summary>
    public string Kind { get; init; } = "";
    public string? FromLocationId { get; init; }
    public string? ToLocationId { get; init; }
    public long DetectedVersion { get; init; }
}

public sealed record RackCensusMovementsResponse
{
    public bool Success { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public Guid? SessionId { get; init; }
    public long SnapshotVersion { get; init; }
    public long CheckedAtMillis { get; init; }
    public IReadOnlyList<RackCensusMovement> Movements { get; init; } = Array.Empty<RackCensusMovement>();
}

public sealed record RackCensusMissingConfirmation
{
    public string PackageId { get; init; } = "";
    public long ConfirmedAtMillis { get; init; }
}

public sealed record RackCensusReconcileRequest
{
    public Guid SessionId { get; init; }
    public long SnapshotVersion { get; init; }
    public IReadOnlyList<string> AcknowledgedMovementPackageIds { get; init; } = Array.Empty<string>();
    /// <summary>Quien confirma es el usuario autenticado; el cliente no lo elige.</summary>
    public IReadOnlyList<RackCensusMissingConfirmation> MissingConfirmations { get; init; } =
        Array.Empty<RackCensusMissingConfirmation>();
    /// <summary>No vistos que se dejan sin resolver a propósito; no afirman faltante.</summary>
    public IReadOnlyList<string> KeptPendingPackageIds { get; init; } = Array.Empty<string>();
    public string Note { get; init; } = "";
}

public sealed record RackCensusCounts
{
    public int ExpectedInScope { get; init; }
    /// <summary>Identidades físicas distintas observadas. El único conteo válido.</summary>
    public int UniqueObservedPackages { get; init; }
    public int Matched { get; init; }
    public int FoundElsewhere { get; init; }
    public int NotSeen { get; init; }
    public int MissingConfirmed { get; init; }
    public int UnidentifiedCodes { get; init; }
    public int MovementsPendingReview { get; init; }
    public int TotalScans { get; init; }
}

public sealed record RackCensusReconcileResponse
{
    public bool Success { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public Guid? SessionId { get; init; }
    public long ClosedAtMillis { get; init; }
    public bool Replayed { get; init; }
    public RackCensusCounts? Counts { get; init; }
    public IReadOnlyList<RackCensusMovement> Movements { get; init; } = Array.Empty<RackCensusMovement>();
    public IReadOnlyList<string> BlockReasons { get; init; } = Array.Empty<string>();
}

public sealed record RackCensusResult(int StatusCode, object Body);
