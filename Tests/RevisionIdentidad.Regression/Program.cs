using System.Text.RegularExpressions;

using PMMovil.Api.Services;

// Regresiones de la IDENTIDAD de una factura en la revisión (caso 01C263616880000, 12-nov-2026).
//
// fact.cnofact es el número que imprime el proveedor y se REPITE entre entradas (1,528 números en 2026). Unir revdet con
// fact por cnofact trae las partidas de otras facturas (el Monitor contaba 4 partidas en lugar de 2) y hace que el cierre
// de la revisión crea que quedan partidas pendientes (no marcaba traf.status = 2 ni escribía traf3). Escribir fact con
// "WHERE cnofact = ..." modifica también las facturas de otras entradas.
//
// Sin servidor ni base de datos. Se corre con:  dotnet run --project Tests/RevisionIdentidad.Regression
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");

// ── 1. A qué entrada se refiere una petición que solo trae el número de factura ─────────────────────────────────────

var ninguna = Array.Empty<string>();
Equal("01C263616880000", RevisionFacturaAlcance.ElegirEntrada("01C263616880000", new[] { "OTRA" }, "OTRA"),
    "Si la app manda la entrada, esa manda siempre");
Equal("01C263616880000", RevisionFacturaAlcance.ElegirEntrada("  01C263616880000  ", ninguna, null),
    "La entrada indicada se recorta");
Check(RevisionFacturaAlcance.ElegirEntrada(null, ninguna, "01C1") is null,
    "Sin entrada indicada y sin facturas con ese número no se escribe nada");
Check(RevisionFacturaAlcance.ElegirEntrada("   ", ninguna, null) is null,
    "Una entrada en blanco cuenta como no indicada");
Equal("01C1", RevisionFacturaAlcance.ElegirEntrada(null, new[] { "01C1" }, null),
    "Un solo candidato sin ambigüedad: ese");
Equal("01C1", RevisionFacturaAlcance.ElegirEntrada(null, new[] { "01C2", "01C1" }, "01C1"),
    "APK vieja y número repetido: se usa la entrada donde el usuario trabaja");
Equal("01C2", RevisionFacturaAlcance.ElegirEntrada(null, new[] { "01C2", "01C1" }, "ZZZ"),
    "Si la entrada activa no es candidata, la más reciente");
Equal("01C2", RevisionFacturaAlcance.ElegirEntrada(null, new[] { "01C2", "01C1" }, null),
    "Sin entrada activa, la más reciente");
Equal("01C1", RevisionFacturaAlcance.ElegirEntrada(null, new[] { "01C2", "01C1" }, "01c1"),
    "La entrada activa se compara sin distinguir mayúsculas");

// ── 2. La regla de cierre (REVISADA FÍSICAMENTE) ──────────────────────────────────────────────────────────────────────

var completa = new RevisionNormalCompletionState(TotalFacturas: 1, FacturasIncompletas: 0, FacturasConDiscrepancia: 0, FacturasConPartidasIncompletas: 0);
Check(completa.PuedeEnviarCorreo, "Una factura completa y sin partidas pendientes se puede cerrar");
Equal(1, completa.EstatusFinal, "Cierre limpio = estatus 1");
Check(completa.MotivoIncompleto is null, "Cierre limpio: sin motivo");

var conDiscrepancia = new RevisionNormalCompletionState(2, 0, 1, 0);
Check(conDiscrepancia.PuedeEnviarCorreo, "Con discrepancia también se cierra y se manda el reporte");
Equal(2, conDiscrepancia.EstatusFinal, "Con discrepancia el estatus es 2");

var partidasPendientes = new RevisionNormalCompletionState(1, 0, 0, 1);
Check(!partidasPendientes.PuedeEnviarCorreo, "Con partidas sin revisar no se puede marcar revisada");
Equal(2, partidasPendientes.EstatusFinal, "Con partidas pendientes el estatus es 2");
Check(partidasPendientes.MotivoIncompleto is not null && partidasPendientes.MotivoIncompleto.Contains("partidas sin revisar"),
    "El motivo dice que faltan partidas por revisar");

var facturasSinCompletar = new RevisionNormalCompletionState(3, 2, 0, 0);
Check(!facturasSinCompletar.PuedeEnviarCorreo, "Con facturas sin completar no se marca revisada");
Check(facturasSinCompletar.MotivoIncompleto is not null && facturasSinCompletar.MotivoIncompleto.Contains("2 factura(s) sin completar"),
    "El motivo cuenta las facturas sin completar");

var ambas = new RevisionNormalCompletionState(3, 1, 0, 2);
Check(ambas.MotivoIncompleto is not null
      && ambas.MotivoIncompleto.Contains("1 factura(s) sin completar")
      && ambas.MotivoIncompleto.Contains("2 factura(s) con partidas sin revisar"),
    "El motivo junta las dos causas");

var sinFacturas = new RevisionNormalCompletionState(0, 0, 0, 0);
Check(!sinFacturas.PuedeEnviarCorreo, "Una entrada sin facturas no se marca revisada");
Check(sinFacturas.MotivoIncompleto is not null, "Una entrada sin facturas explica por qué");

// ── 3. Guardián del código fuente ─────────────────────────────────────────────────────────────────────────────────────
// Si alguien vuelve a escribir el cruce por número de factura, esta prueba falla antes de llegar a producción.

string apiRoot = AppContext.BaseDirectory;
while (apiRoot.Length > 0 && !File.Exists(Path.Combine(apiRoot, "PMMovil.Api.csproj")))
{
    apiRoot = Path.GetDirectoryName(apiRoot) ?? string.Empty;
}
Check(apiRoot.Length > 0, "No se encontró la carpeta del API (PMMovil.Api.csproj) a partir de " + AppContext.BaseDirectory);

string[] carpetas = { "Controllers", "Services", "Hubs", "Helpers", "Models" };
var archivos = carpetas
    .Select(c => Path.Combine(apiRoot, c))
    .Where(Directory.Exists)
    .SelectMany(c => Directory.EnumerateFiles(c, "*.cs", SearchOption.AllDirectories))
    .ToList();
Check(archivos.Count > 20, "El guardián no encontró los archivos del API");

// Quita los comentarios // para no dar falsos positivos con los que explican el problema.
static string SinComentarios(string texto) => Regex.Replace(texto, @"(?m)^\s*//.*$", string.Empty);

// 3a. revdet se une con fact por idfactura, nunca por cnofact.
var cruceProhibido = new Regex(
    @"\b(rd0?|r|pending|revdet)\.cnofact\s*=\s*(f2?|fact)\.cnofact|\b(f2?|fact)\.cnofact\s*=\s*(rd0?|r|pending|revdet)\.cnofact",
    RegexOptions.IgnoreCase);
var violacionesCruce = new List<string>();
foreach (string archivo in archivos)
{
    string texto = SinComentarios(File.ReadAllText(archivo));
    foreach (Match m in cruceProhibido.Matches(texto))
    {
        violacionesCruce.Add($"{Path.GetFileName(archivo)}: «{m.Value}»");
    }
}
Check(violacionesCruce.Count == 0,
    "revdet se une con fact por idfactura (el número de factura se repite entre entradas). Cruces por cnofact: "
    + string.Join("; ", violacionesCruce));

// 3b. UPDATE fact ... WHERE cnofact = @cnofact debe acotarse a la entrada.
var updateSinEntrada = new Regex(
    @"UPDATE\s+fact\b(?:(?!"";).)*?\bWHERE\s+cnofact\s*=\s*@cnofact\b(?![^""]*\bcref_agrupada\s*=\s*@\w+)",
    RegexOptions.IgnoreCase | RegexOptions.Singleline);
var violacionesUpdate = new List<string>();
foreach (string archivo in archivos)
{
    string texto = SinComentarios(File.ReadAllText(archivo));
    foreach (Match m in updateSinEntrada.Matches(texto))
    {
        violacionesUpdate.Add($"{Path.GetFileName(archivo)}: «{Regex.Replace(m.Value, @"\s+", " ")[..Math.Min(90, Regex.Replace(m.Value, @"\s+", " ").Length)]}…»");
    }
}
Check(violacionesUpdate.Count == 0,
    "UPDATE fact con WHERE cnofact sin cref_agrupada modificaría las facturas de otras entradas con el mismo número: "
    + string.Join("; ", violacionesUpdate));

// 3c. Los UPDATE de fact que usan la constante de alcance la definen con la entrada.
string servicio = File.ReadAllText(Path.Combine(apiRoot, "Services", "RevisionService.cs"));
Check(Regex.IsMatch(servicio, @"const string soloEstaFactura = ""WHERE cnofact = @cnofact AND cref_agrupada = @entrada"";"),
    "GuardarCampoFactura debe acotar sus UPDATE a la entrada (soloEstaFactura)");

// 3d. El cierre de la revisión no puede depender de que la llamada cambie una fila de sesión.
string timer = File.ReadAllText(Path.Combine(apiRoot, "Controllers", "TimerController.cs"));
Check(timer.Contains("bool converge = req.EsFinal"), "FinalizarTimer debe converger el cierre aunque las sesiones ya estuvieran cerradas");
Check(!Regex.IsMatch(timer, @"if \(rowsCerradas > 0 && req\.EsFinal\)\s*\{[^}]*SincronizarSistemaLegadoAsync"),
    "La sincronización con el sistema legado no puede ir condicionada a rowsCerradas");

Console.WriteLine($"RevisionIdentidad.Regression: {checks} comprobaciones correctas.");
