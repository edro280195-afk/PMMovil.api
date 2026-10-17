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

// Regresiones sin servidor, fotos productivas ni conexiones a bases de datos.
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

LabelCaptureSnapshot before = new()
{
    NumLote = "L-001, L-002", NumSer = "S-17", Marca = "SKF", Modelo = "Z-1", CPaisOrigen = "THA",
    HazmatUn = "1866", HazmatClase = "3", HazmatGrupo = "III"
};
ConfirmLabelCaptureRequest request = new()
{
    CaptureId = Guid.Parse("3583d20a-27cb-4af6-9f59-3e840ba7b8ae"), CrefAgrupada = "01C260000000000", IdRevdet = 7,
    ExpectedSnapshot = before, Fields = new() { NumLote = "L-003" }, PhotoBase64 = "placeholder"
};

Check(LabelCaptureRules.AppendLots("A\r\nB, A", "B; C") == "A, B, C", "Conservar varios lotes y quitar duplicados.");
Check(LabelCaptureRules.AppendLots(" AB-01 ", "ab-01") == "AB-01", "Conservar la escritura original de un lote repetido.");
Check(LabelCaptureRules.AppendLots("01.2-A", "01.2A") == "01.2-A, 01.2A", "Los signos internos distinguen lotes.");
LabelCaptureSnapshot partial = LabelCaptureRules.Apply(before, request.Fields, new() { Un = "UN 3082" }, false);
Check(partial.NumLote == "L-001, L-002, L-003", "La captura añade lote sin perder los anteriores.");
Check(partial.HazmatUn == "3082" && partial.HazmatClase == "3" && partial.HazmatGrupo == "III", "UN parcial conserva clase y grupo.");
Check(partial.Marca == "SKF" && partial.NumSer == "S-17", "Campos ausentes preservan datos.");
Check(!partial.Revisada && before.NumLote == "L-001, L-002", "Guardar avance no revisa ni modifica el snapshot original.");
Check(LabelCaptureRules.Apply(before, new(), null, true).Revisada, "Confirmación física permite revisar.");
Check(LabelCaptureRules.Apply(before with { Revisada = true }, new(), null, false).Revisada, "Avance no reabre una partida.");
Check(LabelCaptureRules.SnapshotsMatch(before, before with { NumLote = " L-001, L-002 " }), "Relleno externo no genera conflicto falso.");
Check(LabelCaptureRules.SnapshotsMatch(new(), new() { NumLote = "", HazmatGrupo = "" }), "NULL y vacío legacy son equivalentes.");
foreach (LabelCaptureSnapshot changed in new[]
{
    before with { NumLote = "X" }, before with { NumSer = "X" }, before with { Marca = "X" }, before with { Modelo = "X" },
    before with { CPaisOrigen = "USA" }, before with { Revisada = true }, before with { HazmatUn = "3082" },
    before with { HazmatClase = "8" }, before with { HazmatGrupo = "II" }
}) Check(!LabelCaptureRules.SnapshotsMatch(before, changed), "Cada dato cotejado detecta cambios concurrentes.");

LabelCaptureRules.Validate(request);
checks++;
Reject(() => LabelCaptureRules.Validate(request with { MarkReviewed = true }), "No revisar por defecto sin confirmación física.");
LabelCaptureRules.Validate(request with { MarkReviewed = true, PhysicalReviewConfirmed = true });
checks++;
Reject(() => LabelCaptureRules.Validate(request with { CaptureId = Guid.Empty }), "Se requiere UUID.");
Reject(() => LabelCaptureRules.Validate(request with { IdRevdet = 0 }), "Se requiere partida.");
Reject(() => LabelCaptureRules.Validate(request with { CrefAgrupada = "../ruta" }), "Referencia no puede ser una ruta.");
Reject(() => LabelCaptureRules.Validate(request with { ExpectedSnapshot = null }), "El snapshot es obligatorio.");
Reject(() => LabelCaptureRules.Validate(request with { Fields = new() { NumLote = " " } }), "No hay borrado implícito por blanco.");
Reject(() => LabelCaptureRules.Validate(request with { Fields = new() { NumSer = "A\nB" } }), "No reintroducir saltos de línea.");
Reject(() => LabelCaptureRules.Validate(request with { Fields = new() { CPaisOrigen = "THAILAND" } }), "País debe ser código de catálogo.");
foreach (string invalid in new[] { "L", "IV", "", "1" })
    Reject(() => LabelCaptureRules.ValidateHazmat(null, null, invalid), "Grupo fuera de I/II/III rechazado.");
foreach (string valid in new[] { "I", "ii", " III " }) { LabelCaptureRules.ValidateHazmat("UN1866", "3", valid); checks++; }
Reject(() => LabelCaptureRules.ValidateHazmat("186", null, null), "UN incompleto rechazado.");
Reject(() => LabelCaptureRules.ValidateHazmat("1866 3 III", null, null), "UN no mezcla clase/grupo.");
Reject(() => LabelCaptureRules.ValidateHazmat(null, "LIQUIDO", null), "Clase textual no válida.");
Reject(() => LabelCaptureRules.Validate(request with { RawText = new('x', 24001) }), "Texto de auditoría acotado.");
Reject(() => LabelCaptureRules.Validate(request with { RawCodes = Enumerable.Repeat("X", 101).ToArray() }), "Lista de códigos acotada.");
Reject(() => LabelCaptureRules.Validate(request with { Observations = new[] { new LabelCaptureObservation { Field = null!, Source = "OCR" } } }), "Observación nula rechazada sin excepción inesperada.");

string fingerprint = LabelCaptureRules.Fingerprint(request, "PHOTO-1");
Check(fingerprint == LabelCaptureRules.Fingerprint(request with { PhotoBase64 = "otro envoltorio del mismo binario" }, "PHOTO-1"), "Idempotencia usa hash binario sin guardar base64.");
Check(fingerprint != LabelCaptureRules.Fingerprint(request, "PHOTO-2"), "Cambiar imagen invalida el replay.");
foreach (ConfirmLabelCaptureRequest changed in new[]
{
    request with { Fields = new() { NumLote = "L-004" } }, request with { IdRevdet = 8 },
    request with { RawText = "otra lectura" }, request with { MarkReviewed = true, PhysicalReviewConfirmed = true },
    request with { ExpectedSnapshot = before with { Marca = "NUEVA" } },
    request with { Observations = new[] { new LabelCaptureObservation { Field = "lote", Value = "L-003", Source = "GS1", Accepted = true } } }
}) Check(fingerprint != LabelCaptureRules.Fingerprint(changed, "PHOTO-1"), "El replay no admite payload cambiado.");

JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
string json = JsonSerializer.Serialize(request, jsonOptions);
Check(json.Contains("\"cPaisOrigen\""), "Nombre de país coincide con Android.");
Check(json.Contains("\"idRevdet\":7") && json.Contains("\"expectedSnapshot\""), "Contrato JSON estable.");
Check(JsonSerializer.Deserialize<ConfirmLabelCaptureRequest>(json, jsonOptions)!.ExpectedSnapshot == before, "Contrato conserva snapshot al serializar.");
Type controller = typeof(LabelCapturesController);
Check(controller.GetCustomAttribute<AuthorizeAttribute>() is not null, "Endpoint autenticado.");
Check(controller.GetCustomAttribute<RouteAttribute>()!.Template == "api/Revision/label-captures", "Ruta acordada.");
Check(controller.GetMethod("Confirm")!.GetCustomAttribute<RequiereModuloAttribute>() is not null, "Confirmación exige permiso explícito de modificar.");
Check(controller.GetMethod("Confirm")!.GetCustomAttribute<HttpPostAttribute>()!.Template == "confirm", "POST confirm disponible.");
Check(controller.GetMethod("Context")!.GetCustomAttribute<HttpGetAttribute>()!.Template == "context/{idRevdet:int}", "GET context disponible.");

IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{ ["PhotoSettings:BasePath"] = Path.Combine(Path.GetTempPath(), "pmm-label-regression-no-write") }).Build();
DeniedPermissions denied = new();
LabelCaptureService service = new(new BaseRepository(configuration), configuration, denied, null!, null!, null!, NullLogger<LabelCaptureService>.Instance);
LabelCaptureResult invalidResult = await service.ConfirmAsync(request with { MarkReviewed = true }, "tester", default);
Check(invalidResult.StatusCode == 400 && invalidResult.Body.Code == "INVALID_CAPTURE" && denied.Calls == 0, "Validación anterior a permisos/BD/fotos.");
LabelCaptureResult deniedResult = await service.ConfirmAsync(request, "tester", default);
Check(deniedResult.StatusCode == 403 && deniedResult.Body.Code == "PERMISSION_DENIED", "Permiso denegado sin abrir BD ni decodificar imagen.");
Check(!Directory.Exists(configuration["PhotoSettings:BasePath"]), "Pruebas negativas no crean archivos.");

Console.WriteLine($"OK: {checks} comprobaciones de captura segura; sin conexiones a BD ni servidor.");

sealed class DeniedPermissions : IModulePermissionService
{
    public int Calls { get; private set; }
    public Task<bool> HasFullAccessAsync(string username, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<IReadOnlyList<ModuloPermisoDto>> GetPermissionsAsync(string username, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuloPermisoDto>>(Array.Empty<ModuloPermisoDto>());
    public Task<bool> HasPermissionAsync(string username, int moduleId, string action, CancellationToken cancellationToken = default)
    { Calls++; return Task.FromResult(false); }
}
