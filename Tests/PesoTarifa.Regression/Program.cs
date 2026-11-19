using PMMovil.Api.Services;

// Regresiones de la regla "al capturar Peso lb o Peso kg en la partida, ncanttarifa toma el peso de la UMT elegida"
// (Revisión normal). Sin servidor ni base de datos: prueba la función pura RevisionPesoTarifa.
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");

// ── UMT en kilos: la cantidad tarifa son los kilos (libras x 0.4536) ──────────

Equal<decimal?>(45.36m, RevisionPesoTarifa.CantidadTarifaPorPeso("KG", 100m), "100 lb con UMT KG = 45.36 kg");
Equal<decimal?>(0m, RevisionPesoTarifa.CantidadTarifaPorPeso("KG", 0m), "Peso en cero deja la cantidad tarifa en cero");
Equal<decimal?>(10.000m, RevisionPesoTarifa.CantidadTarifaPorPeso("KG", 10m / 0.4536m), "Si en la pantalla se capturaron 10 kg (se mandan como libras), la cantidad tarifa es 10 kg exactos");
Equal<decimal?>(123.450m, RevisionPesoTarifa.CantidadTarifaPorPeso("KG", 123.45m / 0.4536m), "123.45 kg capturados regresan como 123.45");
Equal<decimal?>(45.36m, RevisionPesoTarifa.CantidadTarifaPorPeso("  kg ", 100m), "Espacios y minúsculas no importan");
Equal<decimal?>(45.36m, RevisionPesoTarifa.CantidadTarifaPorPeso("KGM", 100m), "KGM (código de aduana) también es kilos");
Equal<decimal?>(45.36m, RevisionPesoTarifa.CantidadTarifaPorPeso("KILOS", 100m), "KILOS también es kilos");

// ── UMT en libras: la cantidad tarifa son las libras tal cual ──────────────────

Equal<decimal?>(100m, RevisionPesoTarifa.CantidadTarifaPorPeso("LB", 100m), "100 lb con UMT LB = 100");
Equal<decimal?>(22.046m, RevisionPesoTarifa.CantidadTarifaPorPeso("lb", 22.0462m), "Se redondea a 3 decimales, como la regla legacy");
Equal<decimal?>(100m, RevisionPesoTarifa.CantidadTarifaPorPeso("LBR", 100m), "LBR (código de aduana) también es libras");
Equal<decimal?>(100m, RevisionPesoTarifa.CantidadTarifaPorPeso("LIBRAS", 100m), "LIBRAS también es libras");

// ── Cualquier otra UMT: el peso no define la cantidad tarifa y no se toca ─────

Check(RevisionPesoTarifa.CantidadTarifaPorPeso("L", 100m) is null, "Litros no se tocan");
Check(RevisionPesoTarifa.CantidadTarifaPorPeso("PZA", 100m) is null, "Piezas no se tocan");
Check(RevisionPesoTarifa.CantidadTarifaPorPeso("M2", 100m) is null, "Metros cuadrados no se tocan");
Check(RevisionPesoTarifa.CantidadTarifaPorPeso("", 100m) is null, "Sin UMT no se toca");
Check(RevisionPesoTarifa.CantidadTarifaPorPeso(null, 100m) is null, "UMT nula no se toca");
Check(RevisionPesoTarifa.CantidadTarifaPorPeso("KGX", 100m) is null, "Coincidencia exacta de código, no por texto");

Console.WriteLine($"OK: {checks} comprobaciones de la regla peso -> ncanttarifa.");
