using PMMovil.Api.Services;

// Regresiones de lo que la entrada de bodega guarda en traf.crecibio (5-nov-2026). Sin servidor ni bases de datos.
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");

// ── El nombre, no la clave ─────────────────────────────────────────────────────────────────────────
// usuarios: claveusuario = frodriguez, nombre = FRANCISCO JAVIE, apaterno = RODRIGUEZ.
Equal("FRANCISCO JAVIE RODRIGUEZ", EntradaService.NombreRecibio("FRANCISCO JAVIE RODRIGUEZ", "frodriguez", "frodriguez"),
    "Con nombre en usuarios se guarda el nombre y no la clave");
Equal("GLADYS RODRIGUEZ", EntradaService.NombreRecibio("Gladys Rodriguez", "", "gladys"),
    "Se guarda en mayúsculas como las entradas del legacy");
Equal("HUMBERTO SANCHEZ", EntradaService.NombreRecibio("  Humberto   Sanchez ", null, "bhsanchez"),
    "Los espacios sobrantes se limpian");

// ── Respaldos cuando no hay nombre ─────────────────────────────────────────────────────────────────
Equal("JORGE MEDINA", EntradaService.NombreRecibio(null, "Jorge Medina", "jmedina"),
    "Sin nombre en usuarios se respeta lo que mandó la app");
Equal("JMEDINA", EntradaService.NombreRecibio("", "   ", "jmedina"),
    "Sin nada más, la clave: nunca queda vacío");
Equal("", EntradaService.NombreRecibio(null, null, null), "Sin usuario tampoco se inventa un nombre");

// ── traf.crecibio es varchar(30): con el modo estricto de MySQL un nombre más largo haría fallar el guardado ──
string largo = EntradaService.NombreRecibio("MARIA GUADALUPE FERNANDA RODRIGUEZ SANCHEZ", null, "mgfrs");
Check(largo.Length <= EntradaService.LongitudMaximaRecibio, "El nombre se recorta a la longitud de la columna");
Check(!largo.EndsWith(' '), "El recorte no deja un espacio al final");
Equal("MARIA GUADALUPE FERNANDA RODRI", largo, "Se recorta por la derecha");

Console.WriteLine($"Entrada.Regression: {checks} comprobaciones correctas.");
