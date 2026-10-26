using System.Text.Json;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

// Regresiones de las reglas del Tablero del inicio. Sin servidor ni conexiones a bases de datos.
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");

// ── Decisiones con Eduardo (26-oct) ────────────────────────────────────────────

Equal(7, HomeSummaryRules.OldYardDays, "El patio marca como viejas las cajas de más de siete días");
Equal(7, HomeSummaryRules.TakenWindowDays, "Mis tomadas cuenta la actividad de los últimos siete días");
int cacheSeconds = HomeSummaryRules.CacheSeconds;
Check(cacheSeconds is >= 10 and <= 120, "La caché del servidor es de segundos, no de minutos");

// ── Excepciones: solo existen si hay casos ───────────────────────────────────

Check(HomeSummaryRules.ExceptionOf(TraceSignal.Atencion, HomeExceptionCode.OldInYard, 0) is null, "Sin casos no hay excepción");
Check(HomeSummaryRules.ExceptionOf(TraceSignal.Atencion, HomeExceptionCode.OldInYard, -3) is null, "Un conteo negativo no genera excepción");
HomeStationException? old = HomeSummaryRules.ExceptionOf(TraceSignal.Atencion, HomeExceptionCode.OldInYard, 103);
Check(old is not null, "Con casos hay excepción");
Equal(TraceSignal.Atencion, old!.Kind, "La caja vieja pide atención");
Equal(HomeExceptionCode.OldInYard, old.Code, "Código de la excepción");
Equal(103, old.Count, "Cuántas");
Equal(int.MaxValue, HomeSummaryRules.ToCount(long.MaxValue), "Un conteo enorme no desborda");
Equal(0, HomeSummaryRules.ToCount(-1), "Un conteo negativo se lee como cero");

HomeStationSummary summary = HomeSummaryRules.Summary(164, old);
Equal(164, summary.Count, "La cifra");
Check(ReferenceEquals(old, summary.Exception), "La excepción viaja con la cifra");
Check(HomeSummaryRules.Summary(149).Exception is null, "Entrada no tiene excepción");

// ── Qué módulos dan acceso a cada estación ───────────────────────────────────

Equal(1, HomeSummaryRules.ModulesOf(TraceStationId.Caseta).Count, "Caseta tiene un módulo");
Check(HomeSummaryRules.ModulesOf(TraceStationId.Caseta).Contains(MobileModuleIds.Gate), "Caseta: Gate");
Check(HomeSummaryRules.ModulesOf(TraceStationId.Entrada).Contains(MobileModuleIds.WarehouseOperations), "Entrada: operaciones de bodega");
Check(HomeSummaryRules.ModulesOf(TraceStationId.Revision).Contains(MobileModuleIds.Review), "Revisión: Review");
Check(HomeSummaryRules.ModulesOf(TraceStationId.Relacion).Contains(MobileModuleIds.WarehouseRelation), "Relación: relación de bodega");
Check(HomeSummaryRules.ModulesOf(TraceStationId.Salida).Contains(MobileModuleIds.GateRelationExit), "Salida: con relación");
Check(HomeSummaryRules.ModulesOf(TraceStationId.Salida).Contains(MobileModuleIds.GatePhysicalExit), "Salida: física");
Check(HomeSummaryRules.ModulesOf("OTRA").Count == 0, "Una estación desconocida no abre nada");
foreach (string station in TraceStationId.Order)
    Check(HomeSummaryRules.ModulesOf(station).Count > 0, $"{station} tiene al menos un módulo");

// ── Estatus de la lista de Relación ──────────────────────────────────────────

Equal(8, HomeSummaryRules.SupervisionStatuses.Count, "La lista del supervisor tiene ocho estatus");
Check(!HomeSummaryRules.SupervisionStatuses.Contains("CARGANDO"), "CARGANDO sigue en manos de Bodega: no es del supervisor");
Check(!HomeSummaryRules.SupervisionStatuses.Contains("AUTORIZADA"), "Lo autorizado ya no está por supervisar");
Check(HomeSummaryRules.SupervisionStatuses.Contains("CARGADA"), "CARGADA espera autorización");
Check(HomeSummaryRules.BlockedStatuses.All(HomeSummaryRules.SupervisionStatuses.Contains), "Lo bloqueado sale de la misma lista");
Check(!HomeSummaryRules.BlockedStatuses.Contains("CARGADA"), "CARGADA no es un bloqueo");
Check(!HomeSummaryRules.BlockedStatuses.Contains("EN ESPERA"), "EN ESPERA no es un bloqueo");

// ── El contrato con la app (los nombres JSON no cambian sin avisar) ─────────────

var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var result = new HomeSummaryResult
{
    Caseta = HomeSummaryRules.Summary(164, HomeSummaryRules.ExceptionOf(TraceSignal.Atencion, HomeExceptionCode.OldInYard, 103)),
    Entrada = HomeSummaryRules.Summary(149),
    Revision = null,
    Relacion = HomeSummaryRules.Summary(2),
    Salida = HomeSummaryRules.Summary(47, HomeSummaryRules.ExceptionOf(TraceSignal.Listo, HomeExceptionCode.ReadyToExit, 11)),
    CacheSeconds = HomeSummaryRules.CacheSeconds
};
using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result, web));
JsonElement root = doc.RootElement;
Check(root.GetProperty("success").GetBoolean(), "success");
Equal(164, root.GetProperty("caseta").GetProperty("count").GetInt32(), "caseta.count");
JsonElement ex = root.GetProperty("caseta").GetProperty("exception");
Equal("ATENCION", ex.GetProperty("kind").GetString(), "caseta.exception.kind");
Equal("OLD_IN_YARD", ex.GetProperty("code").GetString(), "caseta.exception.code");
Equal(103, ex.GetProperty("count").GetInt32(), "caseta.exception.count");
Equal(149, root.GetProperty("entrada").GetProperty("count").GetInt32(), "entrada.count");
Equal(JsonValueKind.Null, root.GetProperty("entrada").GetProperty("exception").ValueKind, "entrada.exception es null");
Equal(JsonValueKind.Null, root.GetProperty("revision").ValueKind, "revision es null si no se ve o falla");
Equal(2, root.GetProperty("relacion").GetProperty("count").GetInt32(), "relacion.count");
Equal("LISTO", root.GetProperty("salida").GetProperty("exception").GetProperty("kind").GetString(), "salida.exception.kind");
Equal("LISTAS_PARA_SALIR", root.GetProperty("salida").GetProperty("exception").GetProperty("code").GetString(), "salida.exception.code");
Equal(30, root.GetProperty("cacheSeconds").GetInt32(), "cacheSeconds");

// Los códigos de excepción son los mismos que espera la app.
Equal("OLD_IN_YARD", HomeExceptionCode.OldInYard, "OLD_IN_YARD");
Equal("DISCREPANCIAS", HomeExceptionCode.Discrepancies, "DISCREPANCIAS");
Equal("BLOQUEADAS", HomeExceptionCode.Blocked, "BLOQUEADAS");
Equal("LISTAS_PARA_SALIR", HomeExceptionCode.ReadyToExit, "LISTAS_PARA_SALIR");

Console.WriteLine($"HomeSummary.Regression: {checks} comprobaciones correctas.");
