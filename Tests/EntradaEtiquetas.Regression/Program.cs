using PMMovil.Api.Services;

// Regresiones del registro de bultos de una entrada en etiqueta_mcia (19-nov-2026): la app guardaba traf, traf3 y trafdet pero
// nunca las filas de etiqueta_mcia (una por bulto), así que las entradas hechas desde la app no tenían bultos que cargar. Sin
// servidor ni bases de datos: son las reglas puras que usa el servicio (el INSERT contra MySQL se comprobó aparte, con una base
// temporal con el mismo esquema de producción).
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");
void Same(IEnumerable<int> expected, IEnumerable<int> actual, string message)
    => Check(expected.SequenceEqual(actual), $"{message} (esperado: [{string.Join(",", expected)}], real: [{string.Join(",", actual)}])");

// ── El total de bultos: lo mismo que calcula la app al imprimir (maxOf(1, suma.roundToInt())) ───────────
Equal(21, EntradaEtiquetas.TotalBultos(new[] { 21m }), "Una línea de 21 bultos");
Equal(16, EntradaEtiquetas.TotalBultos(new[] { 3m, 4m, 5m, 4m }), "Cuatro líneas se suman (3+4+5+4) y se numeran juntas");
Equal(3, EntradaEtiquetas.TotalBultos(new[] { 2.5m }), "El .5 sube, como roundToInt de la app");
Equal(1, EntradaEtiquetas.TotalBultos(new[] { 0.5m }), "0.5 también sube a 1");
Equal(1, EntradaEtiquetas.TotalBultos(new[] { 0.4m }), "Menos de medio bulto sigue siendo 1 etiqueta (mínimo 1)");
Equal(1, EntradaEtiquetas.TotalBultos(new[] { 0m }), "Cero bultos se imprime como 1 etiqueta, igual que la app");
Equal(1, EntradaEtiquetas.TotalBultos(Array.Empty<decimal>()), "Sin líneas, mínimo 1");
Equal(1, EntradaEtiquetas.TotalBultos(new[] { -5m }), "Una cantidad negativa no produce etiquetas negativas");
Equal(7, EntradaEtiquetas.TotalBultos(new[] { 3.4m, 3.4m }), "Se suma primero y se redondea al final (6.8 → 7), como la app");
Equal(int.MaxValue, EntradaEtiquetas.TotalBultos(new[] { 3_000_000_000m }), "Un total desmedido se topa en lugar de desbordar");
Equal(int.MaxValue, EntradaEtiquetas.TotalBultos(new[] { 2_000_000_000m, 2_000_000_000m }), "Sumar dos desmedidos tampoco desborda");

// ── El tope de bultos por entrada ──────────────────────────────────────────────────────────────────────
Check(EntradaEtiquetas.MensajeTotalInvalido(1) == null, "1 bulto es válido");
Check(EntradaEtiquetas.MensajeTotalInvalido(500) == null, "500 bultos (lo más grande de este año) es válido");
Check(EntradaEtiquetas.MensajeTotalInvalido(EntradaEtiquetas.MaxBultos) == null, "El tope exacto es válido");
string? demasiados = EntradaEtiquetas.MensajeTotalInvalido(EntradaEtiquetas.MaxBultos + 1);
Check(demasiados != null, "Pasarse del tope se rechaza");
Check(demasiados!.Contains("10,000") && demasiados.Contains("10,001"), "El mensaje dice el total y el máximo");

// ── El texto de la etiqueta: el mismo que el QR de la app («$reference $index-$total») ───────────────────
Equal("01C263619470000 14-14", EntradaEtiquetas.Texto("01C263619470000", 14, 14), "Referencia, espacio, n, guion, total");
Equal("01C263619290000 1-1", EntradaEtiquetas.Texto("01C263619290000", 1, 1), "Un solo bulto");
Equal("01C263619200000 21-21", EntradaEtiquetas.Texto("01C263619200000", 21, 21), "El último bulto de 21");

// ── El usuario cabe en varchar(15) ─────────────────────────────────────────────────────────────────────
Equal("gsolis", EntradaEtiquetas.UsuarioParaEtiqueta("gsolis"), "Un usuario normal queda igual");
Equal("gsolis", EntradaEtiquetas.UsuarioParaEtiqueta("  gsolis  "), "Los espacios de los extremos no cuentan");
Equal("auxiliar.manzan", EntradaEtiquetas.UsuarioParaEtiqueta("auxiliar.manzan"), "Uno de 15 caracteres cabe completo");
Equal("auxiliar.manzan", EntradaEtiquetas.UsuarioParaEtiqueta("auxiliar.manzanillo"), "Uno más largo se recorta a 15 (en modo estricto rechazaría el INSERT)");
Equal("", EntradaEtiquetas.UsuarioParaEtiqueta(null), "Sin usuario queda vacío, como la columna por omisión");

// ── Qué bultos faltan por registrar (idempotencia) ─────────────────────────────────────────────────────
const string referencia = "01C263619200000";
Same(
    Enumerable.Range(1, 21),
    EntradaEtiquetas.Faltantes(referencia, 21, Array.Empty<string>()),
    "Sin nada registrado faltan los 21");

var yaRegistrados = new[] { "01C263619200000 1-21", "01C263619200000 2-21", "01C263619200000 21-21" };
Same(
    Enumerable.Range(3, 18),
    EntradaEtiquetas.Faltantes(referencia, 21, yaRegistrados),
    "Solo faltan los que no tienen fila (un reintento no duplica)");

Equal(
    0,
    EntradaEtiquetas.Faltantes(referencia, 21, Enumerable.Range(1, 21).Select(n => EntradaEtiquetas.Texto(referencia, n, 21))).Count,
    "Con todos registrados no falta ninguno");

Same(
    new[] { 3 },
    EntradaEtiquetas.Faltantes(referencia, 3, new[] { " 01c263619200000 1-3 ", "01C263619200000 2-3", null, "" }),
    "Compara sin distinguir mayúsculas ni espacios en los extremos (latin1_swedish_ci) e ignora vacíos: solo falta el 3");

Same(
    Enumerable.Range(1, 16),
    EntradaEtiquetas.Faltantes(referencia, 16, Enumerable.Range(1, 14).Select(n => EntradaEtiquetas.Texto(referencia, n, 14))),
    "Etiquetas registradas con OTRO total (14) no cuentan para el total actual (16): son otro texto");

Same(
    new[] { 1 },
    EntradaEtiquetas.Faltantes(referencia, 1, Array.Empty<string>()),
    "Una entrada de un bulto necesita una fila");

// ── El INSERT por lotes ────────────────────────────────────────────────────────────────────────────────
string uno = EntradaEtiquetas.ArmarInsert(1);
Check(uno.StartsWith("INSERT INTO etiqueta_mcia (creferencia, nlineno, total, textoetiqueta, usucrea, fcrea, usumodi, fmodi, "),
    "Las columnas van en el orden del legacy");
Check(uno.Contains("(@referencia, @n0, @total, @t0, @usuario, NOW(), @usuario, NOW(), '', '', '', '', '', 0, '')"),
    "Una fila: parámetros por bulto y vacíos como el legacy");
Check(!uno.Contains("time_scan") && !uno.Contains("time_inv"),
    "time_scan y time_inv no se nombran: quedan en su valor por omisión (0000-00-00) como las filas del legacy");

string tres = EntradaEtiquetas.ArmarInsert(3);
Equal(3, tres.Split("(@referencia, ").Length - 1, "Tres filas");
Check(tres.Contains("@n0") && tres.Contains("@n1") && tres.Contains("@n2") && !tres.Contains("@n3"), "Un @n por fila");
Check(tres.Contains("@t0") && tres.Contains("@t1") && tres.Contains("@t2") && !tres.Contains("@t3"), "Un @t por fila");

string lote = EntradaEtiquetas.ArmarInsert(EntradaEtiquetas.FilasPorLote);
Equal(EntradaEtiquetas.FilasPorLote, lote.Split("(@referencia, ").Length - 1, "Un lote completo");
Check(lote.Contains($"@n{EntradaEtiquetas.FilasPorLote - 1}") && !lote.Contains($"@n{EntradaEtiquetas.FilasPorLote},"),
    "El último parámetro del lote es el 199");

bool lanza = false;
try { EntradaEtiquetas.ArmarInsert(0); } catch (ArgumentOutOfRangeException) { lanza = true; }
Check(lanza, "Cero filas no arma un INSERT inválido");

// ── Una entrada de 450 bultos se reparte en lotes de 200 ───────────────────────────────────────────────
int[][] lotes = Enumerable.Range(1, 450).Chunk(EntradaEtiquetas.FilasPorLote).ToArray();
Equal(3, lotes.Length, "450 bultos son 3 lotes");
Equal(200, lotes[0].Length, "El primer lote es de 200");
Equal(50, lotes[2].Length, "El último lleva el resto");
Equal(450, lotes.Sum(l => l.Length), "No se pierde ni se repite ninguno");

// ── Los manejos que el legacy no etiqueta (PIPA 0 de 126, EXPO 1 de 38 entre el 15-sep y el 18-nov) ─────
foreach (string manejo in new[] { "M", "D", "T", "PAQ/M", "P", "C", "PLAT", "NOPED", "TOLVA", "F", "CTR", "" })
    Check(EntradaEtiquetas.LlevaEtiquetas(manejo), $"El manejo '{manejo}' lleva etiquetas");
Check(EntradaEtiquetas.LlevaEtiquetas(null), "Sin manejo no se excluye nada (el guardado ya exige manejo)");
foreach (string manejo in new[] { "PIPA", "pipa", " PIPA ", "EXPO", "Expo", " expo " })
    Check(!EntradaEtiquetas.LlevaEtiquetas(manejo), $"El manejo '{manejo}' NO lleva etiquetas (sin distinguir mayusculas ni espacios)");
Equal(2, EntradaEtiquetas.ManejosSinEtiqueta.Count, "Solo PIPA y EXPO");

Console.WriteLine($"EntradaEtiquetas.Regression: {checks} comprobaciones correctas.");
