using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PMMovil.Api.Attributes;
using PMMovil.Api.Controllers;
using PMMovil.Api.Data;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

// Regresiones del censo de racks sin servidor ni conexiones a bases de datos.
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (ArgumentException) { checks++; return; }
    throw new InvalidOperationException(message);
}

// ── Código impreso: el ordinal es dato de la etiqueta, no identidad ──────────

Check(RackCensusRules.NormalizeCode(" 01c260000123   14-17 ") == "01C260000123 14-17",
    "Normalizar deja el código comparable contra textoetiqueta.");
Check(RackCensusRules.NormalizeCode("01C26\u001D 1-2") == "01C26 1-2", "Los controles no forman parte del código.");
Check(RackCensusRules.NormalizeCode(null) == "" && RackCensusRules.NormalizeCode("  ") == "",
    "Una lectura vacía no produce código.");

RackCensusRules.ParsedCode parsed = RackCensusRules.Parse("01C260000123 14-17")!;
Check(parsed.Reference == "01C260000123" && parsed.PrintedOrdinal == 14 && parsed.DeclaredTotal == 17 && !parsed.IsFolderLabel,
    "Se lee el ordinal y el total impresos sin convertirlos en conteo.");
Check(RackCensusRules.Parse("01C260000123")!.IsFolderLabel, "La etiqueta de carpeta no trae ordinal.");
foreach (string invalid in new[] { "01C26 18-17", "01C26 0-3", "01C26 3-0", "../ruta", "A", "", "   " })
    Check(RackCensusRules.Parse(invalid) is null, $"Lectura inválida rechazada: «{invalid}».");
Check(RackCensusRules.Parse(new string('X', 121)) is null, "Una lectura enorme se rechaza.");

Check(RackCensusRules.IsReleased("CARGADO") && RackCensusRules.IsReleased(" salida ") && RackCensusRules.IsReleased("ELIMINAR"),
    "Una etiqueta liberada ya no ocupa ubicación.");
Check(!RackCensusRules.IsReleased("") && !RackCensusRules.IsReleased("PENDIENTE"),
    "Una etiqueta pendiente sigue ocupando su ubicación.");

// ── Resolución contra el inventario ─────────────────────────────────────────

RackCensusPackage Package(string code, string location, long[] labels, bool inScope = true) => new()
{
    PackageId = code, LabelIds = labels, ExpectedLocationId = location,
    Reference = code.Split(' ')[0], Line = 1, InScope = inScope
};
Dictionary<string, RackCensusPackage> catalog = new(StringComparer.Ordinal)
{
    ["01C26 1-3"] = Package("01C26 1-3", "R1", new[] { 501L, 101L }),
    ["01C26 2-3"] = Package("01C26 2-3", "R1", new[] { 102L }),
    ["01C26 3-3"] = Package("01C26 3-3", "R2", new[] { 103L }),
    ["01D99 1-1"] = Package("01D99 1-1", "R9", new[] { 901L }, inScope: false)
};

(string resolution, string? packageId, long? labelId) = RackCensusRules.Resolve(catalog, " 01c26 1-3 ");
Check(resolution == RackCensusRules.ResolutionResolved && packageId == "01C26 1-3" && labelId == 101L,
    "Un código conocido resuelve a su bulto y usa la fila menor como representante.");
Check(RackCensusRules.Resolve(catalog, "01C26 1-4").Resolution == RackCensusRules.ResolutionUnknownCode,
    "Una reimpresión con otro total no se adivina.");
Check(RackCensusRules.Resolve(catalog, "01C26").Resolution == RackCensusRules.ResolutionFolderLabel,
    "La etiqueta de carpeta de una referencia conocida se reconoce como tal.");
Check(RackCensusRules.Resolve(catalog, "01Z00").Resolution == RackCensusRules.ResolutionUnknownCode,
    "Un código de referencia desconocida no se llama carpeta.");
Check(RackCensusRules.Resolve(catalog, "../ruta").Resolution == RackCensusRules.ResolutionMalformed,
    "Una lectura que no es etiqueta IMEX se marca como ilegible.");
Check(RackCensusRules.Resolve(catalog, "01D99 1-1").PackageId == "01D99 1-1",
    "Un bulto de otra ubicación se puede nombrar cuando aparece en el rack.");

// ── Alcance y validaciones ──────────────────────────────────────────────────

Check(RackCensusRules.NormalizeScope(new[] { " r1 ", "R1", "r2" }).SequenceEqual(new[] { "R1", "R2" }),
    "El alcance se normaliza y no repite ubicaciones.");
Reject(() => RackCensusRules.NormalizeScope(new[] { "../otra" }), "Una ubicación no puede ser una ruta.");
Reject(() => RackCensusRules.NormalizeScope(new[] { "R1;DROP" }), "Una ubicación no admite caracteres fuera del catálogo.");

Guid sessionId = Guid.Parse("3583d20a-27cb-4af6-9f59-3e840ba7b8ae");
RackCensusOpenRequest open = new() { SessionId = sessionId, WarehouseId = "MAQUILA", LocationIds = new[] { "R1" } };
RackCensusRules.ValidateOpen(open); checks++;
Reject(() => RackCensusRules.ValidateOpen(open with { SessionId = Guid.Empty }), "Se requiere UUID de sesión.");
Reject(() => RackCensusRules.ValidateOpen(open with { WarehouseId = "../bodega" }), "La bodega no puede ser una ruta.");
Reject(() => RackCensusRules.ValidateOpen(open with { LocationIds = Array.Empty<string>() }), "Se requiere alcance.");
Reject(() => RackCensusRules.ValidateOpen(open with
{ LocationIds = Enumerable.Range(1, RackCensusRules.MaxScopeLocations + 1).Select(i => $"R{i}").ToArray() }),
    "Un recorrido no abarca la bodega entera.");

RackCensusObservationInput reading = new()
{
    ObservationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
    RawCode = "01C26 1-3", ObservedLocationId = "R1", ObservedAtMillis = 1_700_000_000_000
};
RackCensusObserveRequest observe = new() { SessionId = sessionId, Observations = new[] { reading } };
RackCensusRules.ValidateObserve(observe); checks++;
Reject(() => RackCensusRules.ValidateObserve(observe with { Observations = Array.Empty<RackCensusObservationInput>() }),
    "No se aceptan envíos vacíos.");
Reject(() => RackCensusRules.ValidateObserve(observe with { Observations = new[] { reading, reading } }),
    "Dos lecturas no comparten identificador.");
Reject(() => RackCensusRules.ValidateObserve(observe with
{ Observations = new[] { reading with { ObservationId = Guid.Empty } } }), "Cada lectura requiere identificador.");
Reject(() => RackCensusRules.ValidateObserve(observe with
{ Observations = new[] { reading with { ObservedAtMillis = 0 } } }), "Cada lectura requiere su hora.");
Reject(() => RackCensusRules.ValidateObserve(observe with
{ Observations = new[] { reading with { RawCode = "" } } }), "Una lectura vacía se rechaza.");
Reject(() => RackCensusRules.ValidateObserve(observe with
{ Observations = new[] { reading with { ObservedLocationId = "../R1" } } }), "La ubicación observada se valida.");
Reject(() => RackCensusRules.ValidateObserve(observe with
{
    Observations = Enumerable.Range(1, RackCensusRules.MaxObservationsPerRequest + 1)
        .Select(i => reading with { ObservationId = Guid.NewGuid() }).ToArray()
}), "El lote de lecturas está acotado.");

RackCensusReconcileRequest reconcile = new() { SessionId = sessionId, SnapshotVersion = 1000 };
RackCensusRules.ValidateReconcile(reconcile); checks++;
Reject(() => RackCensusRules.ValidateReconcile(reconcile with { SnapshotVersion = 0 }),
    "Se requiere la versión del inventario comparado.");
Reject(() => RackCensusRules.ValidateReconcile(reconcile with { Note = new string('x', 2049) }),
    "La nota del censo está acotada.");
Reject(() => RackCensusRules.ValidateReconcile(reconcile with
{
    MissingConfirmations = new[] { new RackCensusMissingConfirmation { PackageId = "01C26 2-3", ConfirmedAtMillis = 1 } },
    KeptPendingPackageIds = new[] { "01C26 2-3" }
}), "Un bulto no es faltante confirmado y pendiente a la vez.");
Reject(() => RackCensusRules.ValidateReconcile(reconcile with
{ MissingConfirmations = new[] { new RackCensusMissingConfirmation { PackageId = " ", ConfirmedAtMillis = 1 } } }),
    "Una confirmación de faltante indica el bulto.");
Reject(() => RackCensusRules.ValidateReconcile(reconcile with
{ MissingConfirmations = new[] { new RackCensusMissingConfirmation { PackageId = "01C26 2-3", ConfirmedAtMillis = 0 } } }),
    "Una confirmación de faltante indica cuándo se hizo.");

// ── Conciliación: cuatro categorías y compuertas ────────────────────────────

List<RackCensusPackage> packages = catalog.Values.ToList();
HashSet<string> scope = new(new[] { "R1", "R2" }, StringComparer.Ordinal);
HashSet<string> none = new(StringComparer.Ordinal);
RackCensusRules.ObservationRow Row(string code, string location, string? packageId) =>
    new(code, packageId, location, packageId is null ? RackCensusRules.ResolutionUnknownCode : RackCensusRules.ResolutionResolved);

RackCensusRules.ReconcileOutcome outcome = RackCensusRules.Reconcile(
    packages,
    new[]
    {
        Row("01C26 1-3", "R1", "01C26 1-3"),
        Row("01C26 3-3", "R1", "01C26 3-3"),
        Row("01E77 9-9", "R1", null)
    },
    Array.Empty<RackCensusMovement>(), none, none,
    new HashSet<string>(new[] { "01C26 2-3" }, StringComparer.Ordinal), scope);
Check(outcome.Counts.Matched == 1 && outcome.Counts.FoundElsewhere == 1 && outcome.Counts.NotSeen == 1,
    "Las tres categorías de bulto se reparten sin mezclarse.");
Check(outcome.Counts.UnidentifiedCodes == 1 && outcome.Counts.UniqueObservedPackages == 2,
    "Un código sin identidad no se cuenta como bulto observado.");
Check(outcome.Packages.Count == 3 && outcome.Packages.Select(p => p.PackageId).Distinct().Count() == 3,
    "Ningún bulto aparece en dos categorías.");
Check(outcome.Packages.All(p => p.PackageId != "01D99 1-1"),
    "Un bulto esperado fuera del alcance no entra si no se vio.");
Check(outcome.Counts.ExpectedInScope == 3, "El catálogo de otras ubicaciones no infla lo esperado.");
Check(outcome.CanConfirm, "Con todo decidido se puede conciliar.");
Check(outcome.Packages.Single(p => p.PackageId == "01C26 3-3").ExpectedLocationId == "R2",
    "La ubicación esperada sale del snapshot, no del UBICA impreso.");

RackCensusRules.ReconcileOutcome foreignFound = RackCensusRules.Reconcile(
    packages, new[] { Row("01D99 1-1", "R1", "01D99 1-1") }, Array.Empty<RackCensusMovement>(),
    none, none, new HashSet<string>(new[] { "01C26 1-3", "01C26 2-3", "01C26 3-3" }, StringComparer.Ordinal), scope);
Check(foreignFound.Packages.Any(p => p.PackageId == "01D99 1-1" && p.Bucket == RackCensusRules.BucketFoundElsewhere),
    "Un bulto ajeno visto en el rack se nombra en lugar de quedar sin identificar.");
Check(foreignFound.Counts.UnidentifiedCodes == 0, "Ese hallazgo no se cuenta dos veces.");

RackCensusRules.ReconcileOutcome repeated = RackCensusRules.Reconcile(
    packages,
    new[] { Row("01C26 1-3", "R1", "01C26 1-3"), Row("01C26 1-3", "R1", "01C26 1-3"), Row("01C26 1-3", "R1", "01C26 1-3") },
    Array.Empty<RackCensusMovement>(), none, none,
    new HashSet<string>(new[] { "01C26 2-3", "01C26 3-3" }, StringComparer.Ordinal), scope);
Check(repeated.Counts.UniqueObservedPackages == 1 && repeated.Counts.Matched == 1 && repeated.Counts.TotalScans == 3,
    "Leer la misma etiqueta tres veces es un solo bulto.");

RackCensusRules.ReconcileOutcome twoLocations = RackCensusRules.Reconcile(
    packages, new[] { Row("01C26 1-3", "R1", "01C26 1-3"), Row("01C26 1-3", "R2", "01C26 1-3") },
    Array.Empty<RackCensusMovement>(), none, none,
    new HashSet<string>(new[] { "01C26 2-3", "01C26 3-3" }, StringComparer.Ordinal), scope);
Check(twoLocations.Packages.Single(p => p.PackageId == "01C26 1-3").Bucket == RackCensusRules.BucketFoundElsewhere,
    "Un código leído en dos ubicaciones queda para revisión, no se elige una.");

// Catorce lecturas de otra impresión no prueban catorce bultos vistos.
List<RackCensusPackage> seventeen = Enumerable.Range(1, 17)
    .Select(i => Package($"01C26 {i}-17", "R1", new[] { 1000L + i })).ToList();
RackCensusRules.ReconcileOutcome ordinalOnly = RackCensusRules.Reconcile(
    seventeen, Enumerable.Range(1, 14).Select(i => Row($"01C26 {i}-18", "R1", null)).ToArray(),
    Array.Empty<RackCensusMovement>(), none, none,
    seventeen.Select(p => p.PackageId).ToHashSet(StringComparer.Ordinal), scope);
Check(ordinalOnly.Counts.UniqueObservedPackages == 0 && ordinalOnly.Counts.NotSeen == 17 &&
      ordinalOnly.Counts.UnidentifiedCodes == 14,
    "El total declarado nunca se convierte en conteo de bultos vistos.");

RackCensusMovement movement = new()
{
    PackageId = "01C26 1-3", Reference = "01C26", ExpectedLocationId = "R1",
    Kind = RackCensusRules.MovementLeftWarehouse, DetectedVersion = 1500
};
RackCensusRules.ReconcileOutcome moved = RackCensusRules.Reconcile(
    packages, new[] { Row("01C26 1-3", "R1", "01C26 1-3") }, new[] { movement }, none, none,
    new HashSet<string>(new[] { "01C26 2-3", "01C26 3-3" }, StringComparer.Ordinal), scope);
Check(!moved.CanConfirm && moved.Counts.MovementsPendingReview == 1,
    "Un movimiento durante el recorrido bloquea la conciliación.");
Check(moved.Packages.Single(p => p.PackageId == "01C26 1-3").Bucket == RackCensusRules.BucketMovementReview &&
      moved.Counts.Matched == 0,
    "Una lectura no se presenta como coincidencia cuando el bulto ya salió.");
RackCensusRules.ReconcileOutcome acknowledged = RackCensusRules.Reconcile(
    packages, new[] { Row("01C26 1-3", "R1", "01C26 1-3") }, new[] { movement },
    new HashSet<string>(new[] { "01C26 1-3" }, StringComparer.Ordinal), none,
    new HashSet<string>(new[] { "01C26 2-3", "01C26 3-3" }, StringComparer.Ordinal), scope);
Check(acknowledged.CanConfirm && acknowledged.Counts.MovementsPendingReview == 0,
    "Reconocer el movimiento es lo que desbloquea, no leer otra vez la etiqueta.");

RackCensusRules.ReconcileOutcome undecided = RackCensusRules.Reconcile(
    packages, Array.Empty<RackCensusRules.ObservationRow>(), Array.Empty<RackCensusMovement>(),
    none, none, none, scope);
Check(!undecided.CanConfirm && undecided.Counts.NotSeen == 3 && undecided.Counts.MissingConfirmed == 0,
    "«No visto» sin decidir bloquea y no se cuenta como faltante.");
Check(undecided.BlockReasons.Any(reason => reason.Contains("no vistos sin decidir")),
    "El motivo del bloqueo se explica al revisor.");
RackCensusRules.ReconcileOutcome pending = RackCensusRules.Reconcile(
    packages, Array.Empty<RackCensusRules.ObservationRow>(), Array.Empty<RackCensusMovement>(), none, none,
    new HashSet<string>(new[] { "01C26 1-3", "01C26 2-3", "01C26 3-3" }, StringComparer.Ordinal), scope);
Check(pending.CanConfirm && pending.Counts.MissingConfirmed == 0,
    "Dejar pendiente a propósito desbloquea sin afirmar que falte.");
RackCensusRules.ReconcileOutcome missing = RackCensusRules.Reconcile(
    packages, Array.Empty<RackCensusRules.ObservationRow>(), Array.Empty<RackCensusMovement>(), none,
    new HashSet<string>(new[] { "01C26 1-3", "01C26 2-3", "01C26 3-3" }, StringComparer.Ordinal), none, scope);
Check(missing.CanConfirm && missing.Counts.MissingConfirmed == 3,
    "Un faltante confirmado se registra como tal.");

// ── Contrato y endpoints ────────────────────────────────────────────────────

JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
string json = JsonSerializer.Serialize(observe, jsonOptions);
Check(json.Contains("\"observationId\"") && json.Contains("\"observedLocationId\"") && json.Contains("\"rawCode\""),
    "Nombres de lectura iguales a los de Android.");
Check(JsonSerializer.Deserialize<RackCensusObserveRequest>(json, jsonOptions)!.Observations[0].RawCode == "01C26 1-3",
    "El contrato conserva la lectura al serializar.");
string openJson = JsonSerializer.Serialize(new RackCensusOpenResponse
{ Success = true, SessionId = sessionId, SnapshotVersion = 1000, ForeignCatalogTruncated = true }, jsonOptions);
Check(openJson.Contains("\"snapshotVersion\":1000") && openJson.Contains("\"foreignCatalogTruncated\":true"),
    "El snapshot avisa cuando el catálogo ajeno viene recortado.");

Type controller = typeof(RackCensusController);
Check(controller.GetCustomAttribute<AuthorizeAttribute>() is not null, "Endpoint autenticado.");
Check(controller.GetCustomAttribute<RouteAttribute>()!.Template == "api/Inventario/rack-census", "Ruta acordada.");
Check(controller.GetCustomAttribute<RequiereModuloAttribute>() is not null, "El censo exige el módulo de Inventario.");
Check(controller.GetMethod("Open")!.GetCustomAttribute<HttpPostAttribute>()!.Template == "sessions",
    "POST sessions abre el censo.");
Check(controller.GetMethod("Open")!.GetCustomAttribute<RequiereModuloAttribute>() is not null,
    "Abrir exige permiso explícito.");
Check(controller.GetMethod("Observe")!.GetCustomAttribute<HttpPostAttribute>()!.Template == "sessions/{sessionId:guid}/observations",
    "POST observations registra el barrido.");
Check(controller.GetMethod("Observe")!.GetCustomAttribute<RequiereModuloAttribute>() is not null,
    "Registrar lecturas exige permiso de agregar.");
Check(controller.GetMethod("Movements")!.GetCustomAttribute<HttpGetAttribute>()!.Template == "sessions/{sessionId:guid}/movements",
    "GET movements expone la compuerta de movimientos.");
Check(controller.GetMethod("Reconcile")!.GetCustomAttribute<HttpPostAttribute>()!.Template == "sessions/{sessionId:guid}/reconcile",
    "POST reconcile cierra el censo.");
Check(controller.GetMethod("Reconcile")!.GetCustomAttribute<RequiereModuloAttribute>() is not null,
    "Conciliar exige permiso de modificar porque escribe la última observación.");

// ── Orden de las guardas en el servicio ─────────────────────────────────────

IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{ ["ConnectionStrings:PmmtrafDb"] = "Server=127.0.0.1;Port=1;Database=sin_base;Uid=nadie;Pwd=nada;" }).Build();
DeniedPermissions denied = new();
RackCensusService service = new(new BaseRepository(configuration), denied, NullLogger<RackCensusService>.Instance);

RackCensusResult invalidOpen = await service.OpenAsync(open with { LocationIds = Array.Empty<string>() }, "tester", default);
Check(invalidOpen.StatusCode == 400 && ((RackCensusOpenResponse)invalidOpen.Body).Code == "INVALID_CENSUS" && denied.Calls == 0,
    "Validación anterior a permisos y base de datos.");
RackCensusResult deniedOpen = await service.OpenAsync(open, "tester", default);
Check(deniedOpen.StatusCode == 403 && ((RackCensusOpenResponse)deniedOpen.Body).Code == "PERMISSION_DENIED",
    "Permiso denegado sin abrir la base de datos.");
RackCensusResult crossedRoute = await service.ObserveAsync(Guid.NewGuid(), observe, "tester", default);
Check(crossedRoute.StatusCode == 400 && ((RackCensusObserveResponse)crossedRoute.Body).Code == "INVALID_CENSUS",
    "Una ruta y un cuerpo de sesiones distintas se rechazan.");
RackCensusResult deniedObserve = await service.ObserveAsync(sessionId, observe, "tester", default);
Check(deniedObserve.StatusCode == 403 && ((RackCensusObserveResponse)deniedObserve.Body).Code == "PERMISSION_DENIED",
    "Registrar lecturas exige permiso antes de tocar la base.");
RackCensusResult deniedReconcile = await service.ReconcileAsync(sessionId, reconcile, "tester", default);
Check(deniedReconcile.StatusCode == 403 && ((RackCensusReconcileResponse)deniedReconcile.Body).Code == "PERMISSION_DENIED",
    "Conciliar exige permiso antes de tocar la base.");
Check(denied.Actions.Contains("Agregar"), "El censo pide permiso de agregar.");

Console.WriteLine($"OK: {checks} comprobaciones del censo de racks; sin conexiones a BD ni servidor.");

sealed class DeniedPermissions : IModulePermissionService
{
    public int Calls { get; private set; }
    public HashSet<string> Actions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Task<bool> HasFullAccessAsync(string username, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<IReadOnlyList<ModuloPermisoDto>> GetPermissionsAsync(string username, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuloPermisoDto>>(Array.Empty<ModuloPermisoDto>());
    public Task<bool> HasPermissionAsync(string username, int moduleId, string action, CancellationToken cancellationToken = default)
    { Calls++; Actions.Add(action); return Task.FromResult(false); }
}
