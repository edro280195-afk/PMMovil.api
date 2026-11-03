using PMMovil.Api.Services;

// Regresiones de los catalogos maestros. Sin servidor ni base de datos.
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");

static CatalogoMaestroItem Item(string id, string nombre, string extra = "") => new() { Id = id, Nombre = nombre, Extra = extra };

// ── La version es una huella del contenido ──────────────────────────────────────────────────────
var base1 = new List<CatalogoMaestroItem> { Item("56", "CAJA CERRADA 53'"), Item("58", "CAJA REFRIGERADA 53'") };
string v1 = CatalogosService.CalcularVersion(base1);
Equal(v1, CatalogosService.CalcularVersion([Item("56", "CAJA CERRADA 53'"), Item("58", "CAJA REFRIGERADA 53'")]), "El mismo contenido da la misma version");
Check(v1.StartsWith("2-"), "La version empieza con la cantidad de renglones");
Check(v1 != CatalogosService.CalcularVersion([Item("56", "CAJA CERRADA 53'"), Item("58", "CAJA REFRIGERADA 53 PIES")]), "Cambiar un nombre cambia la version");
Check(v1 != CatalogosService.CalcularVersion([Item("56", "CAJA CERRADA 53'"), Item("59", "CAJA REFRIGERADA 53'")]), "Cambiar un id cambia la version");
Check(v1 != CatalogosService.CalcularVersion([Item("56", "CAJA CERRADA 53'"), Item("58", "CAJA REFRIGERADA 53'", "C")]), "Cambiar la marca extra cambia la version");
Check(v1 != CatalogosService.CalcularVersion([Item("58", "CAJA REFRIGERADA 53'"), Item("56", "CAJA CERRADA 53'")]), "Cambiar el orden cambia la version (el orden se ve en pantalla)");
Check(v1 != CatalogosService.CalcularVersion([Item("56", "CAJA CERRADA 53'")]), "Quitar un renglon cambia la version");
Check(CatalogosService.CalcularVersion([]).StartsWith("0-"), "Un catalogo vacio tambien tiene version");
// Que "AB"+"C" y "A"+"BC" no den la misma huella.
Check(CatalogosService.CalcularVersion([Item("AB", "C")]) != CatalogosService.CalcularVersion([Item("A", "BC")]), "Los campos no se pueden confundir entre si");
// Con acentos y enies: la huella no depende de la configuracion regional.
Equal(CatalogosService.CalcularVersion([Item("1", "NIÑO ÁNGEL")]), CatalogosService.CalcularVersion([Item("1", "NIÑO ÁNGEL")]), "Acentos estables");

// ── Clientes: una sola lista para Caseta y Entrada de bodega ────────────────────────────────────
var activos = new List<(string Id, string Nombre, string TipoMv)>
{
    ("3300", "WATSON PHILLIPS", "OPERACION"),
    ("CL7575", "TC HEARTLAND", ""),
    ("CL8400", "FREUDENBERG-NOK", "OPERACION"),
    ("3300", "WATSON PHILLIPS (REPETIDO)", "OPERACION"),   // id repetido: se queda el primero
    ("", "SIN ID", "OPERACION")                              // sin id: no se ofrece
};
var vista = new List<(string Id, string Nombre)> { ("CL7575", "TC HEARTLAND"), ("CL9999", "SOLO EN LA VISTA") };
var clientes = CatalogosService.CombinarClientes(activos, vista);

Equal(4, clientes.Count, "Tres activos distintos mas el que solo esta en la vista");
Check(clientes.First(c => c.Id == "3300").Extra == "C", "Un cliente OPERACION es de Caseta");
Check(clientes.First(c => c.Id == "CL8400").Extra == "C", "Otro OPERACION tambien");
Check(clientes.First(c => c.Id == "CL7575").Extra == "C", "Uno que sin ser OPERACION esta en la vista de Caseta tambien lo es");
Check(clientes.First(c => c.Id == "CL9999").Extra == "C", "Uno que solo esta en la vista tambien se ofrece a Caseta");
Equal("WATSON PHILLIPS", clientes.First(c => c.Id == "3300").Nombre, "Con un id repetido se queda el primero");
Equal("FREUDENBERG-NOK,SOLO EN LA VISTA,TC HEARTLAND,WATSON PHILLIPS", string.Join(",", clientes.Select(c => c.Nombre)), "Ordenados por nombre");

var soloBodega = CatalogosService.CombinarClientes([("CL1", "CLIENTE DE BODEGA", ""), ("CL2", "OTRO", "")], []);
Check(soloBodega.All(c => c.Extra == ""), "Los que Caseta no ve no llevan marca: solo los ve Entrada de bodega");
Equal(0, CatalogosService.CombinarClientes([], []).Count, "Sin clientes no truena");

// ── Los nombres publicados ──────────────────────────────────────────────────────────────────────
string[] publicados =
[
    CatalogosService.Clientes, CatalogosService.Lineas, CatalogosService.TiposCaja, CatalogosService.BodegasCaseta,
    CatalogosService.PaisesCaseta, CatalogosService.Paises, CatalogosService.UnidadesComerciales,
    CatalogosService.EntradaBodegas, CatalogosService.EntradaHazmat, CatalogosService.EntradaManejos,
    CatalogosService.EntradaRecibidores, CatalogosService.EntradaUbicaciones, CatalogosService.EntradaClases
];
Equal(publicados.Length, publicados.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Ningun nombre de catalogo se repite");
Check(publicados.All(n => n == n.ToLowerInvariant() && !n.Contains(',')), "Los nombres van en minusculas y sin comas (viajan separados por coma)");

Console.WriteLine($"Catalogos.Regression: {checks} comprobaciones correctas.");
