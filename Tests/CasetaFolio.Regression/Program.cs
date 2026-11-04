using PMMovil.Api.Services;

// Regresiones del folio de caseta que llega por la ruta. Sin servidor ni bases de datos.
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");

// ── Lo que ASP.NET Core entrega como valor de ruta (verificado con Kestrel el 28-oct) ──────────────
// La app manda E-020485%2F26; el controlador recibe "E-020485%2F26" SIN decodificar.
Equal("E-020485/26", CasetaFolio.Normalizar("E-020485%2F26"), "El %2F de la ruta se deshace");
Equal("E-020485/26", CasetaFolio.Normalizar("E-020485%252F26"), "Doblemente codificado también");
Equal("E-020485/26", CasetaFolio.Normalizar(" E-020485/26 "), "Los espacios de los extremos no cuentan");
Equal("E-020485/26", CasetaFolio.Normalizar("E-020485/26"), "Un folio ya limpio no cambia");
Equal("", CasetaFolio.Normalizar(null), "Sin folio queda vacío");
Equal("E-0204%ZZ85/26", CasetaFolio.Normalizar("E-0204%ZZ85/26"), "Un % que no es una codificación se respeta");

// ── La causa del aviso «El folio de caseta no coincide con el registro a modificar» ────────────────
Check(CasetaFolio.Coinciden("E-020485%2F26", "E-020485/26"), "La ruta codificada coincide con el folio del cuerpo (el caso de cada actualización)");
Check(CasetaFolio.Coinciden("E-020485%252F26", "E-020485/26"), "La ruta doblemente codificada también");
Check(CasetaFolio.Coinciden("e-020485%2f26", "E-020485/26"), "Mayúsculas y minúsculas no cuentan");
Check(CasetaFolio.Coinciden("E-020485/26", "E-020485/26"), "Un folio igual en ambos lados coincide");
Check(CasetaFolio.Coinciden("E-020485%2F26", ""), "Un cuerpo sin folio no contradice la ruta");
Check(CasetaFolio.Coinciden("E-020485%2F26", null), "Un cuerpo nulo tampoco");

// ── Lo que la validación debe seguir impidiendo ───────────────────────────────────────────────────
Check(!CasetaFolio.Coinciden("E-020485%2F26", "E-020486/26"), "Otro folio en el cuerpo sigue rechazándose");
Check(!CasetaFolio.Coinciden("E-020485%2F26", "E-020485/27"), "Otro año sigue rechazándose");

Console.WriteLine($"CasetaFolio.Regression: {checks} comprobaciones correctas.");
