using PMMovil.Api.Models;
using PMMovil.Api.Services;

// Regresiones de las reglas de la ficha de un código. Sin servidor ni conexiones a bases de datos.
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");
void Sequence(IEnumerable<string> expected, IEnumerable<string> actual, string message)
    => Check(expected.SequenceEqual(actual), $"{message} (esperado: [{string.Join(", ", expected)}], real: [{string.Join(", ", actual)}])");

string StateOf(TraceFacts facts, string station)
    => TrazabilidadRules.BuildStations(facts).Single(item => item.Id == station).State;

TraceFacts Facts(Action<TraceFacts> setup)
{
    var facts = new TraceFacts { Kind = TraceKind.Caja };
    setup(facts);
    return facts;
}

DateTime arrived = new(2026, 10, 20, 8, 30, 0);
DateTime left = new(2026, 10, 22, 17, 5, 0);

// ── Reconocer el código ─────────────────────────────────────────────────────

// Caja: la diagonal es parte del folio y el cero de la plantilla vieja puede estar o no.
TraceParsedCode caja = TrazabilidadRules.Parse("E-017846/26");
Equal(TraceKind.Caja, caja.Kind, "E-017846/26 es una caja");
Equal("E-017846/26", caja.Code, "El código se conserva tal cual");
Sequence(new[] { "E-017846/26", "E-17846/26" }, caja.CasetaIdCandidates, "Con cero se busca también sin cero, el escaneado primero");
Sequence(new[] { "E-17846/26", "E-017846/26" }, TrazabilidadRules.Parse("E-17846/26").CasetaIdCandidates, "Sin cero se busca también con cero, el escaneado primero");
Sequence(new[] { "E-7846/26", "E-07846/26" }, TrazabilidadRules.Parse("E-7846/26").CasetaIdCandidates, "Folio corto: mismas dos formas");
Check(caja.CasetaIdCandidates.All(id => id.Contains('/') && id.EndsWith("/26")), "Todas las formas conservan la diagonal y el año");
Equal("E-017846/26", TrazabilidadRules.Parse(" e-017846/26\r\n").Code, "Espacios, controles y minúsculas se normalizan");
Equal("E-017846/26", TrazabilidadRules.Parse("E-017846%2F26").Code, "La diagonal escapada se deshace");
Equal("E-017846/26", TrazabilidadRules.Parse("e-017846%2f26").Code, "También en minúsculas");
Equal("E-017846/26", TrazabilidadRules.Parse("E-017846/26").Code, "Normalizar es idempotente");
Check(!TrazabilidadRules.Parse("E-017846/2").IsRecognized, "El año necesita dos dígitos");
Check(!TrazabilidadRules.Parse("E-017846").IsRecognized, "Sin año no es un folio");
Check(!TrazabilidadRules.Parse("E-17846/26/1").IsRecognized, "Otra diagonal no es un folio");
Check(!TrazabilidadRules.Parse("E-1/26").IsRecognized, "Menos de tres dígitos no es un folio");

// Relación: el tercer carácter decide la tabla.
TraceParsedCode relacion = TrazabilidadRules.Parse("01I26104526");
Equal(TraceKind.Relacion, relacion.Kind, "01I26104526 es una relación");
Equal("relacion", TrazabilidadRules.RelationTable("01I26104526"), "I va a relacion");
Equal("relacion", TrazabilidadRules.RelationTable("01E26104526"), "E va a relacion");
Equal("relacion_imex", TrazabilidadRules.RelationTable("01C26104526"), "C va a relacion_imex");
Equal(TraceKind.Relacion, TrazabilidadRules.Parse("01C26104526").Kind, "01C26104526 también es una relación");

// Carpeta y bulto.
TraceParsedCode carpeta = TrazabilidadRules.Parse("01C263582800000");
Equal(TraceKind.Carpeta, carpeta.Kind, "Una referencia de 15 caracteres es una carpeta");
Equal("01C263582800000", carpeta.Reference, "La carpeta guarda su referencia");
TraceParsedCode bulto = TrazabilidadRules.Parse("01c263582800000 3-12");
Equal(TraceKind.Bulto, bulto.Kind, "Referencia + posición es un bulto");
Equal(3, bulto.Ordinal, "Posición del bulto");
Equal(12, bulto.Total, "Total del bulto");
Equal("01C263582800000 3-12", bulto.Code, "El bulto se normaliza a mayúsculas");
Check(!TrazabilidadRules.Parse("01C263582800000 13-12").IsRecognized, "Posición mayor al total es una lectura mutilada");
Check(!TrazabilidadRules.Parse("01C263582800000 0-5").IsRecognized, "Posición cero no existe");
Check(!TrazabilidadRules.Parse("01C263582800000 3-0").IsRecognized, "Total cero no existe");
Check(!TrazabilidadRules.Parse("01C2635828 3-12").IsRecognized, "La referencia del bulto debe ser de 15 caracteres");

// Pre-registro: lleva token; se conserva la capitalización y no se devuelve.
TraceParsedCode pre = TrazabilidadRules.Parse("PMMPR|7K2Q-4481|a1B2c3");
Equal(TraceKind.PreRegistro, pre.Kind, "PMMPR|folio|token es un pre-registro");
Equal("7K2Q-4481", pre.PreFolio, "Folio del pre-registro");
Equal("a1B2c3", pre.PreToken, "El token conserva mayúsculas y minúsculas");
TraceParsedCode preLower = TrazabilidadRules.Parse("  pmmpr|7K2Q-4481|a1B2c3\r\n");
Equal(TraceKind.PreRegistro, preLower.Kind, "El prefijo se acepta en minúsculas");
Equal("a1B2c3", preLower.PreToken, "Aun así el token conserva su capitalización");
Equal("PMMPR|7K2Q-4481|a1B2c3", preLower.Code, "El prefijo queda normalizado");
Check(!TrazabilidadRules.Parse("PMMPR|7K2Q-4481").IsRecognized, "Sin token no hay pre-registro que consultar");
Check(!TrazabilidadRules.Parse("PMMPR|7K2Q|a|b").IsRecognized, "Más de tres partes no es un pre-registro");
Check(!TrazabilidadRules.Parse("PMMPR||token").IsRecognized, "Folio vacío");

// Gafete: jamás se consulta ni se devuelve su valor.
TraceParsedCode gafete = TrazabilidadRules.Parse("juan.perez;Secreto123");
Equal(TraceReason.BadgeNotLookedUp, gafete.Rejection, "El gafete se rechaza");
Check(gafete.Code.Length == 0 && gafete.PreToken.Length == 0 && gafete.PreFolio.Length == 0 && gafete.Reference.Length == 0,
    "El resultado del gafete no conserva nada de su valor");
Equal(TraceReason.BadgeNotLookedUp, TrazabilidadRules.Parse("E-017846/26;x").Rejection, "Un ';' basta para tratarlo como gafete");

// Lo que no es de PMM.
Equal(TraceReason.Empty, TrazabilidadRules.Parse("   ").Rejection, "Vacío");
Equal(TraceReason.Empty, TrazabilidadRules.Parse(null).Rejection, "Nulo");
Equal(TraceReason.Unsupported, TrazabilidadRules.Parse("+30S77120045").Rejection, "Etiqueta de producto del cliente: no es de la ficha");
Equal(TraceReason.Unsupported, TrazabilidadRules.Parse("HOLA").Rejection, "Texto cualquiera");
Equal(TraceReason.Unsupported, TrazabilidadRules.Parse(new string('A', 121)).Rejection, "Demasiado largo");

// ── Token del pre-registro ──────────────────────────────────────────────────

Check(TrazabilidadService.TokenMatches("a1B2c3", "a1B2c3"), "Token igual");
Check(!TrazabilidadService.TokenMatches("a1B2c3", "a1b2c3"), "El token distingue mayúsculas");
Check(!TrazabilidadService.TokenMatches("a1B2c3", "a1B2c"), "Token de otra longitud");
Check(!TrazabilidadService.TokenMatches("a1B2c3", "a1B2c4"), "Token distinto");

// ── Fechas y relación ───────────────────────────────────────────────────────

Equal<string?>(null, TrazabilidadRules.FormatDate(null), "Sin fecha");
Equal<string?>(null, TrazabilidadRules.FormatDate(new DateTime(1, 1, 1)), "Fecha en cero del sistema viejo");
Equal<string?>("2026-10-20T08:30:00", TrazabilidadRules.FormatDate(arrived), "Fecha real");
Check(!TrazabilidadRules.HasRelationValue(""), "Relación vacía");
Check(!TrazabilidadRules.HasRelationValue("  "), "Relación en blanco");
Check(!TrazabilidadRules.HasRelationValue("NO_RELACION"), "Marcador de sin relación");
Check(TrazabilidadRules.HasRelationValue("01I26104526"), "Relación real");

// ── Estaciones, banderas, señal y acciones ──────────────────────────────────

// Caja recién llegada, sin relación: hoy Bodega no la liga a su entrada, así que NO se afirma «sin entrada».
TraceFacts fresh = Facts(f => { f.HasCaja = true; f.CajaArrival = arrived; });
Equal(TraceState.Hecha, StateOf(fresh, TraceStationId.Caseta), "Fresca: caseta hecha");
Equal(TraceState.SinDato, StateOf(fresh, TraceStationId.Entrada), "Fresca: entrada sin dato (no se adivina)");
Equal(TraceState.SinDato, StateOf(fresh, TraceStationId.Revision), "Fresca: revisión sin dato");
Equal(TraceState.Pendiente, StateOf(fresh, TraceStationId.Relacion), "Fresca: relación pendiente");
Equal(TraceState.Pendiente, StateOf(fresh, TraceStationId.Salida), "Fresca: salida pendiente");
Sequence(new[] { TraceFlag.SinEntradaVinculada, TraceFlag.SinRelacion }, TrazabilidadRules.BuildFlags(fresh), "Fresca: banderas");
Sequence(new[] { TraceAction.CreateEntry, TraceAction.FindInYard, TraceAction.ExitGate }, TrazabilidadRules.BuildActions(fresh, true), "Fresca: acciones");
Equal(TraceSignal.Aviso, TrazabilidadRules.BuildSignal(fresh, true), "Fresca: aviso");
Equal(5, TrazabilidadRules.BuildStations(fresh).Count, "Siempre son cinco estaciones");
Sequence(TraceStationId.Order, TrazabilidadRules.BuildStations(fresh).Select(item => item.Id), "En el orden de La Línea");

// Caja con relación autorizada y su entrada, completa, encontrada por la relación.
TraceFacts ready = Facts(f =>
{
    f.HasCaja = true; f.CajaArrival = arrived;
    f.HasRelacion = true; f.RelacionStatus = "AUTORIZADA";
    f.EntradasCount = 1; f.HasLinkByRelacion = true; f.EntradaArrival = arrived;
    f.ReviewStatuses = new[] { "COMPLETA" };
});
Equal(TraceState.Hecha, StateOf(ready, TraceStationId.Entrada), "Lista: entrada hecha");
Equal(TraceState.Hecha, StateOf(ready, TraceStationId.Revision), "Lista: revisión hecha");
Equal(TraceState.Hecha, StateOf(ready, TraceStationId.Relacion), "Lista: relación hecha");
Equal(TraceState.EnCurso, StateOf(ready, TraceStationId.Salida), "Lista: salida en curso");
Equal(TraceSignal.Listo, TrazabilidadRules.BuildSignal(ready, true), "Lista: señal LISTO");
Sequence(new[] { TraceFlag.EntradaPorRelacion }, TrazabilidadRules.BuildFlags(ready), "Lista: encontrada por la relación");
Sequence(
    new[] { TraceAction.ExitGate, TraceAction.OpenRelation, TraceAction.OpenEntry, TraceAction.ReviewEntry, TraceAction.FindInYard },
    TrazabilidadRules.BuildActions(ready, true), "Lista: la salida es la principal");

// Con dos entradas no se ofrece abrir «la» entrada: se elige de la lista.
TraceFacts two = Facts(f =>
{
    f.HasCaja = true; f.HasRelacion = true; f.RelacionStatus = "AUTORIZADA"; f.EntradasCount = 2; f.HasLinkByRelacion = true;
    f.ReviewStatuses = new[] { "COMPLETA", "COMPLETA" };
});
Check(!TrazabilidadRules.BuildActions(two, true).Contains(TraceAction.OpenEntry), "Dos entradas: no hay una entrada que abrir");

// Salió por relación.
TraceFacts gone = Facts(f =>
{
    f.HasCaja = true; f.CajaArrival = arrived; f.CajaDeparture = left;
    f.HasRelacion = true; f.RelacionStatus = "SALIDA"; f.RelacionDeparture = left;
    f.EntradasCount = 1; f.ReviewStatuses = new[] { "COMPLETA" };
});
Equal(TraceState.Hecha, StateOf(gone, TraceStationId.Salida), "Salió: salida hecha");
Equal(TraceSignal.Cerrado, TrazabilidadRules.BuildSignal(gone, true), "Salió: cerrado");
Sequence(new[] { TraceAction.OpenRelation, TraceAction.OpenEntry, TraceAction.ReviewEntry }, TrazabilidadRules.BuildActions(gone, true), "Salió: ya no se libera ni se busca en el patio");
Check(TrazabilidadRules.BuildFlags(gone).Count == 0, "Salió: sin banderas");

// Salida física sin relación (más de la mitad de las cajas): la relación no aplica.
TraceFacts physical = Facts(f => { f.HasCaja = true; f.CajaArrival = arrived; f.CajaDeparture = left; });
Equal(TraceState.NoAplica, StateOf(physical, TraceStationId.Relacion), "Salida física: la relación no aplica");
Equal(TraceState.Hecha, StateOf(physical, TraceStationId.Salida), "Salida física: salida hecha");
Equal(TraceSignal.Cerrado, TrazabilidadRules.BuildSignal(physical, true), "Salida física: cerrado");
Check(TrazabilidadRules.BuildActions(physical, true).Count == 0, "Salida física sin entrada: nada que hacer");

// Caja cancelada.
TraceFacts cancelled = Facts(f => { f.HasCaja = true; f.CajaCancelled = true; f.CajaArrival = arrived; });
Equal(TraceState.Atencion, StateOf(cancelled, TraceStationId.Caseta), "Cancelada: caseta pide atención");
Equal(TraceSignal.Alto, TrazabilidadRules.BuildSignal(cancelled, true), "Cancelada: alto");
Check(TrazabilidadRules.BuildFlags(cancelled).Contains(TraceFlag.Cancelada), "Cancelada: bandera");
Check(TrazabilidadRules.BuildActions(cancelled, true).Count == 0, "Cancelada: ninguna acción");

// Registro abierto: la relación ya salió y la caja sigue en el patio.
TraceFacts openRecord = Facts(f =>
{
    f.HasCaja = true; f.CajaArrival = arrived; f.HasRelacion = true; f.RelacionStatus = "SALIDA"; f.RelacionDeparture = left;
});
Equal(TraceState.Hecha, StateOf(openRecord, TraceStationId.Salida), "Registro abierto: la carga ya salió");
Check(TrazabilidadRules.BuildFlags(openRecord).Contains(TraceFlag.RegistroAbierto), "Registro abierto: bandera");
Equal(TraceSignal.Atencion, TrazabilidadRules.BuildSignal(openRecord, true), "Registro abierto: pide atención");

// Revisión.
Equal(TraceState.EnCurso, StateOf(Facts(f => { f.EntradasCount = 2; f.ReviewStatuses = new[] { "COMPLETA", "SIN_REVISAR" }; }), TraceStationId.Revision), "Una completa y una sin revisar: en curso");
Equal(TraceState.EnCurso, StateOf(Facts(f => { f.EntradasCount = 1; f.ReviewStatuses = new[] { "EN_PROGRESO" }; }), TraceStationId.Revision), "En progreso");
Equal(TraceState.Pendiente, StateOf(Facts(f => { f.EntradasCount = 1; f.ReviewStatuses = new[] { "SIN_REVISAR" }; }), TraceStationId.Revision), "Sin revisar: pendiente");
TraceFacts discrepancy = Facts(f => { f.EntradasCount = 1; f.ReviewStatuses = new[] { "COMPLETA_CON_DISCREPANCIAS" }; });
Equal(TraceState.Atencion, StateOf(discrepancy, TraceStationId.Revision), "Discrepancias: atención");
Check(TrazabilidadRules.BuildFlags(discrepancy).Contains(TraceFlag.Discrepancia), "Discrepancias: bandera");
Equal(TraceSignal.Atencion, TrazabilidadRules.BuildSignal(discrepancy, true), "Discrepancias: señal");

// Daño y hazmat.
TraceFacts damaged = Facts(f => { f.HasCaja = true; f.EntradasCount = 1; f.HasDamage = true; f.HasHazmat = true; f.ReviewStatuses = new[] { "COMPLETA" }; });
Sequence(new[] { TraceFlag.Danada, TraceFlag.Hazmat }, TrazabilidadRules.BuildFlags(damaged).Where(flag => flag is TraceFlag.Danada or TraceFlag.Hazmat), "Daño y hazmat");
Equal(TraceSignal.Atencion, TrazabilidadRules.BuildSignal(damaged, true), "Daño: atención");

// Relación ocupada y lista recortada.
TraceFacts busy = Facts(f => { f.Kind = TraceKind.Relacion; f.HasRelacion = true; f.RelacionStatus = "AUTORIZADA"; f.RelacionBusy = true; f.EntradasCount = 25; f.EntradasTruncated = true; f.ReviewStatuses = new[] { "COMPLETA" }; });
Check(TrazabilidadRules.BuildFlags(busy).Contains(TraceFlag.RelacionOcupada), "Relación ocupada: bandera");
Check(TrazabilidadRules.BuildFlags(busy).Contains(TraceFlag.ListaRecortada), "Lista recortada: bandera");
Equal(TraceSignal.Atencion, TrazabilidadRules.BuildSignal(busy, true), "Relación ocupada: atención");

// Relación sin entradas colgando.
TraceFacts relationOnly = Facts(f => { f.Kind = TraceKind.Relacion; f.HasRelacion = true; f.RelacionStatus = "CARGADA"; });
Equal(TraceState.EnCurso, StateOf(relationOnly, TraceStationId.Relacion), "Cargada: relación en curso");
Check(TrazabilidadRules.BuildFlags(relationOnly).Contains(TraceFlag.SinEntradaVinculada), "Relación sin entradas: bandera");
Sequence(new[] { TraceAction.OpenRelation, TraceAction.ExitGate }, TrazabilidadRules.BuildActions(relationOnly, true), "Relación sin autorizar: se abre, no se libera primero");

// Carpeta y bulto: la revisión pendiente sugiere revisar; terminada, abrir.
TraceFacts pendingReview = Facts(f => { f.Kind = TraceKind.Carpeta; f.EntradasCount = 1; f.ReviewStatuses = new[] { "EN_PROGRESO" }; });
Sequence(new[] { TraceAction.ReviewEntry, TraceAction.OpenEntry, TraceAction.ShowOnMap }, TrazabilidadRules.BuildActions(pendingReview, true), "Carpeta en revisión: revisar primero");
TraceFacts doneReview = Facts(f => { f.Kind = TraceKind.Bulto; f.EntradasCount = 1; f.ReviewStatuses = new[] { "COMPLETA" }; f.HasRelacion = true; f.RelacionStatus = "AUTORIZADA"; });
Sequence(new[] { TraceAction.OpenEntry, TraceAction.ReviewEntry, TraceAction.ShowOnMap, TraceAction.OpenRelation }, TrazabilidadRules.BuildActions(doneReview, true), "Bulto revisado: abrir primero");
Check(!TrazabilidadRules.BuildFlags(pendingReview).Contains(TraceFlag.SinEntradaVinculada), "Una carpeta es la entrada: no aplica «sin entrada»");

// Pre-registro.
TraceFacts valid = Facts(f => { f.Kind = TraceKind.PreRegistro; f.PreStatus = PreregistroEstatus.Vigente; });
Equal(TraceState.Pendiente, StateOf(valid, TraceStationId.Caseta), "Pre-registro vigente: caseta pendiente");
Equal(TraceSignal.Aviso, TrazabilidadRules.BuildSignal(valid, true), "Pre-registro vigente: aviso");
Sequence(new[] { TraceAction.RegisterPreRegistro }, TrazabilidadRules.BuildActions(valid, true), "Pre-registro vigente: registrar llegada");
TraceFacts expired = Facts(f => { f.Kind = TraceKind.PreRegistro; f.PreStatus = PreregistroEstatus.Expirado; });
Equal(TraceSignal.Alto, TrazabilidadRules.BuildSignal(expired, true), "Pre-registro vencido: alto");
Equal(TraceState.Atencion, StateOf(expired, TraceStationId.Caseta), "Pre-registro vencido: caseta pide atención");
Check(TrazabilidadRules.BuildActions(expired, true).Count == 0, "Pre-registro vencido: nada que registrar");
TraceFacts used = Facts(f => { f.Kind = TraceKind.PreRegistro; f.PreStatus = PreregistroEstatus.Usado; f.HasCaja = true; f.CajaArrival = arrived; });
Equal(TraceState.Hecha, StateOf(used, TraceStationId.Caseta), "Pre-registro usado: sigue la cadena de su caja");
Sequence(new[] { TraceAction.FindInYard }, TrazabilidadRules.BuildActions(used, true), "Pre-registro usado: buscar la caja en el patio");

// Lo que no existe.
Equal(TraceSignal.SinDato, TrazabilidadRules.BuildSignal(fresh, false), "No encontrado: sin dato");
Check(TrazabilidadRules.BuildActions(fresh, false).Count == 0, "No encontrado: sin acciones");

// Nombres de acción: son los mismos de ScanActionId en la app (CreateEntry → CREATE_ENTRY…).
Sequence(
    new[] { "CREATE_ENTRY", "EXIT_GATE", "FIND_IN_YARD", "OPEN_RELATION", "OPEN_ENTRY", "REVIEW_ENTRY", "SHOW_ON_MAP", "REGISTER_PREREGISTRO" },
    new[]
    {
        TraceAction.CreateEntry, TraceAction.ExitGate, TraceAction.FindInYard, TraceAction.OpenRelation,
        TraceAction.OpenEntry, TraceAction.ReviewEntry, TraceAction.ShowOnMap, TraceAction.RegisterPreRegistro
    },
    "Los nombres de las acciones no cambian sin avisar a la app");

Console.WriteLine($"Trazabilidad.Regression: {checks} comprobaciones correctas.");
