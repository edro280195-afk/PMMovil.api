using System.Diagnostics;

using PMMovil.Api.Services;

// Regresiones de la búsqueda difusa de entradas (pantalla de Revisión). Sin servidor ni base de datos.
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");

DateTime today = new(2026, 11, 4);
EntrySearchEntry E(string reference, string client, int daysAgo = 0, string clientId = "")
    => new(reference, clientId, client, today.AddDays(-daysAgo));

// ── Normalización ─────────────────────────────────────────────────────────────

Equal("VALVULAS NORTE", EntrySearchRules.Normalize("  Válvulas   Norte "), "Quita acentos, mayúsculas y espacios de sobra");
Equal("01C263607340000", EntrySearchRules.NormalizeKey("01c26 3607340000"), "La referencia no lleva espacios");
Equal("", EntrySearchRules.Normalize(null), "Nulo es vacío");
Equal("A B", EntrySearchRules.Normalize("a-b"), "Los signos separan palabras");
Equal(0, EntrySearchRules.Tokens("   ").Length, "Solo espacios no son consulta");
Equal(2, EntrySearchRules.Tokens("rodamientos 3607").Length, "Dos palabras, dos partes");

// ── Datos de la prueba ────────────────────────────────────────────────────────

var index = new List<EntrySearchEntry>
{
    E("01C263607340000", "Rodamientos Aurora", 0, "2939"),
    E("01C263591120000", "Válvulas Norte", 1),
    E("01C263607890000", "Industrias Marlo", 1),
    E("01C263580470000", "Lubricantes del Golfo", 2),
    E("01C263571900000", "Sellos Técnicos Ávila", 4),
    E("01C263562840000", "Tornillería Delta", 6),
    E("01C263555550000", "Rodamientos Aurora", 30),
    E("01C253607340000", "Aceros del Bajío", 200),
};

List<string> Refs(EntrySearchResult r) => r.Hits.Select(h => h.Entry.Reference).ToList();

// ── Mínimo de caracteres ──────────────────────────────────────────────────────

Equal(0, EntrySearchRules.Search(index, "36", 8).Hits.Count, "Con 2 caracteres no se busca");
Equal(0, EntrySearchRules.Search(index, "  ", 8).Hits.Count, "Sin texto no se busca");
Equal(0, EntrySearchRules.Search(index, null, 8).Hits.Count, "Consulta nula no se busca");
Check(EntrySearchRules.Search(index, "360", 8).Hits.Count > 0, "Con 3 caracteres ya se busca");

// ── Referencia ────────────────────────────────────────────────────────────────

EntrySearchResult exact = EntrySearchRules.Search(index, "01C263607340000", 8);
Equal("01C263607340000", exact.Hits[0].Entry.Reference, "La referencia completa va primero");
Check(!exact.Hits[0].Approximate, "Una coincidencia exacta no es aproximada");
Equal(1, exact.Hits.Count, "La referencia completa solo coincide con ella");

EntrySearchResult digits = EntrySearchRules.Search(index, "3607", 8);
Equal(3, digits.Hits.Count, "Cuatro dígitos del medio encuentran todas las que los traen");
Equal("01C263607340000", digits.Hits[0].Entry.Reference, "A igual coincidencia gana la más reciente");
Check(!digits.Tolerant, "Con coincidencias directas de sobra no hace falta la pasada tolerante");

Equal("01C263607340000", EntrySearchRules.Search(index, "01c26 3607 34", 8).Hits[0].Entry.Reference, "Espacios y minúsculas no estorban");
Equal(0, EntrySearchRules.Search(index, "999999", 8).Hits.Count, "Lo que no existe no devuelve nada");

// ── Cliente ───────────────────────────────────────────────────────────────────

EntrySearchResult byClient = EntrySearchRules.Search(index, "valvulas", 8);
Equal(1, byClient.Hits.Count, "Sin acento encuentra «Válvulas»");
Equal("01C263591120000", byClient.Hits[0].Entry.Reference, "El cliente correcto");

Equal(2, EntrySearchRules.Search(index, "rodam", 8).Hits.Count, "El inicio de una palabra del cliente basta");
Equal(1, EntrySearchRules.Search(index, "golfo", 8).Hits.Count, "Cualquier palabra del cliente basta");
Equal(1, EntrySearchRules.Search(index, "ila", 8).Hits.Count, "Un tramo dentro del nombre también (Ávila)");
Equal("01C263607340000", EntrySearchRules.Search(index, "2939", 8).Hits[0].Entry.Reference, "El número de cliente exacto encuentra sus entradas");

// ── Dos partes: todas deben coincidir ─────────────────────────────────────────

EntrySearchResult both = EntrySearchRules.Search(index, "rodamientos 3607", 8);
Equal(1, both.Hits.Count, "Cliente y dígitos a la vez acotan");
Equal("01C263607340000", both.Hits[0].Entry.Reference, "La que cumple las dos");
Equal(0, EntrySearchRules.Search(index, "rodamientos 4047", 8).Hits.Count, "Si una parte no coincide, no sale");

// ── Tolerancia a errores de tecleo ────────────────────────────────────────────

EntrySearchResult swapped = EntrySearchRules.Search(index, "360374", 8);     // 360734 con dos dígitos intercambiados
Equal("01C263607340000", swapped.Hits[0].Entry.Reference, "Dos dígitos intercambiados siguen encontrando la entrada");
Check(swapped.Hits[0].Approximate, "…y se marca como aproximada");
Check(swapped.Tolerant, "…porque corrió la pasada tolerante");

EntrySearchResult wrongDigit = EntrySearchRules.Search(index, "360834", 8);   // un dígito distinto
Equal("01C263607340000", wrongDigit.Hits[0].Entry.Reference, "Un dígito distinto también");

EntrySearchResult missingLetter = EntrySearchRules.Search(index, "rodamintos", 8);
Equal(2, missingLetter.Hits.Count, "Una letra de menos en el cliente");
Check(missingLetter.Hits.All(h => h.Approximate), "Todas aproximadas");

EntrySearchResult shortTypo = EntrySearchRules.Search(index, "36703", 8);     // 5 dígitos: ya no se tolera error
Equal(0, shortTypo.Hits.Count, "Con menos de 6 dígitos no se tolera error: coincidiría con entradas al azar");
Equal(0, EntrySearchRules.Search(index, "rodx", 8).Hits.Count, "Un nombre corto con error no se adivina");

Equal(0, EntrySearchRules.Search(index, "zzzzzz", 8).Hits.Count, "Letras que no se parecen a nada");
Equal(0, EntrySearchRules.Search(index, "360999999", 8).Hits.Count, "Lejos de cualquier referencia");

// ── Orden, límite y total ─────────────────────────────────────────────────────

var many = new List<EntrySearchEntry>();
for (int i = 0; i < 50; i++) many.Add(E($"01C26360{i:D2}00000", "Cliente Demo", i));
EntrySearchResult limited = EntrySearchRules.Search(many, "01C2636", 8);
Equal(8, limited.Hits.Count, "Respeta el límite pedido");
Equal(50, limited.Total, "Informa cuántas hubo en total");
Equal("01C263600000000", limited.Hits[0].Entry.Reference, "Empatadas, primero la más reciente");
Equal(20, EntrySearchRules.Search(many, "01C2636", 500).Hits.Count, "El límite tiene techo");
Equal(8, EntrySearchRules.Search(many, "01C2636", 0).Hits.Count, "Un límite inválido usa el de omisión");

Equal(
    string.Join(",", Refs(EntrySearchRules.Search(many, "0036", 8))),
    string.Join(",", Refs(EntrySearchRules.Search(many, "0036", 8))),
    "El orden es determinista");

// ── Distancia ─────────────────────────────────────────────────────────────────

Equal(0, EntrySearchRules.Distance("abc", "abc"), "Iguales");
Equal(1, EntrySearchRules.Distance("abc", "abd"), "Una sustitución");
Equal(1, EntrySearchRules.Distance("abc", "ab"), "Una resta");
Equal(1, EntrySearchRules.Distance("ab", "abc"), "Una suma");
Equal(1, EntrySearchRules.Distance("abcd", "abdc"), "Dos contiguos intercambiados cuentan como un solo error");
Equal(2, EntrySearchRules.Distance("abcd", "badc"), "Dos intercambios son dos errores");
Equal(3, EntrySearchRules.Distance("", "abc"), "Contra vacío es el largo");
Equal(int.MaxValue, EntrySearchRules.Distance(new string('a', 60), "abc"), "Un tramo absurdo de largo no se compara");

// ── Rendimiento: el tamaño real de un año de entradas ─────────────────────────

var rng = new Random(20261104);
var big = new List<EntrySearchEntry>(10771);
string[] clients = { "Rodamientos Aurora", "Válvulas Norte", "Lubricantes del Golfo", "Sellos Técnicos Ávila", "Bombas Cóndor", "Industrias Marlo" };
var seen = new HashSet<string>();
while (big.Count < 10771)
{
    string reference = $"01C26{rng.Next(350000, 379000)}0000";
    if (seen.Add(reference)) big.Add(E(reference, clients[rng.Next(clients.Length)], rng.Next(365)));
}
EntrySearchRules.Search(big, "3607", 8);   // calienta el JIT
var watch = Stopwatch.StartNew();
EntrySearchResult direct = EntrySearchRules.Search(big, "3607", 8);
double directMs = watch.Elapsed.TotalMilliseconds;
watch.Restart();
EntrySearchResult tolerant = EntrySearchRules.Search(big, "360374", 8);
double tolerantMs = watch.Elapsed.TotalMilliseconds;
Check(direct.Hits.Count == 8, "Hay coincidencias de sobra en un año");
Check(directMs < 50, $"Búsqueda directa sobre 10,771 entradas debe ser instantánea (tardó {directMs:F1} ms)");
Check(tolerantMs < 250, $"Búsqueda tolerante sobre 10,771 entradas debe seguir siendo ágil (tardó {tolerantMs:F1} ms)");

Console.WriteLine($"OK: {checks} comprobaciones. 10,771 entradas: directa {directMs:F1} ms, tolerante {tolerantMs:F1} ms.");
