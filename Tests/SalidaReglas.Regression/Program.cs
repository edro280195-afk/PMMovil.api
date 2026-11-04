using PMMovil.Api.Services;

// Regresiones de las reglas de la carga de mercancía (Salida de bodega) contra el comportamiento del legacy
// (Reportes.Business\Relacion.cs). Sin servidor ni base de datos.
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");

DateTime t0 = new(2026, 11, 4, 10, 0, 0);
EtiquetaRafaga R(string texto, int segundos, string? manejo = "I", string? hist = "ESCANEO x , ")
    => new(texto, manejo, t0.AddSeconds(segundos), hist);

// ── Clientes exentos: igualdad exacta e incluye 2901 (SURFAX) ──────────────────

Check(SalidaReglas.EsClienteExento("2901", SalidaReglas.ClientesExentosRafaga), "2901 está exento de la ráfaga");
Check(SalidaReglas.EsClienteExento("2901", SalidaReglas.ClientesExentosTiempoFinal), "2901 está exento de la revisión final");
Check(SalidaReglas.EsClienteExento("CL7467", SalidaReglas.ClientesExentosRafaga), "CL7467 está exento de la ráfaga");
Check(!SalidaReglas.EsClienteExento("CL7467", SalidaReglas.ClientesExentosTiempoFinal), "CL7467 NO está en la lista de la revisión final del legacy");
Check(SalidaReglas.EsClienteExento("1234,2944", SalidaReglas.ClientesExentosRafaga), "Varios clientes: basta uno exento");
Check(!SalidaReglas.EsClienteExento("29441", SalidaReglas.ClientesExentosRafaga), "Coincidencia exacta, no por texto");
Check(!SalidaReglas.EsClienteExento("", SalidaReglas.ClientesExentosRafaga), "Sin cliente no hay exención");
Check(!SalidaReglas.EsClienteExento(null, SalidaReglas.ClientesExentosRafaga), "Cliente nulo no está exento");

// ── Ráfaga: más de 1 bulto en 20 s (más de 3 en exentos) ───────────────────

Check(SalidaReglas.DetectarRafaga(new[] { R("A", 0), R("B", 60) }, "9999") is null, "Dos bultos con un minuto de separación no bloquean");
var bloque = SalidaReglas.DetectarRafaga(new[] { R("A", 0), R("B", 5) }, "9999");
Check(bloque is not null && bloque.Count == 2, "Dos bultos en 5 s bloquean a un cliente normal");
Check(SalidaReglas.DetectarRafaga(new[] { R("A", 0), R("B", 5), R("C", 10) }, "2901") is null, "Un cliente exento tolera tres bultos seguidos");
Check(SalidaReglas.DetectarRafaga(new[] { R("A", 0), R("B", 5), R("C", 10), R("D", 15) }, "2901") is not null, "El cuarto bulto seguido sí bloquea al exento");
Check(SalidaReglas.DetectarRafaga(new[] { R("A", 0), R("B", 25) }, "9999") is null, "Más de 20 s entre bultos rompe la ráfaga");
Check(SalidaReglas.DetectarRafaga(new[] { R("A", 0, "PAQ/M"), R("B", 2, "PAQ/M") }, "9999") is null, "PAQ/M no cuenta");
Check(SalidaReglas.DetectarRafaga(new[] { R("A", 0, "T"), R("B", 2, "T") }, "9999") is null, "Transbordo con historial no cuenta");
Check(SalidaReglas.DetectarRafaga(new[] { R("A", 0, "I", "BLOQUEO u 2026 , DESBLOQUEO s 2026 , "), R("B", 5, "I", "BLOQUEO u 2026 , DESBLOQUEO s 2026 , ") }, "9999") is null,
    "Etiquetas ya desbloqueadas no vuelven a bloquear");
Check(SalidaReglas.DetectarRafaga(new[] { R("A", 0, "I", "BLOQUEO u 2026 , DESBLOQUEO s 2026 , ESCANEO 2026 , "), R("B", 5, "I", "BLOQUEO u 2026 , DESBLOQUEO s 2026 , ESCANEO 2026 , ") }, "9999") is not null,
    "Reescaneadas después del desbloqueo sí bloquean");

// ── Revisión final de tiempo: literal del legacy (count == 1) ─────────────

EtiquetaTiempo T(int segundos, string? manejo = "I") => new(manejo, t0.AddSeconds(segundos));

Check(SalidaReglas.EsCargaDemasiadoRapida(new[] { T(0) }, "9999"), "Un solo bulto de un cliente normal sale DEMASIADO RAPIDO (legacy, count == 1)");
Check(!SalidaReglas.EsCargaDemasiadoRapida(new[] { T(0) }, "2901"), "Un solo bulto de un cliente exento no");
Check(!SalidaReglas.EsCargaDemasiadoRapida(new[] { T(0), T(5), T(10) }, "9999"), "Tres bultos NO marcan DEMASIADO RAPIDO (legacy)");
Check(!SalidaReglas.EsCargaDemasiadoRapida(new[] { T(0), T(600) }, "9999"), "Dos bultos con diez minutos no");
Check(!SalidaReglas.EsCargaDemasiadoRapida(new[] { T(0, "PAQ/M"), T(5, "PAQ/M") }, "9999"), "Solo PAQ/M: no hay bultos que contar");
Check(SalidaReglas.EsCargaDemasiadoRapida(new[] { T(0), T(10, "PAQ/M") }, "9999"), "1 bulto normal + 1 PAQ/M en 10 s: promedio 10 s con count == 1");
Check(!SalidaReglas.EsCargaDemasiadoRapida(new[] { T(0), T(120, "PAQ/M") }, "9999"), "1 bulto normal + PAQ/M a 2 min: promedio 120 s");
Check(SalidaReglas.EsCargaDemasiadoRapida(new[] { T(0) }, "CL7467"), "CL7467 no está exento en la revisión final");
Check(!SalidaReglas.EsCargaDemasiadoRapida(Array.Empty<EtiquetaTiempo>(), "9999"), "Sin etiquetas no hay carga rápida");
SalidaReglas.EsCargaDemasiadoRapida(new[] { T(0), T(30, "PAQ/M") }, "9999", out DateTime ini, out DateTime fin, out int cant);
Equal(t0, ini, "Inicio = primera hora real");
Equal(t0.AddSeconds(30), fin, "Fin = última hora real");
Equal(1, cant, "Cantidad = bultos que no son PAQ/M");

// ── Historial de bloqueo ──────────────────────────────────────────────────

Check(SalidaReglas.RequiereBloqueoPorHistorial(null), "Sin historial se bloquea");
Check(SalidaReglas.RequiereBloqueoPorHistorial("ESCANEO x , "), "Sin desbloqueo previo se bloquea");
Check(!SalidaReglas.RequiereBloqueoPorHistorial("BLOQUEO a , DESBLOQUEO b , "), "Desbloqueada y sin reescaneo no se bloquea");
Check(SalidaReglas.RequiereBloqueoPorHistorial("BLOQUEO a , DESBLOQUEO b , ESCANEO c , "), "Desbloqueada y reescaneada se bloquea");

// ── Estados ──────────────────────────────────────────────────────────────

Check(SalidaReglas.EstadoPermiteCaptura("COMPLETA"), "COMPLETA permite capturar");
Check(SalidaReglas.EstadoPermiteCaptura("DIFERENCIA EN BULTOS-ETIQUETAS"), "DIFERENCIA permite capturar (el legacy deja escanear)");
Check(SalidaReglas.EstadoPermiteCaptura("NO DISP ETIQUETAS EN MCIA"), "NO DISP permite pedir autorización");
Check(!SalidaReglas.EstadoPermiteCaptura("INFORMACION INCORRECTA EN RELACION, NOTIFICAR A ENCARGADO"), "INFORMACION INCORRECTA no permite capturar");
Check(!SalidaReglas.EstadoPermiteCaptura("EN PROCESO"), "EN PROCESO no permite capturar");
Check(!SalidaReglas.EstadoPermiteCaptura(null), "Sin estado no permite capturar");
Check(SalidaReglas.EsEstadoBloqueo("BLOQUEO REIMPRESION") && SalidaReglas.EsEstadoBloqueo("BLOQUEO") && !SalidaReglas.EsEstadoBloqueo("EN ESPERA"), "Estados de bloqueo");
Check(SalidaReglas.EsEstadoCargaCerrada("DEMASIADO RAPIDO") && SalidaReglas.EsEstadoCargaCerrada("SALIDA") && !SalidaReglas.EsEstadoCargaCerrada("CARGANDO"), "Estados cerrados");

// ── Autorizar: mismas condiciones que el botón del legacy ──────────────────

string? Aut(string estatus = "COMPLETA", string carga = "CARGADA", string cargo = "operador", string usuario = "supervisor",
    bool ocupada = false, bool ajenas = false, bool error = false)
    => SalidaReglas.MotivoNoSePuedeAutorizar(estatus, carga, cargo, usuario, ocupada, ajenas, error);

Check(Aut() is null, "Una relación cargada por otro se puede autorizar");
Equal("NECESITA AUTORIZAR OTRO ENCARGADO", Aut(cargo: "Supervisor"), "No se autoriza lo que uno mismo cargó (sin importar mayúsculas)");
Check(Aut(carga: "", cargo: "NO REQUERIDO", usuario: "NO REQUERIDO") is null, "NO REQUERIDO se autoriza sin carga");
Check(Aut(carga: "", cargo: "NO REQUERIDO") is null, "NO REQUERIDO se autoriza sin carga (otro usuario)");
Equal("NO HA SIDO CARGADA", Aut(carga: ""), "Sin carga y sin NO REQUERIDO no se autoriza");
Equal("EN ESPERA", Aut(carga: "EN ESPERA"), "EN ESPERA no se autoriza");
Equal("CARGANDO", Aut(carga: "CARGANDO"), "CARGANDO no se autoriza");
Equal("RELACION BLOQUEADA", Aut(carga: "BLOQUEO"), "BLOQUEO no se autoriza");
Equal("RELACION BLOQUEADA", Aut(carga: "BLOQUEO REIMPRESION"), "BLOQUEO REIMPRESION no se autoriza");
Equal("EN PROCESO", Aut(estatus: "EN PROCESO"), "EN PROCESO no se autoriza");
Equal("RELACION OCUPADA", Aut(ocupada: true), "Ocupada no se autoriza");
Check(Aut(ajenas: true)!.StartsWith("ERROR"), "Bultos ajenos (posible descargo) no se autoriza");
Check(Aut(error: true)!.StartsWith("INFORMACION INCORRECTA"), "Etiquetas de otra relación no se autoriza");
Check(Aut(carga: "DEMASIADO RAPIDO") is null && Aut(carga: "INCOMPLETA") is null && Aut(carga: "DIFERENCIA EN BULTOS-ETIQUETAS") is null, "Los estados que piden autorización sí se pueden autorizar");
Check(Aut(carga: "SALIDA") is not null, "Una relación que ya salió no se vuelve a autorizar");
Check(SalidaReglas.MotivoNoSePuedeRetornar("BLOQUEO") is not null && SalidaReglas.MotivoNoSePuedeRetornar("CARGADA") is null, "Retornar a espera no se permite con bloqueo");

// ── Recorte a la columna ─────────────────────────────────────────────────

Equal(50, SalidaReglas.Recortar(new string('X', 120), 50).Length, "Un código largo se recorta a 50");
Equal("ABC", SalidaReglas.Recortar("ABC", 50), "Un código corto no cambia");
Equal("", SalidaReglas.Recortar(null, 13), "Nulo queda vacío");

// ── Totales ilegibles: el legacy deja «ERROR EN INFORMACION» y el supervisor no autoriza ─────────────────

Equal("ERROR EN INFORMACION (revisar si esta agrupado)", SalidaReglas.EstatusInformacionIncorrecta, "Texto del estado, igual que el legacy");
Equal(SalidaReglas.EstatusInformacionIncorrecta,
    SalidaReglas.MotivoNoSePuedeAutorizar("COMPLETA", "CARGADA", "operador", "supervisor", false, false, false, informacionIncorrecta: true),
    "Con los totales ilegibles no se autoriza");
Check(SalidaReglas.MotivoNoSePuedeAutorizar("COMPLETA", "CARGADA", "operador", "supervisor", false, false, false, informacionIncorrecta: false) is null,
    "Con los totales bien sí se autoriza");
Check(SalidaReglas.EstatusInformacionIncorrecta.Contains("ERROR"), "Contiene ERROR: la app ya bloquea ese texto (RelacionRules.canAuthorize)");

// ── Plantillas de correo de reportes.config_correo ─────────────────────────────────────────────────────

var valoresCorreo = new Dictionary<string, string> { ["relacion"] = "01I26104539", ["usuario"] = "a<b", ["caja"] = "ABC 123" };
Equal("Caja ABC 123, relación 01I26104539", SalidaReglas.AplicarPlantilla("Caja [caja], relación [relacion]", valoresCorreo, html: false), "Los marcadores se llenan");
Equal("<b>a&lt;b</b>", SalidaReglas.AplicarPlantilla("<b>[usuario]</b>", valoresCorreo, html: true), "En el cuerpo HTML los valores se codifican");
Equal("a<b", SalidaReglas.AplicarPlantilla("[usuario]", valoresCorreo, html: false), "En el asunto no se codifican");
Equal("01I26104539", SalidaReglas.AplicarPlantilla("[RELACION]", valoresCorreo, html: false), "El marcador no distingue mayúsculas");
Equal("[inicio]", SalidaReglas.AplicarPlantilla("[inicio]", valoresCorreo, html: false), "Un marcador sin valor se deja como está");

// ── Búsqueda: lo que teclea el usuario no se vuelve comodín ──────────────────────────────────────────

Equal(@"100\%\_x\\", SalidaReglas.EscaparLike(@"100%_x\"), "Se escapan %, _ y la diagonal");
Equal("01I2610", SalidaReglas.EscaparLike("01I2610"), "Un número normal no cambia");

Console.WriteLine($"SalidaReglas.Regression: {checks} comprobaciones correctas.");
