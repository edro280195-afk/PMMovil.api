using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PMMovil.Api.Services;

// Regresiones del almacén de fotos de Entrada Bodega y Revisión (ClientePhotoStorage): las fotos que viven en la carpeta
// del cliente en el 23 (Fotos\CLIENTE\REF-1071.jpg) pasan por la misma cola y el mismo almacén caliente que Caseta. Aquí se
// prueba lo propio: el nombre local con idfotos (la línea se reusa), la copia a la carpeta del cliente, las bajas en el acto
// con marca que solo alcanza a su versión, el nombre anterior de los archivos, el modo directo (sin cola, como antes) y la
// limpieza a las 48 h. Sin servidor ni base de datos: el "23", la cola y lo caliente son carpetas temporales.

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

string raiz = Path.Combine(Path.GetTempPath(), "cliente-almacen-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(raiz);

try
{
    await ColaYCalienteAsync();
    await LineaReusadaAsync();
    await BajasAsync();
    await NombreAnteriorAsync();
    await ModoDirectoAsync();
    await LimpiezaAsync();
    Console.WriteLine($"ClienteFotosAlmacen.Regression: {checks} comprobaciones correctas.");
    return 0;
}
finally
{
    try { Directory.Delete(raiz, recursive: true); } catch { }
}

// ── Pruebas ─────────────────────────────────────────────────────────────────────────────────────────────────────────

async Task ColaYCalienteAsync()
{
    var dirs = new Carpetas(raiz, "a");
    ClientePhotoStorage almacen = Nuevo(dirs);
    Check(!almacen.ColaActiva, "Sin trabajador corriendo no se usa la cola: nadie la copiaría al 23.");
    Check(!almacen.HotCacheActivo, "Ni lo caliente.");
    almacen.MarcarTrabajadorActivo();
    Check(almacen.ColaActiva && almacen.HotCacheActivo && almacen.VariantesDisponibles, "Con trabajador: cola, caliente y variantes activas.");

    // 1) Llega una foto de entrada: se prepara en local (no toca el 23) y al tener su fila se activa con su idfotos.
    byte[] foto = FotoJpeg(1920, 2560, 1);
    FotoClientePreparada preparada = await almacen.PrepararAsync(foto, "FREUD", "01I26106450", 100, 1, "jpg");
    Check(!preparada.Directa && preparada.Archivo == "01I26106450-1001.jpg", "Con cola la foto se prepara en local, con el nombre que lee FoxPro.");
    Check(!Directory.Exists(Path.Combine(dirs.El23, "FREUD")), "Preparar no toca el 23 (antes la petición esperaba esa copia).");
    almacen.Activar(preparada, 501);
    string local = "FREUD~01I26106450-1001~501.jpg";
    Check(File.Exists(Path.Combine(dirs.Pendientes, local)), "Activada queda en pendientes como CARPETA~ARCHIVO~idfotos.");
    Check(!File.Exists(preparada.Ruta), "La preparación ya no queda.");

    var f501 = new FotoCliente(501, "FREUD", "01I26106450", 100, 1, "jpg");
    Check((await almacen.ResolverAsync(f501, FotoTamano.Original))?.Ruta == Path.Combine(dirs.Pendientes, local), "Recién subida se sirve de pendientes, antes de llegar al 23.");
    FotoArchivo? mini = await almacen.ResolverAsync(f501, FotoTamano.Mini);
    Check(mini is { EsVariante: true }, "La miniatura sale desde ya.");
    (int ancho, int alto) = Medidas(mini!.Ruta);
    Check(Math.Max(ancho, alto) == FotoVariantes.MiniLado, $"La miniatura mide {FotoVariantes.MiniLado} px de lado mayor (midió {ancho}x{alto}).");
    FotoArchivo? vista = await almacen.ResolverAsync(f501, FotoTamano.Vista);
    (ancho, alto) = Medidas(vista!.Ruta);
    Check(vista.EsVariante && Math.Max(ancho, alto) == FotoVariantes.VistaLado, "La vista mide 1280 px de lado mayor.");
    Check(almacen.BuscarOriginal(f501) == Path.Combine(dirs.Pendientes, local), "Los enlaces, el ZIP y el PDF también la ven antes de que llegue al 23.");

    // 2) Se publica: llega a la carpeta del cliente en el 23, con su fecha (la versión), y lo local pasa a caliente.
    DateTime firma = File.GetLastWriteTimeUtc(Path.Combine(dirs.Pendientes, local));
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    string en23 = Path.Combine(dirs.El23, "FREUD", "01I26106450-1001.jpg");
    Check(File.Exists(en23) && File.ReadAllBytes(en23).SequenceEqual(foto), "Llega completa al 23, en la carpeta del cliente y con el nombre que lee FoxPro.");
    Check(File.GetLastWriteTimeUtc(en23) == firma, "El 23 conserva la fecha de la versión.");
    Check(!File.Exists(Path.Combine(dirs.Pendientes, local)), "Sale de pendientes.");
    Check(File.Exists(almacen.RutaCaliente(local, FotoTamano.Original)), "Y se queda caliente.");
    Check((await almacen.ResolverAsync(f501, FotoTamano.Original))!.Ruta == almacen.RutaCaliente(local, FotoTamano.Original), "Ya publicada se lee de lo caliente, no del 23.");
    Check((await almacen.ResolverAsync(f501, FotoTamano.Mini))!.Ruta == mini.Ruta, "La miniatura sigue valiendo tras publicar (misma versión).");
    Check(File.Exists(Path.Combine(dirs.Caliente, "actividad", "FREUD~01I26106450")), "El reloj de 48 h es del registro (carpeta + referencia), no de cada foto.");
    Check(Restos(dirs).Count == 0, "No quedan temporales: " + string.Join(", ", Restos(dirs)));

    // 3) La fila no entró: la foto preparada se descarta y no deja nada.
    FotoClientePreparada sobra = await almacen.PrepararAsync(FotoJpeg(800, 600, 9), "FREUD", "01I26106450", 100, 2, "jpg");
    almacen.Descartar(sobra);
    Check(!File.Exists(sobra.Ruta) && !Directory.EnumerateFiles(dirs.Pendientes).Any(), "Una foto descartada no deja nada.");

    // 4) Sin idfotos (no debería pasar): no se pierde, se copia directo al 23.
    FotoClientePreparada sinId = await almacen.PrepararAsync(FotoJpeg(800, 600, 10), "FREUD", "01I26106450", 107, 1, "jpg");
    almacen.Activar(sinId, 0);
    Check(File.Exists(Path.Combine(dirs.El23, "FREUD", "01I26106450-1071.jpg")) && !File.Exists(sinId.Ruta), "Activada sin idfotos se copia directo al 23.");
}

async Task LineaReusadaAsync()
{
    var dirs = new Carpetas(raiz, "b");
    ClientePhotoStorage almacen = Nuevo(dirs);
    almacen.MarcarTrabajadorActivo();

    byte[] vieja = FotoJpeg(1600, 1200, 21);
    await SubirAsync(almacen, vieja, "ACME", "01I26100001", 107, 3, 700);
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    var f700 = new FotoCliente(700, "ACME", "01I26100001", 107, 3, "jpg");
    Check((await almacen.ResolverAsync(f700, FotoTamano.Original))!.Ruta.StartsWith(dirs.Caliente), "La foto 700 queda caliente.");
    FotoArchivo? mini700 = await almacen.ResolverAsync(f700, FotoTamano.Mini);

    // FoxPro (o la captura de etiquetas) borró esa fila y escribió otra foto con la misma línea, directo en el 23.
    string en23 = Path.Combine(dirs.El23, "ACME", "01I26100001-1073.jpg");
    byte[] deFoxPro = FotoJpeg(1600, 1200, 22);
    File.WriteAllBytes(en23, deFoxPro);
    var f701 = new FotoCliente(701, "ACME", "01I26100001", 107, 3, "jpg");
    Check((await almacen.ResolverAsync(f701, FotoTamano.Original))?.Ruta == en23, "La fila nueva (701) se sirve del 23 aunque la 700 siga caliente con el mismo nombre de archivo.");
    FotoArchivo? mini701 = await almacen.ResolverAsync(f701, FotoTamano.Mini);
    Check(mini701 is { EsVariante: true } && mini701.Ruta != mini700!.Ruta, "Su miniatura es suya, no la de la 700.");
    Check(mini701!.ModificadaUtc == File.GetLastWriteTimeUtc(en23), "Y es de la versión que está en el 23 (misma fecha).");

    // La app borra la 701 y toma otra: misma línea (MAX + 1) e idfotos nuevo.
    almacen.Borrar(f701);
    Check(!File.Exists(en23), "La baja borra del 23 en el acto.");
    byte[] otra = FotoJpeg(1600, 1200, 23);
    await SubirAsync(almacen, otra, "ACME", "01I26100001", 107, 3, 702);
    var f702 = new FotoCliente(702, "ACME", "01I26100001", 107, 3, "jpg");
    Check((await almacen.ResolverAsync(f702, FotoTamano.Original))!.Ruta == Path.Combine(dirs.Pendientes, "ACME~01I26100001-1073~702.jpg"), "La nueva se ve desde pendientes.");
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    Check(File.ReadAllBytes(en23).SequenceEqual(otra), "El 23 queda con la nueva.");
    Check(!Directory.Exists(dirs.Bajas) || !Directory.EnumerateFiles(dirs.Bajas).Any(), "No quedan marcas de baja.");
}

async Task BajasAsync()
{
    // a) Baja normal: lo local y el 23 en el acto, sin marca que se quede.
    var dirs = new Carpetas(raiz, "c");
    ClientePhotoStorage almacen = Nuevo(dirs);
    almacen.MarcarTrabajadorActivo();
    await SubirAsync(almacen, FotoJpeg(1200, 900, 31), "ZETA", "01I26100002", 100, 1, 800);
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    var f800 = new FotoCliente(800, "ZETA", "01I26100002", 100, 1, "jpg");
    await almacen.ResolverAsync(f800, FotoTamano.Mini);
    string local = "ZETA~01I26100002-1001~800.jpg";
    almacen.Borrar(f800);
    Check(!File.Exists(Path.Combine(dirs.El23, "ZETA", "01I26100002-1001.jpg")), "La baja quita la foto del 23 en el acto (como siempre en Entrada y Revisión).");
    Check(!File.Exists(almacen.RutaCaliente(local, FotoTamano.Original)) && !File.Exists(almacen.RutaCaliente(local, FotoTamano.Mini)), "Y lo caliente con su miniatura.");
    Check(!Directory.EnumerateFiles(dirs.Bajas).Any(), "Sin copia en camino no queda marca de baja.");
    Check(await almacen.ResolverAsync(f800, FotoTamano.Original) == null, "Ya no se sirve.");

    // b) Baja de una foto que aún no llegaba al 23: nunca llega.
    await SubirAsync(almacen, FotoJpeg(1200, 900, 32), "ZETA", "01I26100002", 100, 2, 801);
    almacen.Borrar(new FotoCliente(801, "ZETA", "01I26100002", 100, 2, "jpg"));
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    Check(!File.Exists(Path.Combine(dirs.El23, "ZETA", "01I26100002-1002.jpg")), "Una pendiente borrada nunca llega al 23.");

    // c) El 23 no contesta al borrar (aquí: su carpeta raíz no existe): la marca se queda con la versión borrada.
    var dirsD = new Carpetas(raiz, "d");
    ClientePhotoStorage sinRed = Nuevo(dirsD, crear23: false);
    sinRed.MarcarTrabajadorActivo();
    byte[] borrada = FotoJpeg(1200, 900, 33);
    await SubirAsync(sinRed, borrada, "ZETA", "01I26100002", 100, 3, 810);
    var f810 = new FotoCliente(810, "ZETA", "01I26100002", 100, 3, "jpg");
    sinRed.Borrar(f810);
    string marca = Path.Combine(dirsD.Bajas, "ZETA~01I26100002-1003~810.jpg.delete");
    Check(File.Exists(marca) && File.ReadAllText(marca).StartsWith("v1|", StringComparison.Ordinal), "Si el 23 no contesta, la marca se queda con la versión borrada para reintentar.");
    Check(await sinRed.ResolverAsync(f810, FotoTamano.Original) == null, "Mientras tanto no se sirve.");

    // Vuelve el 23 y en esa ruta ya hay OTRA foto (alguien reusó la línea): no es la que se borró y no se toca.
    string ruta23 = Path.Combine(dirsD.El23, "ZETA", "01I26100002-1003.jpg");
    Directory.CreateDirectory(Path.GetDirectoryName(ruta23)!);
    byte[] ajena = FotoJpeg(1200, 900, 34);
    File.WriteAllBytes(ruta23, ajena);
    await sinRed.PublishPendingPhotosAsync(CancellationToken.None);
    Check(File.Exists(ruta23) && File.ReadAllBytes(ruta23).SequenceEqual(ajena), "Otra foto en la misma ruta del 23 sobrevive a la baja pendiente.");
    Check(!File.Exists(marca), "Y la marca se retira.");

    // Si lo que hay en el 23 es exactamente la versión borrada (la copia que iba en camino), sí se quita.
    File.WriteAllBytes(ruta23, borrada);
    DateTime firmaBorrada = DateTime.UtcNow.AddMinutes(-3);
    File.SetLastWriteTimeUtc(ruta23, firmaBorrada);
    File.WriteAllText(marca, $"v1|{borrada.Length}|{firmaBorrada.Ticks}");
    await sinRed.PublishPendingPhotosAsync(CancellationToken.None);
    Check(!File.Exists(ruta23) && !File.Exists(marca), "La copia de la versión borrada se quita del 23 y la marca se retira.");

    // Una marca sin versión (no había copia local) borra lo que haya en esa ruta, como siempre.
    File.WriteAllBytes(ruta23, ajena);
    File.WriteAllText(marca, string.Empty);
    await sinRed.PublishPendingPhotosAsync(CancellationToken.None);
    Check(!File.Exists(ruta23) && !File.Exists(marca), "Sin versión en la marca se borra lo que haya en esa ruta.");
}

async Task NombreAnteriorAsync()
{
    var dirs = new Carpetas(raiz, "e");
    ClientePhotoStorage almacen = Nuevo(dirs);
    almacen.MarcarTrabajadorActivo();
    string anterior = Path.Combine(dirs.El23, "VIEJO", "01I25000001-10007.jpg");
    Directory.CreateDirectory(Path.GetDirectoryName(anterior)!);
    File.WriteAllBytes(anterior, FotoJpeg(1000, 800, 41));

    var f = new FotoCliente(900, "VIEJO", "01I25000001", 100, 7, "jpg");
    Check((await almacen.ResolverAsync(f, FotoTamano.Original))?.Ruta == anterior, "Una foto vieja con el nombre anterior (línea a dos dígitos) se encuentra en el 23.");
    Check((await almacen.ResolverAsync(f, FotoTamano.Mini)) is { EsVariante: true }, "Y tiene miniatura.");
    Check(await EsperarAsync(() => File.Exists(almacen.RutaCaliente("VIEJO~01I25000001-10007~900.jpg", FotoTamano.Original))), "Leerla del 23 la deja caliente para la siguiente vez.");
}

async Task ModoDirectoAsync()
{
    var dirs = new Carpetas(raiz, "f");
    ClientePhotoStorage apagado = Nuevo(dirs, true, ("ClientePhotoSettings:Enabled", "false"));
    apagado.MarcarTrabajadorActivo();
    Check(!apagado.ColaActiva && !apagado.HotCacheActivo, "Con Enabled=false todo es como antes.");

    byte[] foto = FotoJpeg(1200, 900, 51);
    FotoClientePreparada preparada = await apagado.PrepararAsync(foto, "DIRECTO", "01I26100003", 103, 1, "jpg");
    Check(preparada.Directa && Path.GetDirectoryName(preparada.Ruta) == Path.Combine(dirs.El23, "DIRECTO"), "Sin cola se escribe en un temporal junto a su lugar del 23.");
    string final = Path.Combine(dirs.El23, "DIRECTO", "01I26100003-1031.jpg");
    Check(!File.Exists(final), "Antes de su fila todavía no tiene su nombre.");
    apagado.Activar(preparada, 950);
    Check(File.Exists(final) && File.ReadAllBytes(final).SequenceEqual(foto) && !File.Exists(preparada.Ruta), "Con la fila toma su nombre definitivo en el 23.");

    var f950 = new FotoCliente(950, "DIRECTO", "01I26100003", 103, 1, "jpg");
    FotoArchivo? mini = await apagado.ResolverAsync(f950, FotoTamano.Mini);
    Check(mini?.Ruta == final && !mini.EsVariante, "Sin almacén caliente se sirve el original, como antes.");
    Check(!Directory.Exists(dirs.Cola) || !Directory.EnumerateFiles(dirs.Cola, "*", SearchOption.AllDirectories).Any(), "Nada pasa por la cola.");

    FotoClientePreparada descartada = await apagado.PrepararAsync(foto, "DIRECTO", "01I26100003", 103, 2, "jpg");
    apagado.Descartar(descartada);
    Check(!File.Exists(descartada.Ruta) && !File.Exists(Path.Combine(dirs.El23, "DIRECTO", "01I26100003-1032.jpg")), "Descartada sin cola no deja nada en el 23.");
    apagado.Borrar(f950);
    Check(!File.Exists(final), "La baja sin cola borra directo en el 23.");

    // Una carpeta de cliente que no cabe en un nombre de archivo (trae diagonal) va directo al 23 aunque haya cola.
    var dirsG = new Carpetas(raiz, "g");
    ClientePhotoStorage almacen = Nuevo(dirsG);
    almacen.MarcarTrabajadorActivo();
    FotoClientePreparada anidada = await almacen.PrepararAsync(foto, @"GRUPO\SUB", "01I26100004", 100, 1, "jpg");
    Check(anidada.Directa, "Una carpeta con diagonal no puede ir a la cola: va directo al 23.");
    almacen.Activar(anidada, 960);
    string rutaAnidada = Path.Combine(dirsG.El23, "GRUPO", "SUB", "01I26100004-1001.jpg");
    Check(File.Exists(rutaAnidada), "Y queda en su carpeta anidada, como antes.");
    var f960 = new FotoCliente(960, @"GRUPO\SUB", "01I26100004", 100, 1, "jpg");
    Check((await almacen.ResolverAsync(f960, FotoTamano.Mini))?.Ruta == rutaAnidada, "Se lee del 23.");
    almacen.Borrar(f960);
    Check(!File.Exists(rutaAnidada), "Y se borra del 23.");
}

async Task LimpiezaAsync()
{
    var dirs = new Carpetas(raiz, "h");
    ClientePhotoStorage almacen = Nuevo(dirs);
    almacen.MarcarTrabajadorActivo();
    await SubirAsync(almacen, FotoJpeg(1200, 900, 61), "LIMPIA", "01I26100005", 107, 1, 970);
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    var f970 = new FotoCliente(970, "LIMPIA", "01I26100005", 107, 1, "jpg");
    await almacen.ResolverAsync(f970, FotoTamano.Mini);
    await almacen.ResolverAsync(f970, FotoTamano.Vista);
    string local = "LIMPIA~01I26100005-1071~970.jpg";

    LimpiezaFotosResultado temprano = await almacen.LimpiarCalienteAsync(DateTime.UtcNow.AddHours(47));
    Check(temprano.Desalojadas == 0 && File.Exists(almacen.RutaCaliente(local, FotoTamano.Original)), "Antes de 48 h se queda caliente.");
    LimpiezaFotosResultado tarde = await almacen.LimpiarCalienteAsync(DateTime.UtcNow.AddHours(49));
    Check(tarde.Desalojadas == 1 && !File.Exists(almacen.RutaCaliente(local, FotoTamano.Original)), "A las 48 h se quita de lo caliente porque el 23 la tiene (en la carpeta del cliente).");
    Check(File.Exists(almacen.RutaCaliente(local, FotoTamano.Mini)), "La miniatura se queda.");
    Check(!File.Exists(almacen.RutaCaliente(local, FotoTamano.Vista)), "La vista se va con su registro.");
    Check((await almacen.ResolverAsync(f970, FotoTamano.Mini))!.Ruta == almacen.RutaCaliente(local, FotoTamano.Mini), "La miniatura se sigue sirviendo sin ir al 23.");

    // Una caliente cuyo archivo ya no está en el 23 no se quita: nunca se borra lo que no está confirmado allá.
    await SubirAsync(almacen, FotoJpeg(1200, 900, 62), "LIMPIA", "01I26100005", 107, 2, 971);
    await almacen.PublishPendingPhotosAsync(CancellationToken.None);
    File.Delete(Path.Combine(dirs.El23, "LIMPIA", "01I26100005-1072.jpg"));
    LimpiezaFotosResultado sinCopia = await almacen.LimpiarCalienteAsync(DateTime.UtcNow.AddHours(49));
    Check(sinCopia.SinCopiaEn23 == 1 && File.Exists(almacen.RutaCaliente("LIMPIA~01I26100005-1072~971.jpg", FotoTamano.Original)), "Sin confirmar en el 23 se conserva.");
}

// ── Ayudantes ───────────────────────────────────────────────────────────────────────────────────────────────────────

ClientePhotoStorage Nuevo(Carpetas dirs, bool crear23 = true, params (string Clave, string Valor)[] extra)
{
    var valores = new Dictionary<string, string?>
    {
        ["ClientePhotoSettings:BasePath"] = dirs.El23,
        ["ClientePhotoSettings:QueuePath"] = dirs.Cola,
        ["ClientePhotoSettings:HotPath"] = dirs.Caliente,
        ["ClientePhotoSettings:HotHours"] = "48",
        ["ClientePhotoSettings:ThumbnailDays"] = "365",
        ["ClientePhotoSettings:PublishParallelism"] = "3"
    };
    foreach ((string clave, string valor) in extra) valores[clave] = valor;
    IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
    if (crear23) Directory.CreateDirectory(dirs.El23);
    return new ClientePhotoStorage(config, NullLogger<ClientePhotoStorage>.Instance);
}

static async Task SubirAsync(ClientePhotoStorage almacen, byte[] bytes, string carpeta, string cref, int tipo, int linea, long idFotos)
{
    FotoClientePreparada preparada = await almacen.PrepararAsync(bytes, carpeta, cref, tipo, linea, "jpg");
    almacen.Activar(preparada, idFotos);
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

static byte[] FotoJpeg(int ancho, int alto, int semilla)
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
        g.DrawString($"BULTO {semilla:D5}", fuente, Brushes.Black, ancho / 10f, alto / 2f);
    }

    ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    using var parametros = new EncoderParameters(1);
    parametros.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
    using var ms = new MemoryStream();
    bmp.Save(ms, codec, parametros);
    return ms.ToArray();
}

sealed record Carpetas(string Raiz, string Nombre)
{
    public string El23 => Path.Combine(Raiz, Nombre, "23");
    public string Cola => Path.Combine(Raiz, Nombre, "cola");
    public string Pendientes => Path.Combine(Cola, "pending");
    public string Bajas => Path.Combine(Cola, "delete");
    public string Caliente => Path.Combine(Raiz, Nombre, "caliente");
}
