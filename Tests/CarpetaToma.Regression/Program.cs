using System.Text.RegularExpressions;

using PMMovil.Api.Services;

// Regresiones de «Tomar carpeta»: el revisador registra que agarró la carpeta física sin abrir la revisión, y el Monitor
// muestra quién la trae y cuánto lleva sin iniciar. Sin servidor ni base de datos.
//   dotnet run --project Tests/CarpetaToma.Regression
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");

// ── Nivel: cuánto lleva una carpeta tomada sin iniciar ────────────────────────────────────────────────────────────────

Equal("OK", CarpetaTomaRules.Nivel(0, 60, 180), "Recién tomada: OK");
Equal("OK", CarpetaTomaRules.Nivel(59 * 60 + 59, 60, 180), "A 59 min 59 s todavía OK");
Equal("AVISO", CarpetaTomaRules.Nivel(60 * 60, 60, 180), "A la hora exacta pasa a AVISO");
Equal("AVISO", CarpetaTomaRules.Nivel(179 * 60, 60, 180), "A 179 min sigue en AVISO");
Equal("CRITICO", CarpetaTomaRules.Nivel(180 * 60, 60, 180), "A las 3 horas exactas pasa a CRITICO");
Equal("CRITICO", CarpetaTomaRules.Nivel(7 * 3600 + 30 * 60, 60, 180), "La carpeta de las 9:30 a las 17:00 es CRITICO");
Equal("OK", CarpetaTomaRules.Nivel(-5, 60, 180), "Un reloj adelantado no da negativos");
Equal("AVISO", CarpetaTomaRules.Nivel(31 * 60, 30, 90), "Umbrales propios: aviso a los 30 min");
Equal("CRITICO", CarpetaTomaRules.Nivel(90 * 60, 30, 90), "Umbrales propios: crítico a los 90 min");

// ── Umbrales: configuración vacía o incoherente vuelve a los de siempre ───────────────────────────────────────────────

Equal((60, 180), CarpetaTomaRules.NormalizarUmbrales(null, null), "Sin configuración: 60 y 180");
Equal((45, 120), CarpetaTomaRules.NormalizarUmbrales(45, 120), "Configuración válida se respeta");
Equal((60, 180), CarpetaTomaRules.NormalizarUmbrales(0, 0), "Ceros no sirven");
Equal((60, 200), CarpetaTomaRules.NormalizarUmbrales(-10, 200), "Aviso negativo usa el de siempre y respeta un crítico válido");
Equal((60, 180), CarpetaTomaRules.NormalizarUmbrales(120, 60), "Crítico por debajo del aviso: valores de siempre");
Equal((60, 180), CarpetaTomaRules.NormalizarUmbrales(90, 90), "Crítico igual al aviso: valores de siempre");
Equal((60, 400), CarpetaTomaRules.NormalizarUmbrales(null, 400), "Solo el crítico configurado: el aviso usa el de siempre");

// ── Decidir: qué pasa al tocar «Tomar carpeta» ────────────────────────────────────────────────────────────────────────

DecisionToma D(bool revisada = false, bool propia = false, bool otro = false, bool mia = false, bool titular = false, bool confirmar = false)
    => CarpetaTomaRules.Decidir(revisada, propia, otro, mia, titular, confirmar);

Equal(DecisionToma.Nueva, D(), "Nadie la tiene: se toma");
Equal(DecisionToma.YaEraTuya, D(mia: true), "Ya era mía: se conserva la hora, no se reinicia");
Equal(DecisionToma.PedirConfirmacion, D(titular: true), "La tiene otra persona: se avisa quién y se pregunta");
Equal(DecisionToma.Transferir, D(titular: true, confirmar: true), "Confirmó: pasa a su nombre");
Equal(DecisionToma.YaRevisada, D(revisada: true), "Ya revisada: no hay nada que tomar");
Equal(DecisionToma.YaRevisada, D(revisada: true, titular: true, confirmar: true), "Ya revisada gana aun confirmando");
Equal(DecisionToma.EnRevisionPropia, D(propia: true), "La estoy revisando yo: no tiene sentido tomarla");
Equal(DecisionToma.EnRevisionPorOtro, D(otro: true), "La está revisando otra persona: no se le quita");
Equal(DecisionToma.EnRevisionPorOtro, D(otro: true, titular: true, confirmar: true), "Confirmar no le quita la revisión en curso a nadie");
Equal(DecisionToma.YaEraTuya, D(mia: true, titular: true), "Si ya era mía no se pide confirmación por una asignación ajena");

Equal("TOMADA", CarpetaTomaRules.Codigo(DecisionToma.Nueva), "Código de toma nueva");
Equal("TOMADA", CarpetaTomaRules.Codigo(DecisionToma.Transferir), "Una transferencia también es TOMADA");
Equal("TOMADA_POR_OTRO", CarpetaTomaRules.Codigo(DecisionToma.PedirConfirmacion), "Código de confirmación");
Equal("YA_REVISADA", CarpetaTomaRules.Codigo(DecisionToma.YaRevisada), "Código de ya revisada");
Check(CarpetaTomaRules.Exito(DecisionToma.Nueva) && CarpetaTomaRules.Exito(DecisionToma.Transferir) && CarpetaTomaRules.Exito(DecisionToma.YaEraTuya),
    "Nueva, transferida y ya mía dejan la carpeta a nombre del revisador");
Check(!CarpetaTomaRules.Exito(DecisionToma.PedirConfirmacion) && !CarpetaTomaRules.Exito(DecisionToma.YaRevisada)
      && !CarpetaTomaRules.Exito(DecisionToma.EnRevisionPorOtro) && !CarpetaTomaRules.Exito(DecisionToma.EnRevisionPropia),
    "Confirmación, ya revisada y en revisión no son éxito");

// ── Hace ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

Equal("hace un momento", CarpetaTomaRules.Hace(0), "0 s");
Equal("hace un momento", CarpetaTomaRules.Hace(59), "59 s");
Equal("hace 12 min", CarpetaTomaRules.Hace(12 * 60 + 5), "12 min");
Equal("hace 1 h", CarpetaTomaRules.Hace(3600), "1 h exacta");
Equal("hace 3 h 20 min", CarpetaTomaRules.Hace(3 * 3600 + 20 * 60), "3 h 20 min");
Equal("hace 7 h 30 min", CarpetaTomaRules.Hace(7 * 3600 + 30 * 60), "de las 9:30 a las 17:00");
Equal("hace 1 día", CarpetaTomaRules.Hace(26 * 3600), "26 h");
Equal("hace 3 días", CarpetaTomaRules.Hace(3 * 24 * 3600 + 100), "3 días");

// ── Guardián del código fuente ────────────────────────────────────────────────────────────────────────────────────────
// En producción asignaciones_revision.fecha_asignacion es TIMESTAMP con ON UPDATE CURRENT_TIMESTAMP: un UPDATE que no la
// nombre le pone la hora de ahora, y «la tomó hace 7 h» pasaría a «hace 2 min» en cuanto el revisador abre la carpeta.

string apiRoot = AppContext.BaseDirectory;
while (apiRoot.Length > 0 && !File.Exists(Path.Combine(apiRoot, "PMMovil.Api.csproj")))
{
    apiRoot = Path.GetDirectoryName(apiRoot) ?? string.Empty;
}
Check(apiRoot.Length > 0, "No se encontró la carpeta del API a partir de " + AppContext.BaseDirectory);

var archivos = new[] { "Controllers", "Services", "Hubs" }
    .Select(c => Path.Combine(apiRoot, c))
    .Where(Directory.Exists)
    .SelectMany(c => Directory.EnumerateFiles(c, "*.cs", SearchOption.AllDirectories))
    .ToList();
Check(archivos.Count > 20, "El guardián no encontró los archivos del API");

var updateAsignacion = new Regex(@"UPDATE\s+asignaciones_revision\b[^""]*", RegexOptions.IgnoreCase | RegexOptions.Singleline);
var sinFecha = new List<string>();
int total = 0;
foreach (string archivo in archivos)
{
    string texto = Regex.Replace(File.ReadAllText(archivo), @"(?m)^\s*//.*$", string.Empty);
    foreach (Match m in updateAsignacion.Matches(texto))
    {
        total++;
        if (!m.Value.Contains("fecha_asignacion", StringComparison.OrdinalIgnoreCase))
        {
            sinFecha.Add($"{Path.GetFileName(archivo)}: «{Regex.Replace(m.Value, @"\s+", " ")}»");
        }
    }
}
Check(total >= 8, $"El guardián esperaba encontrar los UPDATE de asignaciones_revision (halló {total})");
Check(sinFecha.Count == 0,
    "Todo UPDATE de asignaciones_revision debe nombrar fecha_asignacion (conservarla con «fecha_asignacion = fecha_asignacion»): "
    + string.Join("; ", sinFecha));

// El Monitor no mezcla las tomas con las asignaciones del supervisor.
string asignaciones = File.ReadAllText(Path.Combine(apiRoot, "Controllers", "AsignacionesController.cs"));
Check(Regex.IsMatch(asignaciones, @"FROM asignaciones_revision WHERE estatus IN \(0, 1\) AND usuario_asigna <> usuario_asignado"),
    "«activas» (asignaciones del supervisor) debe excluir las carpetas que el propio revisador tomó");

Console.WriteLine($"CarpetaToma.Regression: {checks} comprobaciones correctas.");
