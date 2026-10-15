using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Reglas puras del censo de racks, compartidas por el API y las pruebas sin
/// base de datos. Son el mismo contrato que `RackCensusRules` de Android:
///
/// <list type="bullet">
/// <item>la identidad física de un bulto es su código impreso, no el ordinal;</item>
/// <item>el `UBICA` impreso no se lee: la ubicación esperada sale del snapshot;</item>
/// <item>«no visto» no demuestra «faltante» y exige una decisión humana;</item>
/// <item>un movimiento posterior al snapshot bloquea la conciliación hasta que
/// una persona lo reconozca, y una lectura nunca revierte una salida.</item>
/// </list>
/// </summary>
public static class RackCensusRules
{
    public const string ResolutionResolved = "RESOLVED";
    public const string ResolutionMalformed = "MALFORMED";
    public const string ResolutionFolderLabel = "FOLDER_LABEL";
    public const string ResolutionUnknownCode = "UNKNOWN_CODE";

    public const string BucketMatched = "MATCHED";
    public const string BucketFoundElsewhere = "FOUND_ELSEWHERE";
    public const string BucketNotSeen = "NOT_SEEN";
    public const string BucketMovementReview = "MOVEMENT_REVIEW";

    public const string MovementLocationChanged = "LOCATION_CHANGED";
    public const string MovementLeftWarehouse = "LEFT_WAREHOUSE";
    public const string MovementLabelRemoved = "LABEL_REMOVED";
    public const string MovementStatusChanged = "STATUS_CHANGED";

    /// <summary>Tope de ubicaciones por sesión: un recorrido humano, no la bodega entera.</summary>
    public const int MaxScopeLocations = 24;

    /// <summary>Tope de lecturas por petición, igual que el lote del cliente.</summary>
    public const int MaxObservationsPerRequest = 50;

    /// <summary>Tope del catálogo de otras ubicaciones que viaja en el snapshot.</summary>
    public const int MaxForeignCatalog = 20000;

    /// <summary>Estatus de etiqueta que ya no ocupan ubicación en bodega.</summary>
    private static readonly string[] ReleasedStatuses = { "CARGADO", "SALIDA", "ELIMINAR" };

    private static readonly Regex CodePattern =
        new(@"^([A-Z0-9][A-Z0-9_/-]{2,29})(?:\s+(\d{1,6})-(\d{1,6}))?$", RegexOptions.Compiled);
    private static readonly Regex LocationPattern = new(@"^[A-Z0-9][A-Z0-9 ._/-]{0,59}$", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRuns = new(@"\s+", RegexOptions.Compiled);

    public sealed record ParsedCode(string Normalized, string Reference, int? PrintedOrdinal, int? DeclaredTotal)
    {
        public bool IsFolderLabel => PrintedOrdinal is null;
    }

    /// <summary>Una lectura ya clasificada, tal como queda guardada.</summary>
    public sealed record ObservationRow(string NormalizedCode, string? PackageId, string ObservedLocationId, string Resolution);

    public sealed record PackageOutcome(
        string PackageId,
        string Bucket,
        string ExpectedLocationId,
        IReadOnlyList<string> ObservedLocationIds,
        bool InScope);

    public sealed record ReconcileOutcome(
        RackCensusCounts Counts,
        IReadOnlyList<PackageOutcome> Packages,
        IReadOnlyList<string> BlockReasons)
    {
        public bool CanConfirm => BlockReasons.Count == 0;
    }

    /// <summary>Mismo criterio que usa Android, para comparar contra `textoetiqueta`.</summary>
    public static string NormalizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string cleaned = new(value.Replace(' ', ' ').Where(c => !char.IsControl(c)).ToArray());
        return WhitespaceRuns.Replace(cleaned.Trim(), " ").ToUpperInvariant();
    }

    public static string NormalizeLocation(string? value) => (value ?? "").Trim().ToUpperInvariant();

    /// <summary>Una etiqueta liberada (cargada, salida o eliminada) ya no ocupa ubicación.</summary>
    public static bool IsReleased(string? estatus) =>
        ReleasedStatuses.Contains((estatus ?? "").Trim().ToUpperInvariant());

    public static ParsedCode? Parse(string? raw)
    {
        string normalized = NormalizeCode(raw);
        if (normalized.Length == 0 || normalized.Length > 120) return null;
        Match match = CodePattern.Match(normalized);
        if (!match.Success) return null;
        int? ordinal = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : null;
        int? total = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : null;
        // Un ordinal mayor al total, o un total en cero, es una lectura mutilada.
        if (ordinal is not null && (total is null or <= 0 || ordinal is <= 0 || ordinal > total)) return null;
        return new ParsedCode(normalized, match.Groups[1].Value, ordinal, total);
    }

    /// <summary>
    /// Clasifica una lectura contra el inventario del snapshot. Nunca deduce un
    /// bulto a partir del ordinal ni de la referencia.
    /// </summary>
    public static (string Resolution, string? PackageId, long? LabelId) Resolve(
        IReadOnlyDictionary<string, RackCensusPackage> packagesByCode, string? rawCode)
    {
        ParsedCode? parsed = Parse(rawCode);
        if (parsed is null) return (ResolutionMalformed, null, null);
        if (parsed.IsFolderLabel)
        {
            bool knownReference = packagesByCode.Values
                .Any(p => string.Equals(p.Reference, parsed.Reference, StringComparison.OrdinalIgnoreCase));
            return (knownReference ? ResolutionFolderLabel : ResolutionUnknownCode, null, null);
        }
        if (!packagesByCode.TryGetValue(parsed.Normalized, out RackCensusPackage? match))
            return (ResolutionUnknownCode, null, null);
        return (ResolutionResolved, match.PackageId, match.LabelIds.Count == 0 ? null : match.LabelIds.Min());
    }

    public static void ValidateOpen(RackCensusOpenRequest request)
    {
        if (request.SessionId == Guid.Empty) throw new ArgumentException("Falta el identificador de la sesión de censo.");
        if (!Regex.IsMatch(request.WarehouseId?.Trim() ?? "", @"^[A-Za-z0-9_-]{1,30}$"))
            throw new ArgumentException("La bodega del censo no es válida.");
        List<string> scope = NormalizeScope(request.LocationIds);
        if (scope.Count == 0) throw new ArgumentException("Selecciona al menos una ubicación para el censo.");
        if (scope.Count > MaxScopeLocations)
            throw new ArgumentException($"Un censo admite hasta {MaxScopeLocations} ubicaciones por recorrido.");
    }

    /// <summary>Quita repetidas y rechaza códigos que no son una ubicación del mapa.</summary>
    public static List<string> NormalizeScope(IReadOnlyList<string>? locationIds)
    {
        List<string> scope = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string raw in locationIds ?? Array.Empty<string>())
        {
            string location = NormalizeLocation(raw);
            if (location.Length == 0) continue;
            if (!LocationPattern.IsMatch(location))
                throw new ArgumentException($"La ubicación «{location}» no tiene un código válido.");
            if (seen.Add(location)) scope.Add(location);
        }
        return scope;
    }

    public static void ValidateObserve(RackCensusObserveRequest request)
    {
        if (request.SessionId == Guid.Empty) throw new ArgumentException("Falta el identificador de la sesión de censo.");
        if (request.Observations.Count == 0) throw new ArgumentException("No hay lecturas que registrar.");
        if (request.Observations.Count > MaxObservationsPerRequest)
            throw new ArgumentException($"Envía hasta {MaxObservationsPerRequest} lecturas por petición.");
        HashSet<Guid> seen = new();
        foreach (RackCensusObservationInput observation in request.Observations)
        {
            if (observation.ObservationId == Guid.Empty)
                throw new ArgumentException("Cada lectura requiere su propio identificador.");
            if (!seen.Add(observation.ObservationId))
                throw new ArgumentException("Hay identificadores de lectura repetidos en el envío.");
            if (observation.ObservedAtMillis <= 0)
                throw new ArgumentException("Cada lectura requiere la hora en que se tomó.");
            string code = NormalizeCode(observation.RawCode);
            if (code.Length == 0 || code.Length > 120)
                throw new ArgumentException("La lectura está vacía o excede el tamaño permitido.");
            string location = NormalizeLocation(observation.ObservedLocationId);
            if (!LocationPattern.IsMatch(location))
                throw new ArgumentException("La lectura no indica una ubicación observada válida.");
        }
    }

    public static void ValidateReconcile(RackCensusReconcileRequest request)
    {
        if (request.SessionId == Guid.Empty) throw new ArgumentException("Falta el identificador de la sesión de censo.");
        if (request.SnapshotVersion <= 0)
            throw new ArgumentException("Falta la versión del inventario con la que se comparó.");
        if (request.Note.Length > 2048) throw new ArgumentException("La nota del censo excede el tamaño permitido.");
        if (request.AcknowledgedMovementPackageIds.Count > 20000 ||
            request.MissingConfirmations.Count > 20000 || request.KeptPendingPackageIds.Count > 20000)
            throw new ArgumentException("La conciliación excede el tamaño permitido.");
        foreach (RackCensusMissingConfirmation confirmation in request.MissingConfirmations)
        {
            if (NormalizeCode(confirmation.PackageId).Length == 0)
                throw new ArgumentException("Una confirmación de faltante no indica el bulto.");
            if (confirmation.ConfirmedAtMillis <= 0)
                throw new ArgumentException("Una confirmación de faltante no indica cuándo se confirmó.");
        }
        HashSet<string> missing = new(request.MissingConfirmations.Select(c => NormalizeCode(c.PackageId)), StringComparer.Ordinal);
        if (request.KeptPendingPackageIds.Select(NormalizeCode).Any(missing.Contains))
            throw new ArgumentException("Un bulto no puede quedar confirmado como faltante y pendiente a la vez.");
    }

    /// <summary>
    /// Arma las cuatro categorías y la compuerta de movimientos. Los movimientos
    /// que llegan aquí ya están filtrados a los posteriores al snapshot.
    /// </summary>
    public static ReconcileOutcome Reconcile(
        IReadOnlyList<RackCensusPackage> packages,
        IReadOnlyList<ObservationRow> observations,
        IReadOnlyList<RackCensusMovement> concurrentMovements,
        IReadOnlySet<string> acknowledgedMovements,
        IReadOnlySet<string> confirmedMissing,
        IReadOnlySet<string> keptPending,
        IReadOnlySet<string> scope)
    {
        Dictionary<string, List<ObservationRow>> byPackage = observations
            .Where(o => o.Resolution == ResolutionResolved && !string.IsNullOrEmpty(o.PackageId))
            .GroupBy(o => o.PackageId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        HashSet<string> moved = new(concurrentMovements.Select(m => m.PackageId), StringComparer.Ordinal);

        List<PackageOutcome> outcomes = new();
        foreach (RackCensusPackage item in packages)
        {
            List<ObservationRow> seen = byPackage.TryGetValue(item.PackageId, out List<ObservationRow>? rows)
                ? rows : new List<ObservationRow>();
            string expected = NormalizeLocation(item.ExpectedLocationId);
            // Un bulto esperado fuera del alcance solo aparece si se vio aquí.
            if (!scope.Contains(expected) && seen.Count == 0) continue;
            List<string> locations = seen.Select(o => NormalizeLocation(o.ObservedLocationId))
                .Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal).ToList();
            string bucket = moved.Contains(item.PackageId) ? BucketMovementReview
                : locations.Count > 1 ? BucketFoundElsewhere
                : locations.Count == 0 ? BucketNotSeen
                : locations[0] == expected ? BucketMatched
                : BucketFoundElsewhere;
            outcomes.Add(new PackageOutcome(item.PackageId, bucket, expected, locations, item.InScope));
        }

        List<string> unidentifiedCodes = observations
            .Where(o => o.Resolution != ResolutionResolved)
            .Select(o => o.NormalizedCode)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        List<PackageOutcome> notSeen = outcomes.Where(o => o.Bucket == BucketNotSeen).ToList();
        List<RackCensusMovement> pending = concurrentMovements
            .Where(m => !acknowledgedMovements.Contains(m.PackageId)).ToList();
        int undecided = notSeen.Count(o =>
            !confirmedMissing.Contains(o.PackageId) && !keptPending.Contains(o.PackageId));

        List<string> blockReasons = new();
        if (pending.Count > 0)
            blockReasons.Add($"Hay {pending.Count} bulto(s) con movimientos durante el recorrido. Revísalos antes de conciliar.");
        if (undecided > 0)
            blockReasons.Add($"Hay {undecided} bulto(s) no vistos sin decidir. Confirma el faltante o déjalo pendiente a propósito.");

        RackCensusCounts counts = new()
        {
            ExpectedInScope = packages.Count(p => p.InScope),
            UniqueObservedPackages = byPackage.Count,
            Matched = outcomes.Count(o => o.Bucket == BucketMatched),
            FoundElsewhere = outcomes.Count(o => o.Bucket == BucketFoundElsewhere),
            NotSeen = notSeen.Count,
            MissingConfirmed = notSeen.Count(o => confirmedMissing.Contains(o.PackageId)),
            UnidentifiedCodes = unidentifiedCodes.Count,
            MovementsPendingReview = pending.Count,
            TotalScans = observations.Count
        };
        return new ReconcileOutcome(counts, outcomes, blockReasons);
    }
}
