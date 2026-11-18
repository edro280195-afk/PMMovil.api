using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PMMovil.Api.Services;

// Regresiones del almacén de fotos de Caseta (CasetaPhotoStorage): pendientes → 23 → caliente 48 h desde el último
// movimiento, miniatura y vista, bajas, reemplazos y la regla de no quitar de lo caliente nada que no esté confirmado en el
// 23. Sin servidor ni base de datos: el "23", la cola y lo caliente son carpetas temporales que se borran al terminar.
//
// Modo medición (opcional y SOLO LECTURA sobre el 23 real): CASETA_FOTOS_23=<carpeta Caseta del 23>,
// CASETA_FOTOS_FOLIO=E-021107/26 y CASETA_FOTOS_INDICES=1-7. Lee esas fotos como lo haría el API (la primera vez del 23,
// después de lo caliente), genera miniatura y vista en una carpeta temporal y reporta tiempos y pesos. No escribe en el 23.

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("Estas regresiones necesitan Windows (System.Drawing), igual que el servidor.");
    return 1;
}

int checks = 0;
void Check(bool condicion, string mensaje)
{
    if (!condicion) throw new InvalidOperationException(mensaje);
    checks++;
}

string raiz = Path.Combine(Path.GetTempPath(), "caseta-almacen-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(raiz);

try
{
    string medicion23 = Environment.GetEnvironmentVariable("CASETA_FOTOS_23") ?? string.Empty;
    if (medicion23.Length > 0)
    {
        await MedirContra23Async(medicion23, raiz);
        return 0;
    }

    await PruebasAsync();
    Console.WriteLine($"CasetaFotosAlmacen.Regression: {checks} comprobaciones correctas.");
    return 0;
}
finally
{
    try { Directory.Delete(raiz, recursive: true); } catch { }
}

// ── Pruebas ─────────────────────────────────────────────────────────────────────────────────────────────────────────

async Task PruebasAsync()
{
    var dirs = new Carpetas(raiz, "a");
    CasetaPhotoStorage almacen = Nuevo(dirs);
    Check(almacen.HotCacheActivo, "El almacén caliente debe quedar activo con una carpeta escribible.");
    Check(almacen.VariantesDisponibles, "En Windows hay miniatura y vista.");

    // 1) Llegan tres fotos: quedan pendientes y se pueden ver ya, con su miniatura, antes de llegar al 23.
    byte[] foto1 = FotoJpeg(1920, 2560, 1);
    byte[] foto2 = FotoJpeg(1920, 2560, 2);
    byte[] foto3 = FotoJpeg(1200, 1600, 3);
    await SubirAsync(almacen, "E-000001/26", (1, foto1), (2, foto2), (3, foto3));
    string n1 = almacen.GetFileName("E-000001/26", 1);
    string n2 = almacen.GetFileName("E-000001/26", 2);
    string n3 = almacen.GetFileName("E-000001/26", 3);
    Check(File.Exists(Path.Combine(dirs.Pendientes, n1)), "La foto nueva queda en pendientes.");
    Check(almacen.FindReadablePhotoPath("E-000001/26", 1, "jpg") == Path.Combine(dirs.Pendientes, n1), "Mientras no llega al 23 se lee de pendientes.");

    CasetaFotoArchivo? mini1 = await almacen.ResolverAsync("E-000001/26", 1, "jpg", CasetaFotoTamano.Mini);
    Check(mini1 is { EsVariante: true }, "La miniatura se genera desde la pendiente.");
    Check(mini1!.Ruta == almacen.RutaCaliente(n1, CasetaFotoTamano.Mini), "La miniatura vive en lo caliente.");
    (int ancho, int alto) = Medidas(mini1.Ruta);
    Check(Math.Max(ancho, alto) == FotoVariantes.MiniLado, $"La miniatura mide {FotoVariantes.MiniLado} px de lado mayor (midió {ancho}x{alto}).");
    Check(new FileInfo(mini1.Ruta).Length < foto1.Length / 10, "La miniatura pesa menos de la décima parte del original.");
    CasetaFotoArchivo? vista1 = await almacen.ResolverAsync("E-000001/26", 1, "jpg", CasetaFotoTamano.Vista);
    (ancho, alto) = Medidas(vista1!.Ruta);
    Check(vista1.EsVariante && Math.Max(ancho, alto) == FotoVariantes.VistaLado, $"La vista mide {FotoVariantes.VistaLado} px de lado mayor (midió {ancho}x{alto}).");
    Check(new FileInfo(vista1.Ruta).Length < foto1.Length / 2, "La vista pesa menos de la mitad del original.");
    (ancho, alto) = Medidas((await almacen.ResolverAsync("E-000001/26", 3, "jpg", CasetaFotoTamano.Vista))!.Ruta);
    Check(Math.Max(ancho, alto) == 1280, "Una foto de 1600 px también baja a 1280.");

    // 2) Se publica: el 23 la tiene completa y con la misma fecha (la versión), y lo local pasa a caliente.
    DateTime firma1 = File.GetLastWriteTimeUtc(Path.Combine(dirs.Pendientes, n1));
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    foreach ((string nombre, byte[] bytes) in new[] { (n1, foto1), (n2, foto2), (n3, foto3) })
    {
        string en23 = Path.Combine(dirs.El23, nombre);
        Check(File.Exists(en23) && new FileInfo(en23).Length == bytes.Length, $"{nombre} quedó completa en el 23.");
        Check(File.Exists(almacen.RutaCaliente(nombre, CasetaFotoTamano.Original)), $"{nombre} quedó caliente.");
        Check(!File.Exists(Path.Combine(dirs.Pendientes, nombre)), $"{nombre} salió de pendientes.");
    }

    Check(File.GetLastWriteTimeUtc(Path.Combine(dirs.El23, n1)) == firma1, "El 23 conserva la fecha de la versión (no la de la copia).");
    Check(File.GetLastWriteTimeUtc(almacen.RutaCaliente(n1, CasetaFotoTamano.Original)) == firma1, "Lo caliente conserva la fecha de la versión.");
    Check(almacen.FindReadablePhotoPath("E-000001/26", 1, "jpg") == almacen.RutaCaliente(n1, CasetaFotoTamano.Original), "Ya publicada se lee de lo caliente, no del 23.");
    Check(File.Exists(Path.Combine(dirs.Caliente, "actividad", "E-000001-26")), "El registro tiene su marca de último movimiento.");
    Check(Restos(dirs).Count == 0, "No quedan temporales: " + string.Join(", ", Restos(dirs)));

    DateTime miniAntes = File.GetLastWriteTimeUtc(mini1.Ruta);
    CasetaFotoArchivo? miniOtraVez = await almacen.ResolverAsync("E-000001/26", 1, "jpg", CasetaFotoTamano.Mini);
    Check(miniOtraVez!.Ruta == mini1.Ruta && File.GetLastWriteTimeUtc(miniOtraVez.Ruta) == miniAntes, "La miniatura sigue valiendo tras publicar (misma versión).");
    Check(almacen.EnumerateCasetaPhotos("E-000001/26").Count() == 3, "El listado del folio ve las tres (calientes).");

    // 3) Reemplazo del índice 2: invalida su vista y miniatura; se ve la nueva desde ya y el 23 la recibe.
    byte[] foto2b = FotoJpeg(1920, 2560, 22);
    await SubirAsync(almacen, "E-000001/26", (2, foto2b));
    Check(!File.Exists(almacen.RutaCaliente(n2, CasetaFotoTamano.Mini)) && !File.Exists(almacen.RutaCaliente(n2, CasetaFotoTamano.Vista)), "El reemplazo invalida vista y miniatura.");
    Check(almacen.FindReadablePhotoPath("E-000001/26", 2, "jpg") == Path.Combine(dirs.Pendientes, n2), "La versión nueva (pendiente) tapa a la caliente anterior.");
    CasetaFotoArchivo? mini2b = await almacen.ResolverAsync("E-000001/26", 2, "jpg", CasetaFotoTamano.Mini);
    Check(mini2b!.ModificadaUtc == File.GetLastWriteTimeUtc(Path.Combine(dirs.Pendientes, n2)), "La miniatura nueva es de la versión nueva.");
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    Check(new FileInfo(Path.Combine(dirs.El23, n2)).Length == foto2b.Length, "El 23 tiene la versión nueva.");
    Check(new FileInfo(almacen.RutaCaliente(n2, CasetaFotoTamano.Original)).Length == foto2b.Length, "Lo caliente tiene la versión nueva.");

    // 4) Baja del índice 3: lo caliente se va en el acto y nadie la vuelve a servir aunque el 23 aún la tenga.
    almacen.QueueDelete(n3);
    Check(!File.Exists(almacen.RutaCaliente(n3, CasetaFotoTamano.Original)), "La baja quita el original caliente en el acto.");
    Check(!File.Exists(almacen.RutaCaliente(n3, CasetaFotoTamano.Mini)) && !File.Exists(almacen.RutaCaliente(n3, CasetaFotoTamano.Vista)), "La baja quita vista y miniatura.");
    Check(File.Exists(Path.Combine(dirs.El23, n3)), "(El 23 todavía la tiene hasta el siguiente ciclo.)");
    Check(almacen.FindReadablePhotoPath("E-000001/26", 3, "jpg") == null, "Una foto dada de baja no se sirve aunque siga en el 23.");
    Check(await almacen.ResolverAsync("E-000001/26", 3, "jpg", CasetaFotoTamano.Mini) == null, "Ni su miniatura.");
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    Check(!File.Exists(Path.Combine(dirs.El23, n3)), "El ciclo borra la baja del 23.");
    Check(!Directory.EnumerateFiles(Path.Combine(dirs.Cola, "delete")).Any(), "Y retira la marca de baja.");
    Check(almacen.EnumerateCasetaPhotos("E-000001/26").Count() == 2, "El listado del folio ya no la ve.");

    // 5) A las 48 h sin movimiento: se quitan originales y vistas (el 23 los tiene); la miniatura se queda.
    CasetaLimpiezaResultado temprano = await almacen.LimpiarCalienteAsync(DateTime.UtcNow.AddHours(47));
    Check(temprano.Desalojadas == 0, "A las 47 h no se quita nada.");
    CasetaLimpiezaResultado limpieza = await almacen.LimpiarCalienteAsync(DateTime.UtcNow.AddHours(49));
    Check(limpieza.Desalojadas == 2, $"A las 49 h se quitan los dos originales confirmados (quitó {limpieza.Desalojadas}).");
    Check(!File.Exists(almacen.RutaCaliente(n1, CasetaFotoTamano.Original)) && !File.Exists(almacen.RutaCaliente(n1, CasetaFotoTamano.Vista)), "Original y vista salen de lo caliente.");
    Check(File.Exists(almacen.RutaCaliente(n1, CasetaFotoTamano.Mini)), "La miniatura se queda aunque pasen las 48 h.");
    Check(File.Exists(Path.Combine(dirs.El23, n1)) && File.Exists(Path.Combine(dirs.El23, n2)), "El 23 conserva todo.");

    // 6) Consulta vieja: la cuadrícula sale de la miniatura sin ir al 23; abrir la foto la trae una vez y la recalienta.
    CasetaFotoArchivo? miniVieja = await almacen.ResolverAsync("E-000001/26", 2, "jpg", CasetaFotoTamano.Mini);
    Check(miniVieja is { EsVariante: true } && miniVieja.Ruta == almacen.RutaCaliente(n2, CasetaFotoTamano.Mini), "Un registro viejo pinta su miniatura desde lo caliente.");
    Check(!File.Exists(almacen.RutaCaliente(n2, CasetaFotoTamano.Original)), "Pintar la miniatura no trae el original del 23.");
    CasetaFotoArchivo? vistaVieja = await almacen.ResolverAsync("E-000001/26", 2, "jpg", CasetaFotoTamano.Vista);
    Check(vistaVieja is { EsVariante: true }, "La vista de un registro viejo se genera leyendo el 23.");
    Check(File.Exists(almacen.RutaCaliente(n2, CasetaFotoTamano.Original)), "Y el original queda caliente otra vez.");
    Check(File.GetLastWriteTimeUtc(almacen.RutaCaliente(n2, CasetaFotoTamano.Original)) == File.GetLastWriteTimeUtc(Path.Combine(dirs.El23, n2)), "La copia recalentada conserva la versión del 23.");
    Check(DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(dirs.Caliente, "actividad", "E-000001-26")) < TimeSpan.FromMinutes(1), "Recalentar renueva el reloj del registro.");

    string? leida = almacen.FindReadablePhotoPath("E-000001/26", 1, "jpg");
    Check(leida == Path.Combine(dirs.El23, n1), "El original viejo se sirve del 23 la primera vez.");
    Check(await EsperarAsync(() => File.Exists(almacen.RutaCaliente(n1, CasetaFotoTamano.Original))), "Y en segundo plano se recalienta para la siguiente.");
    Check(almacen.FindReadablePhotoPath("E-000001/26", 1, "jpg") == almacen.RutaCaliente(n1, CasetaFotoTamano.Original), "La segunda lectura ya es local.");

    // 7) Nunca se quita de lo caliente algo que no esté confirmado en el 23.
    byte[] fotoSola = FotoJpeg(1200, 1600, 5);
    await SubirAsync(almacen, "E-000002/26", (1, fotoSola));
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    string nSola = almacen.GetFileName("E-000002/26", 1);
    File.Delete(Path.Combine(dirs.El23, nSola)); // alguien la quitó del 23
    CasetaLimpiezaResultado sinCopia = await almacen.LimpiarCalienteAsync(DateTime.UtcNow.AddHours(72));
    Check(sinCopia.SinCopiaEn23 >= 1, "La limpieza reporta la foto que el 23 no tiene.");
    Check(File.Exists(almacen.RutaCaliente(nSola, CasetaFotoTamano.Original)), "Y la conserva en lo caliente.");
    byte[] fotoDistinta = FotoJpeg(1200, 1600, 6);
    File.WriteAllBytes(Path.Combine(dirs.El23, nSola), fotoDistinta.Take(fotoDistinta.Length / 2).ToArray());
    File.SetLastWriteTimeUtc(Path.Combine(dirs.El23, nSola), File.GetLastWriteTimeUtc(almacen.RutaCaliente(nSola, CasetaFotoTamano.Original)));
    await almacen.LimpiarCalienteAsync(DateTime.UtcNow.AddHours(72));
    Check(File.Exists(almacen.RutaCaliente(nSola, CasetaFotoTamano.Original)), "Una copia del 23 de otro tamaño (y no más nueva) no cuenta como confirmada.");

    // 8) Las 48 h cuentan desde el último movimiento: una consulta a las 47 h le da otras 48.
    await SubirAsync(almacen, "E-000003/26", (1, FotoJpeg(1200, 1600, 7)));
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    string nReloj = almacen.GetFileName("E-000003/26", 1);
    string marca = Path.Combine(dirs.Caliente, "actividad", "E-000003-26");
    File.SetLastWriteTimeUtc(almacen.RutaCaliente(nReloj, CasetaFotoTamano.Original), DateTime.UtcNow.AddHours(-47));
    File.SetLastWriteTimeUtc(Path.Combine(dirs.El23, nReloj), DateTime.UtcNow.AddHours(-47));
    File.SetLastWriteTimeUtc(marca, DateTime.UtcNow.AddHours(-47));
    almacen.FindReadablePhotoPath("E-000003/26", 1, "jpg");
    await almacen.LimpiarCalienteAsync(DateTime.UtcNow.AddHours(2));
    Check(File.Exists(almacen.RutaCaliente(nReloj, CasetaFotoTamano.Original)), "Consultada a las 47 h, sigue caliente 2 h después.");
    File.SetLastWriteTimeUtc(marca, DateTime.UtcNow.AddHours(-47));
    await almacen.LimpiarCalienteAsync(DateTime.UtcNow.AddHours(2));
    Check(!File.Exists(almacen.RutaCaliente(nReloj, CasetaFotoTamano.Original)), "Sin esa consulta, a las 49 h se habría quitado.");

    // 9) Diez fotos se publican a la vez y todas llegan.
    var diez = Enumerable.Range(1, 10).Select(i => (i, FotoJpeg(800, 1000, 100 + i))).ToArray();
    await SubirAsync(almacen, "E-000004/26", diez);
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    Check(diez.All(f => File.Exists(Path.Combine(dirs.El23, almacen.GetFileName("E-000004/26", f.i)))), "Las diez llegaron al 23.");
    Check(!Directory.EnumerateFiles(dirs.Pendientes).Any(), "Y pendientes quedó vacío.");
    Check(Restos(dirs).Count == 0, "Sin temporales tras publicar en paralelo: " + string.Join(", ", Restos(dirs)));

    // 10) Una pendiente apartada que quedó a medias (proceso caído) vuelve a la cola y se publica.
    byte[] rescatada = FotoJpeg(800, 1000, 200);
    string nRescate = almacen.GetFileName("E-000005/26", 1);
    File.WriteAllBytes(Path.Combine(dirs.Pendientes, $"{nRescate}.{Guid.NewGuid():N}.publicando"), rescatada);
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    Check(File.Exists(Path.Combine(dirs.El23, nRescate)) && new FileInfo(Path.Combine(dirs.El23, nRescate)).Length == rescatada.Length, "La apartada huérfana se rescata y llega al 23.");

    // 11) Una foto que no se puede leer se sirve tal cual: la app siempre ve algo.
    byte[] rota = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 0xFF, 0xD9];
    await SubirAsync(almacen, "E-000006/26", (1, rota));
    CasetaFotoArchivo? deRota = await almacen.ResolverAsync("E-000006/26", 1, "jpg", CasetaFotoTamano.Mini);
    Check(deRota is { EsVariante: false } && deRota.Ruta == Path.Combine(dirs.Pendientes, almacen.GetFileName("E-000006/26", 1)), "Sin miniatura posible se sirve el original.");
    Check(FotoVariantes.Generar(rota) == null, "FotoVariantes no revienta con una imagen rota.");

    // 12) EXIF de orientación (fotos que no pasaron por la app): la miniatura sale derecha.
    byte[]? girada = JpegConOrientacion(1000, 600, orientacion: 6);
    if (girada != null)
    {
        FotoVariantesResultado? r = FotoVariantes.Generar(girada);
        (int w, int h) = MedidasDeBytes(r!.Mini);
        Check(h > w, $"Una foto apaisada con EXIF 6 (girar 90°) da miniatura vertical (dio {w}x{h}).");
    }
    else
    {
        Console.WriteLine("  (GDI+ no guardó el EXIF de prueba: se omite la comprobación de orientación)");
    }

    // 13) Con lo caliente apagado todo funciona como antes: se publica, se borra la local y se sirve el original del 23.
    var dirsB = new Carpetas(raiz, "b");
    CasetaPhotoStorage apagado = Nuevo(dirsB, ("CasetaPhotoSettings:HotCacheEnabled", "false"));
    Check(!apagado.HotCacheActivo && !apagado.VariantesDisponibles, "HotCacheEnabled=false apaga el almacén caliente.");
    await SubirAsync(apagado, "E-000007/26", (1, FotoJpeg(800, 1000, 300)));
    await apagado.PublishPendingPhotosAsync(CancellationToken.None);
    string nApagado = apagado.GetFileName("E-000007/26", 1);
    Check(File.Exists(Path.Combine(dirsB.El23, nApagado)) && !File.Exists(Path.Combine(dirsB.Pendientes, nApagado)), "Apagado: llega al 23 y sale de pendientes.");
    Check(!Directory.Exists(dirsB.Caliente) || !Directory.EnumerateFiles(dirsB.Caliente, "*", SearchOption.AllDirectories).Any(), "Apagado: no escribe nada caliente.");
    CasetaFotoArchivo? originalApagado = await apagado.ResolverAsync("E-000007/26", 1, "jpg", CasetaFotoTamano.Mini);
    Check(originalApagado is { EsVariante: false } && originalApagado.Ruta == Path.Combine(dirsB.El23, nApagado), "Apagado: pedir miniatura sirve el original, como antes.");

    // 14) Si la carpeta caliente no se puede escribir, se apaga solo (y lo dice el log) en vez de fallar.
    string archivoEstorbo = Path.Combine(raiz, "no-es-carpeta.txt");
    File.WriteAllText(archivoEstorbo, "x");
    CasetaPhotoStorage sinPermiso = Nuevo(new Carpetas(raiz, "c"), ("CasetaPhotoSettings:HotPath", Path.Combine(archivoEstorbo, "caliente")));
    Check(!sinPermiso.HotCacheActivo, "Una carpeta caliente imposible deja el almacén apagado.");

    // 15) Versión y caché HTTP.
    Check(CasetaFotoCache.Version(0, 123) == string.Empty, "Sin idfotos no hay versión.");
    Check(CasetaFotoCache.Version(255, 4096) == "ff-1000", "La versión es idfotos-fcrea en hexadecimal.");
    Check(CasetaFotoCache.Version(255, -5) == "ff-0", "Una fecha cero o negativa no rompe la versión.");
    Check(CasetaFotoCache.CacheControl("ff-1000", "ff-1000") == CasetaFotoCache.ParaSiempre, "Con la versión vigente se guarda para siempre.");
    Check(CasetaFotoCache.CacheControl("FF-1000 ", "ff-1000") == CasetaFotoCache.ParaSiempre, "La versión se compara sin importar mayúsculas ni espacios.");
    Check(CasetaFotoCache.CacheControl(null, "ff-1000") == CasetaFotoCache.Preguntar, "Sin versión (apps anteriores) se pregunta cada vez.");
    Check(CasetaFotoCache.CacheControl("fe-1000", "ff-1000") == CasetaFotoCache.Preguntar, "Con una versión vieja se pregunta.");
    Check(CasetaFotoCache.CacheControl("", "") == CasetaFotoCache.Preguntar, "Sin fila en la base se pregunta.");
    Check(CasetaFotoCache.Tamano("mini") == CasetaFotoTamano.Mini && CasetaFotoCache.Tamano(" VISTA ") == CasetaFotoTamano.Vista, "mini y vista se reconocen.");
    Check(CasetaFotoCache.Tamano(null) == CasetaFotoTamano.Original && CasetaFotoCache.Tamano("otra") == CasetaFotoTamano.Original, "Cualquier otra cosa es el original.");
}

// ── Medición contra el 23 real (solo lectura) ───────────────────────────────────────────────────────────────────────

async Task MedirContra23Async(string carpeta23, string temporal)
{
    string folio = Environment.GetEnvironmentVariable("CASETA_FOTOS_FOLIO") ?? throw new InvalidOperationException("Falta CASETA_FOTOS_FOLIO.");
    string rango = Environment.GetEnvironmentVariable("CASETA_FOTOS_INDICES") ?? "1-7";
    int[] partes = rango.Split('-').Select(int.Parse).ToArray();
    int[] indices = Enumerable.Range(partes[0], partes[^1] - partes[0] + 1).ToArray();

    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["CasetaPhotoSettings:BasePath"] = carpeta23,
        ["CasetaPhotoSettings:FallbackPath"] = Path.Combine(temporal, "respaldo"),
        ["CasetaPhotoSettings:QueuePath"] = Path.Combine(temporal, "cola"),
        ["CasetaPhotoSettings:HotPath"] = Path.Combine(temporal, "caliente")
    }).Build();
    var almacen = new CasetaPhotoStorage(config, NullLogger<CasetaPhotoStorage>.Instance);
    Console.WriteLine($"Midiendo {folio} índices {rango} contra {carpeta23} (solo lectura; lo caliente va a una carpeta temporal).");

    long totalOriginal = 0, totalMini = 0, totalVista = 0;
    double msMiniFrio = 0, msMiniCaliente = 0, msOriginalFrio = 0, msOriginalCaliente = 0;
    foreach (int indice in indices)
    {
        var reloj = Stopwatch.StartNew();
        CasetaFotoArchivo? mini = await almacen.ResolverAsync(folio, indice, "jpg", CasetaFotoTamano.Mini);
        double primera = reloj.Elapsed.TotalMilliseconds;
        if (mini == null)
        {
            Console.WriteLine($"  {indice}: no está en el 23");
            continue;
        }

        reloj.Restart();
        await almacen.ResolverAsync(folio, indice, "jpg", CasetaFotoTamano.Mini);
        double segunda = reloj.Elapsed.TotalMilliseconds;
        CasetaFotoArchivo vista = (await almacen.ResolverAsync(folio, indice, "jpg", CasetaFotoTamano.Vista))!;
        string nombre = almacen.GetFileName(folio, indice);
        long original = new FileInfo(Path.Combine(carpeta23, nombre)).Length;
        long pesoMini = new FileInfo(mini.Ruta).Length;
        long pesoVista = new FileInfo(vista.Ruta).Length;

        // Lectura completa del original: del 23 (como hoy) contra la copia caliente que dejó la miniatura.
        reloj.Restart();
        await File.ReadAllBytesAsync(Path.Combine(carpeta23, nombre));
        double original23 = reloj.Elapsed.TotalMilliseconds;
        reloj.Restart();
        string local = almacen.FindReadablePhotoPath(folio, indice, "jpg")!;
        await File.ReadAllBytesAsync(local);
        double originalCaliente = reloj.Elapsed.TotalMilliseconds;

        totalOriginal += original; totalMini += pesoMini; totalVista += pesoVista;
        msMiniFrio += primera; msMiniCaliente += segunda; msOriginalFrio += original23; msOriginalCaliente += originalCaliente;
        Console.WriteLine($"  {indice}: original {original / 1024,5:N0} KB | mini {pesoMini / 1024,3:N0} KB | vista {pesoVista / 1024,4:N0} KB | mini 1a vez {primera,6:N0} ms, después {segunda,5:N1} ms | original del 23 {original23,6:N0} ms, caliente {originalCaliente,5:N1} ms");
    }

    Console.WriteLine($"TOTAL: original {totalOriginal / 1024:N0} KB, miniaturas {totalMini / 1024:N0} KB ({(double)totalMini / Math.Max(1, totalOriginal):P1}), vistas {totalVista / 1024:N0} KB ({(double)totalVista / Math.Max(1, totalOriginal):P1})");
    Console.WriteLine($"TIEMPO (desde esta máquina): miniaturas 1a vez {msMiniFrio:N0} ms, después {msMiniCaliente:N1} ms | originales del 23 {msOriginalFrio:N0} ms, calientes {msOriginalCaliente:N1} ms");
}

// ── Ayudantes ───────────────────────────────────────────────────────────────────────────────────────────────────────

CasetaPhotoStorage Nuevo(Carpetas dirs, params (string Clave, string Valor)[] extra)
{
    var valores = new Dictionary<string, string?>
    {
        ["CasetaPhotoSettings:BasePath"] = dirs.El23,
        ["CasetaPhotoSettings:FallbackPath"] = dirs.Respaldo,
        ["CasetaPhotoSettings:QueuePath"] = dirs.Cola,
        ["CasetaPhotoSettings:HotPath"] = dirs.Caliente,
        ["CasetaPhotoSettings:HotHours"] = "48",
        ["CasetaPhotoSettings:ThumbnailDays"] = "365",
        ["CasetaPhotoSettings:PublishParallelism"] = "3"
    };
    foreach ((string clave, string valor) in extra) valores[clave] = valor;
    IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
    Directory.CreateDirectory(dirs.El23);
    return new CasetaPhotoStorage(config, NullLogger<CasetaPhotoStorage>.Instance);
}

static async Task SubirAsync(CasetaPhotoStorage almacen, string casetaId, params (int Indice, byte[] Bytes)[] fotos)
{
    var etapas = new List<CasetaPhotoStage>();
    foreach ((int indice, byte[] bytes) in fotos)
    {
        etapas.Add(await almacen.StageAsync(bytes, almacen.GetFileName(casetaId, indice), CancellationToken.None));
    }

    almacen.ActivateStagedPhotos(etapas);
}

static List<string> Restos(Carpetas dirs)
{
    string[] sufijos = [".tmp", ".publicando", ".uploading"];
    return new[] { dirs.Pendientes, dirs.El23, dirs.Caliente }
        .Where(Directory.Exists)
        .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
        .Where(f => sufijos.Any(s => f.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
        .Select(f => Path.GetFileName(f))
        .ToList();
}

static async Task<bool> EsperarAsync(Func<bool> condicion)
{
    for (int i = 0; i < 100; i++)
    {
        if (condicion()) return true;
        await Task.Delay(50);
    }

    return condicion();
}

static (int Ancho, int Alto) Medidas(string ruta)
{
    using var imagen = Image.FromFile(ruta);
    return (imagen.Width, imagen.Height);
}

static (int Ancho, int Alto) MedidasDeBytes(byte[] bytes)
{
    using var ms = new MemoryStream(bytes);
    using var imagen = Image.FromStream(ms);
    return (imagen.Width, imagen.Height);
}

static byte[] FotoJpeg(int ancho, int alto, int semilla, int calidad = 92)
{
    using var bmp = new Bitmap(ancho, alto, PixelFormat.Format24bppRgb);
    using (var g = Graphics.FromImage(bmp))
    {
        using var fondo = new LinearGradientBrush(
            new Rectangle(0, 0, ancho, alto),
            Color.FromArgb(40 + semilla % 100, 90, 140),
            Color.FromArgb(220, 200 - semilla % 50, 120),
            35f);
        g.FillRectangle(fondo, 0, 0, ancho, alto);
        var azar = new Random(semilla);
        for (int i = 0; i < 60; i++)
        {
            using var pincel = new SolidBrush(Color.FromArgb(azar.Next(256), azar.Next(256), azar.Next(256)));
            g.FillRectangle(pincel, azar.Next(ancho), azar.Next(alto), azar.Next(20, Math.Max(21, ancho / 4)), azar.Next(20, Math.Max(21, alto / 6)));
        }

        using var fuente = new Font(FontFamily.GenericSansSerif, Math.Max(12, ancho / 25f), FontStyle.Bold);
        g.DrawString($"CAJA {semilla:D5} SELLO 4471", fuente, Brushes.Black, ancho / 10f, alto / 2f);
    }

    // Ruido de sensor: una foto real nunca es lisa, y sin él el JPEG de prueba pesaría casi nada.
    BitmapData datos = bmp.LockBits(new Rectangle(0, 0, ancho, alto), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
    int largo = Math.Abs(datos.Stride) * alto;
    byte[] pixeles = new byte[largo];
    Marshal.Copy(datos.Scan0, pixeles, 0, largo);
    var ruido = new Random(semilla * 7 + 1);
    for (int i = 0; i < pixeles.Length; i++) pixeles[i] = (byte)Math.Clamp(pixeles[i] + ruido.Next(-10, 11), 0, 255);
    Marshal.Copy(pixeles, 0, datos.Scan0, largo);
    bmp.UnlockBits(datos);
    return Jpeg(bmp, calidad);
}

static byte[]? JpegConOrientacion(int ancho, int alto, short orientacion)
{
    using var bmp = new Bitmap(ancho, alto, PixelFormat.Format24bppRgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.Clear(Color.SteelBlue);
        g.FillRectangle(Brushes.Orange, 0, 0, ancho / 3, alto);
    }

    var propiedad = (PropertyItem)RuntimeHelpers.GetUninitializedObject(typeof(PropertyItem));
    propiedad.Id = 0x0112;
    propiedad.Type = 3;
    propiedad.Len = 2;
    propiedad.Value = BitConverter.GetBytes(orientacion);
    bmp.SetPropertyItem(propiedad);
    byte[] jpeg = Jpeg(bmp, 90);

    using var ms = new MemoryStream(jpeg);
    using var leida = Image.FromStream(ms);
    return leida.PropertyIdList.Contains(0x0112) ? jpeg : null;
}

static byte[] Jpeg(Image imagen, int calidad)
{
    ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    using var parametros = new EncoderParameters(1);
    parametros.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)calidad);
    using var salida = new MemoryStream();
    imagen.Save(salida, codec, parametros);
    return salida.ToArray();
}

sealed record Carpetas(string Raiz, string Nombre)
{
    public string El23 => Path.Combine(Raiz, Nombre, "23");
    public string Respaldo => Path.Combine(Raiz, Nombre, "respaldo");
    public string Cola => Path.Combine(Raiz, Nombre, "cola");
    public string Pendientes => Path.Combine(Cola, "pending");
    public string Caliente => Path.Combine(Raiz, Nombre, "caliente");
}
