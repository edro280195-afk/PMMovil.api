using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using PMMovil.Api.Services;

// Regresiones de la mejora clásica de fotos. Sin servidor, sin fotos productivas, sin bases de datos: las imágenes se
// generan aquí y los archivos viven en una carpeta temporal que se borra al terminar.
//
// Modo laboratorio (opcional): con FOTOMEJORA_MUESTRAS=<carpeta con .jpg> procesa esas fotos con el servicio real y deja el
// resultado en FOTOMEJORA_SALIDA (o <muestras>\csharp), para compararlo a ojo con el prototipo.
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

FotoMejoraService NuevoServicio(FotoMejoraOptions o) => new(o, NullLogger<FotoMejoraService>.Instance);

// Patrón con bordes duros (tablero + texto): parece una etiqueta, así que tiene nitidez y contraste altos si se ilumina bien.
Mat Patron(byte fondo, byte tinta, double desenfoque = 0, double ruido = 3)
{
    var m = new Mat(900, 1200, MatType.CV_8UC3, new Scalar(fondo, fondo, fondo));
    var color = new Scalar(tinta, tinta, tinta);
    for (int y = 0; y < 900; y += 40)
        for (int x = 0; x < 1200; x += 40)
            if (((x / 40) + (y / 40)) % 2 == 0) Cv2.Rectangle(m, new Rect(x, y, 40, 40), color, -1);
    Cv2.PutText(m, "LOTE 12345", new Point(60, 450), HersheyFonts.HersheySimplex, 3.0, color, 6);
    if (desenfoque > 0) Cv2.GaussianBlur(m, m, new Size(0, 0), desenfoque);

    // Ruido de sensor: una foto real nunca es lisa, y sin él el JPEG de prueba pesa casi nada.
    if (ruido > 0)
    {
        using var m16 = new Mat();
        m.ConvertTo(m16, MatType.CV_16SC3);
        using var variacion = new Mat(m.Size(), MatType.CV_16SC3);
        new RNG(7).Fill(variacion, DistributionType.Normal, Scalar.All(0), Scalar.All(ruido));
        Cv2.Add(m16, variacion, m16);
        m16.ConvertTo(m, MatType.CV_8UC3);
    }

    return m;
}

byte[] Jpg(Mat m)
{
    Cv2.ImEncode(".jpg", m, out byte[] bytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, 92));
    return bytes;
}

FotoMejoraMedicion Medicion(byte[] jpg)
{
    using Mat m = Cv2.ImDecode(jpg, ImreadModes.Color);
    return FotoMejoraService.Medir(m);
}

var opciones = new FotoMejoraOptions { Enabled = true };
Check(!new FotoMejoraOptions().Enabled, "Por omisión la mejora de fotos está apagada (falta el Visual C++ en el servidor).");
FotoMejoraService servicio = NuevoServicio(opciones);
Check(servicio.IntentarIniciar(), "OpenCV debe cargar en esta máquina.");

// --- Reglas de decisión (sin imágenes) ---
Check(servicio.Evaluar(new FotoMejoraMedicion(126, 180, 1161)) == (false, false, false), "Una foto mediana no pide nada.");
Check(servicio.Evaluar(new FotoMejoraMedicion(70, 180, 1161)).Oscura, "Luz baja pide aclarar.");
Check(servicio.Evaluar(new FotoMejoraMedicion(126, 60, 1161)).PocoContraste, "Contraste bajo pide recuperar contraste.");
Check(servicio.Evaluar(new FotoMejoraMedicion(126, 180, 80)).Suave, "Nitidez baja pide afilado.");
Check(servicio.Evaluar(new FotoMejoraMedicion(95, 80, 200)) == (false, false, false), "Los umbrales son estrictos: en el límite no se toca.");

// --- Foto buena: pasa intacta, byte por byte ---
byte[] buena;
using (Mat m = Patron(235, 20)) buena = Jpg(m);
FotoMejoraResultado rBuena = servicio.Mejorar(buena);
Check(!rBuena.Modificada, "Una foto buena no se modifica.");
Check(ReferenceEquals(rBuena.Bytes, buena), "Una foto buena se devuelve idéntica, sin recomprimir.");

// --- Foto oscura: se aclara, conserva tamaño y sigue siendo JPEG válido ---
byte[] oscura;
using (Mat m = Patron(70, 15, ruido: 5)) oscura = Jpg(m);
FotoMejoraResultado rOscura = servicio.Mejorar(oscura);
Check(rOscura.Modificada, $"Una foto oscura se mejora (detalle: {rOscura.Detalle}; antes: {rOscura.Antes}).");
Check(rOscura.Despues != null && rOscura.Despues.Luz > rOscura.Antes!.Luz + 20, "La foto oscura sale más clara.");
using (Mat salida = Cv2.ImDecode(rOscura.Bytes, ImreadModes.Color))
{
    Check(!salida.Empty() && salida.Width == 1200 && salida.Height == 900, "La mejora conserva la resolución.");
}

Check(rOscura.Detalle.Contains("luz"), "El detalle dice qué se corrigió.");
Check(rOscura.Bytes.Length <= oscura.Length * 1.5 + 50_000, "La foto mejorada no se infla.");

// --- Foto casi negra (lente tapado): no hay detalle que recuperar, no se toca ---
// Aclararla convertía el ruido del sensor en una imagen que pesaba 40 % más (426 KB -> 596 KB en la prueba).
byte[] negra;
using (Mat m = Patron(5, 5, ruido: 2)) negra = Jpg(m);
FotoMejoraResultado rNegra = servicio.Mejorar(negra);
Check(!rNegra.Modificada && ReferenceEquals(rNegra.Bytes, negra), "Una foto casi negra se devuelve idéntica.");
Check(rNegra.Detalle.Contains("negra"), "El detalle explica por qué no se tocó.");

// --- Tope de peso: la mejora no engorda la foto más de lo permitido ---
// Con calidad alta el primer intento pesa mucho más que la original; el tope baja la calidad por pasos hasta cumplir o llegar
// al piso. (En la prueba con el Pixel una foto de 1.0 MB pasó a 1.47 MB con calidad 85 antes de existir este tope.)
FotoMejoraResultado rSinTope = NuevoServicio(new FotoMejoraOptions { Enabled = true, JpegQuality = 98, MaxCrecimientoPeso = 100 }).Mejorar(oscura);
FotoMejoraResultado rEnElPiso = NuevoServicio(new FotoMejoraOptions { Enabled = true, JpegQuality = 75, MaxCrecimientoPeso = 100 }).Mejorar(oscura);
FotoMejoraResultado rConTope = NuevoServicio(new FotoMejoraOptions { Enabled = true, JpegQuality = 98 }).Mejorar(oscura);
Check(rSinTope.Modificada && rEnElPiso.Modificada && rConTope.Modificada, "Las tres variantes mejoran la foto oscura.");
Check(rSinTope.Bytes.Length > oscura.Length * 1.15, "Sin tope, la calidad 98 sí engorda la foto (si no, esta prueba no prueba nada).");
Check(rConTope.Bytes.Length < rSinTope.Bytes.Length, "El tope de peso reduce el archivo.");
Check(rConTope.Bytes.Length <= Math.Max(oscura.Length * 1.15, rEnElPiso.Bytes.Length),
    "Con tope, la foto no pasa de +15 % salvo que ni la calidad mínima lo logre.");
Check(rConTope.Bytes.Length >= rEnElPiso.Bytes.Length, "Nunca baja de la calidad mínima.");

// --- Foto suave: pide afilado y sale más nítida ---
byte[] suave;
using (Mat m = Patron(235, 20, desenfoque: 7, ruido: 1)) suave = Jpg(m);
FotoMejoraMedicion medSuave = Medicion(suave);
Check(medSuave.Nitidez < Medicion(buena).Nitidez / 4, "El desenfoque baja la nitidez medida.");
FotoMejoraResultado rSuave = servicio.Mejorar(suave);
if (servicio.Evaluar(medSuave).Suave)
{
    Check(rSuave.Modificada && rSuave.Detalle.Contains("nitidez"), "Una foto suave se afila.");
    Check(rSuave.Despues!.Nitidez > rSuave.Antes!.Nitidez, "La foto suave sale más nítida.");
}
else
{
    Check(!rSuave.Modificada, "Si no cruza el umbral de nitidez, no se toca.");
}

// --- Entradas raras: nunca lanzan ---
Check(!servicio.Mejorar(Array.Empty<byte>()).Modificada, "Vacía: sin cambios.");
byte[] basura = new byte[4096];
new Random(7).NextBytes(basura);
FotoMejoraResultado rBasura = servicio.Mejorar(basura);
Check(!rBasura.Modificada && ReferenceEquals(rBasura.Bytes, basura), "Bytes que no son imagen se devuelven tal cual.");
FotoMejoraService chiquito = NuevoServicio(new FotoMejoraOptions { Enabled = true, MaxPixels = 100 });
Check(!chiquito.Mejorar(oscura).Modificada, "Una imagen mayor al tope de píxeles no se toca.");

// --- Cola ---
string raiz = Path.Combine(Path.GetTempPath(), "pmm-fotomejora-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(raiz);
try
{
    string carpeta = Path.Combine(raiz, "fotos", "CLIENTE-A");
    Directory.CreateDirectory(carpeta);

    var cola = new FotoMejoraCola(opciones, NullLogger<FotoMejoraCola>.Instance);
    string jpgOscuro = Path.Combine(carpeta, "ref-1001.jpg");
    File.WriteAllBytes(jpgOscuro, oscura);
    cola.Encolar(jpgOscuro, "prueba");
    cola.Encolar(Path.Combine(carpeta, "no-existe.jpg"), "prueba");
    cola.Encolar(Path.Combine(carpeta, "documento.pdf"), "prueba");
    cola.Encolar("", "prueba");
    Check(cola.Lector.TryRead(out FotoMejoraTrabajo? trabajo) && trabajo!.Ruta == jpgOscuro && trabajo.Bytes == oscura.Length,
        "Encolar guarda ruta, tamaño y fecha.");
    Check(!cola.Lector.TryRead(out _), "Se ignoran archivos inexistentes, no JPG y rutas vacías.");

    var apagada = new FotoMejoraCola(new FotoMejoraOptions { Enabled = false }, NullLogger<FotoMejoraCola>.Instance);
    apagada.Encolar(jpgOscuro, "prueba");
    Check(!apagada.Lector.TryRead(out _), "Con Enabled=false no se encola nada.");

    var llena = new FotoMejoraCola(new FotoMejoraOptions { Enabled = true, QueueCapacity = 10 }, NullLogger<FotoMejoraCola>.Instance);
    for (int i = 0; i < 30; i++) llena.Encolar(jpgOscuro, "prueba");
    int dentro = 0;
    while (llena.Lector.TryRead(out _)) dentro++;
    Check(dentro == 10, "La cola llena descarta lo que sobra sin lanzar.");

    // --- Trabajador ---
    IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["PhotoSettings:BasePath"] = Path.Combine(raiz, "fotos") }).Build();
    var opcionesTrabajador = new FotoMejoraOptions { Enabled = true };
    FotoMejoraWorker NuevoTrabajador(FotoMejoraOptions o) =>
        new(cola, NuevoServicio(o), o, config, NullLogger<FotoMejoraWorker>.Instance);
    FotoMejoraTrabajo Trabajo(string ruta)
    {
        var info = new FileInfo(ruta);
        return new FotoMejoraTrabajo(ruta, "prueba", info.Length, info.LastWriteTimeUtc);
    }

    FotoMejoraWorker trabajador = NuevoTrabajador(opcionesTrabajador);
    FotoMejoraSalida salidaMejorada = trabajador.Procesar(Trabajo(jpgOscuro));
    Check(salidaMejorada.Estado == FotoMejoraEstado.Mejorada, "El trabajador reemplaza una foto oscura.");
    byte[] despues = File.ReadAllBytes(jpgOscuro);
    Check(!despues.SequenceEqual(oscura), "El archivo quedó con la versión mejorada.");
    Check(Medicion(despues).Luz > Medicion(oscura).Luz + 20, "El archivo reemplazado es más claro.");
    string respaldo = Path.Combine(raiz, "fotos", ".originales", "CLIENTE-A", "ref-1001.jpg");
    Check(File.Exists(respaldo) && File.ReadAllBytes(respaldo).SequenceEqual(oscura), "El original quedó respaldado intacto.");
    Check(!Directory.GetFiles(carpeta, ".mej-*").Any(), "No quedan temporales en la carpeta de fotos.");
    Check(Directory.GetFiles(carpeta).Length == 1, "La carpeta de fotos solo tiene la foto.");
    Check(trabajador.Mejoradas == 1, "El contador de mejoradas sube.");

    // Foto buena: SinCambios y el archivo no se toca ni se respalda.
    string jpgBueno = Path.Combine(carpeta, "ref-1002.jpg");
    File.WriteAllBytes(jpgBueno, buena);
    DateTime fechaBueno = File.GetLastWriteTimeUtc(jpgBueno);
    Check(trabajador.Procesar(Trabajo(jpgBueno)).Estado == FotoMejoraEstado.SinCambios, "Foto buena: sin cambios.");
    Check(File.ReadAllBytes(jpgBueno).SequenceEqual(buena) && File.GetLastWriteTimeUtc(jpgBueno) == fechaBueno, "Foto buena: archivo intacto.");
    Check(!File.Exists(Path.Combine(raiz, "fotos", ".originales", "CLIENTE-A", "ref-1002.jpg")), "Foto buena: sin respaldo.");

    // Archivo que cambió mientras esperaba turno: no se toca.
    string jpgCambia = Path.Combine(carpeta, "ref-1003.jpg");
    File.WriteAllBytes(jpgCambia, oscura);
    FotoMejoraTrabajo trabajoCambia = Trabajo(jpgCambia);
    File.WriteAllBytes(jpgCambia, buena);
    Check(trabajador.Procesar(trabajoCambia).Estado == FotoMejoraEstado.Omitida, "Un archivo que cambió se omite.");
    Check(File.ReadAllBytes(jpgCambia).SequenceEqual(buena), "El archivo que cambió queda como estaba.");

    // Archivo borrado mientras esperaba turno: no resucita.
    string jpgBorrado = Path.Combine(carpeta, "ref-1004.jpg");
    File.WriteAllBytes(jpgBorrado, oscura);
    FotoMejoraTrabajo trabajoBorrado = Trabajo(jpgBorrado);
    File.Delete(jpgBorrado);
    Check(trabajador.Procesar(trabajoBorrado).Estado == FotoMejoraEstado.Omitida, "Un archivo borrado se omite.");
    Check(!File.Exists(jpgBorrado), "Un archivo borrado no resucita.");

    // Si el original no se puede respaldar, la foto NO se reemplaza.
    string raizImposible = Path.Combine(raiz, "bloqueada");
    File.WriteAllText(raizImposible, "esto es un archivo, no una carpeta");
    FotoMejoraWorker sinRespaldo = NuevoTrabajador(new FotoMejoraOptions { Enabled = true, OriginalesPath = raizImposible });
    string jpgSinRespaldo = Path.Combine(carpeta, "ref-1005.jpg");
    File.WriteAllBytes(jpgSinRespaldo, oscura);
    Check(sinRespaldo.Procesar(Trabajo(jpgSinRespaldo)).Estado == FotoMejoraEstado.Omitida, "Sin respaldo no se reemplaza.");
    Check(File.ReadAllBytes(jpgSinRespaldo).SequenceEqual(oscura), "Sin respaldo, la foto queda como llegó.");

    // GuardarOriginales=false: reemplaza sin respaldar.
    FotoMejoraWorker sinGuardar = NuevoTrabajador(new FotoMejoraOptions { Enabled = true, GuardarOriginales = false });
    string jpgSinGuardar = Path.Combine(carpeta, "ref-1006.jpg");
    File.WriteAllBytes(jpgSinGuardar, oscura);
    Check(sinGuardar.Procesar(Trabajo(jpgSinGuardar)).Estado == FotoMejoraEstado.Mejorada, "GuardarOriginales=false también mejora.");
    Check(!File.Exists(Path.Combine(raiz, "fotos", ".originales", "CLIENTE-A", "ref-1006.jpg")), "GuardarOriginales=false no respalda.");

    // --- Modo laboratorio con fotos reales ---
    string? muestras = Environment.GetEnvironmentVariable("FOTOMEJORA_MUESTRAS");
    if (!string.IsNullOrWhiteSpace(muestras) && Directory.Exists(muestras))
    {
        string destino = Environment.GetEnvironmentVariable("FOTOMEJORA_SALIDA") ?? Path.Combine(muestras, "csharp");
        Directory.CreateDirectory(destino);
        int tocadas = 0, total = 0;
        double msTotal = 0;
        // Para afinar a ojo: FOTOMEJORA_QMIN (piso de calidad) y FOTOMEJORA_CRECIMIENTO (tope de peso, 1.15 = +15 %).
        var opcionesLab = new FotoMejoraOptions
        {
            Enabled = true,
            JpegQualityMin = int.TryParse(Environment.GetEnvironmentVariable("FOTOMEJORA_QMIN"), out int qmin) ? qmin : opciones.JpegQualityMin,
            MaxCrecimientoPeso = double.TryParse(Environment.GetEnvironmentVariable("FOTOMEJORA_CRECIMIENTO"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double crec)
                ? crec : opciones.MaxCrecimientoPeso
        };
        FotoMejoraService servicioLab = NuevoServicio(opcionesLab);
        Console.WriteLine($"Laboratorio con piso de calidad {opcionesLab.JpegQualityMin} y tope de peso {opcionesLab.MaxCrecimientoPeso:0.00}.");
        foreach (string foto in Directory.GetFiles(muestras, "*.jpg").OrderBy(x => x))
        {
            byte[] bytes = File.ReadAllBytes(foto);
            FotoMejoraResultado r = servicioLab.Mejorar(bytes);
            File.WriteAllBytes(Path.Combine(destino, Path.GetFileName(foto)), r.Bytes);
            total++;
            if (r.Modificada) { tocadas++; msTotal += r.Milisegundos; }
            Console.WriteLine(
                $"{Path.GetFileName(foto),-18} {(r.Modificada ? "MEJORADA " : "intacta  ")} {r.Detalle,-26} " +
                $"luz {r.Antes?.Luz:0}->{r.Despues?.Luz:0} contraste {r.Antes?.Contraste:0}->{r.Despues?.Contraste:0} " +
                $"nitidez {r.Antes?.Nitidez:0}->{r.Despues?.Nitidez:0}  {bytes.Length / 1024}->{r.Bytes.Length / 1024} KB  {r.Milisegundos} ms");
        }

        Console.WriteLine($"Laboratorio: {tocadas} de {total} fotos mejoradas; promedio {(tocadas == 0 ? 0 : msTotal / tocadas):0} ms por foto mejorada.");
    }
}
finally
{
    try { Directory.Delete(raiz, recursive: true); } catch { }
}

Console.WriteLine($"FotoMejora.Regression: {checks} comprobaciones OK.");
