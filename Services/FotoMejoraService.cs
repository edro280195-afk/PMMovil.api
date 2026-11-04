using System.Diagnostics;
using OpenCvSharp;

namespace PMMovil.Api.Services;

/// <summary>
/// Opciones de la mejora automática de fotos (sección "FotoMejora" de appsettings). Si la sección no existe rigen estos
/// valores, así que un servidor sin configuración también queda con la mejora activa; para apagarla basta
/// <c>"FotoMejora": { "Enabled": false }</c>.
///
/// Los umbrales salen de medir 700 fotos reales de producción (septiembre-octubre 2026): con estos valores se toca cerca
/// del 15 % de las fotos y el resto pasa intacto, byte por byte.
/// </summary>
public sealed class FotoMejoraOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Fotos procesadas a la vez. Una sola alcanza y no le quita CPU al API.</summary>
    public int Parallelism { get; set; } = 1;

    public int QueueCapacity { get; set; } = 1000;

    /// <summary>Hilos que puede usar OpenCV por foto. Poco: la mejora no corre prisa y el API atiende gente.</summary>
    public int OpenCvThreads { get; set; } = 2;

    public int JpegQuality { get; set; } = 85;

    /// <summary>Calidad mínima a la que se puede bajar para respetar [MaxCrecimientoPeso]. Por debajo se pierde legibilidad.</summary>
    public int JpegQualityMin { get; set; } = 70;

    /// <summary>
    /// Cuánto puede pesar la foto mejorada respecto a la original (1.15 = hasta 15 % más). Si se pasa, se baja la calidad JPEG
    /// de 5 en 5 hasta [JpegQualityMin]. Es para cuidar almacenamiento y descargas: la mejora no debe engordar las fotos.
    /// </summary>
    public double MaxCrecimientoPeso { get; set; } = 1.15;

    /// <summary>Tope de megapíxeles: una imagen mayor se deja como llegó.</summary>
    public long MaxPixels { get; set; } = 24_000_000;

    /// <summary>
    /// Guarda la foto tal como llegó antes de reemplazarla. Si no se puede respaldar, la foto NO se reemplaza.
    /// </summary>
    public bool GuardarOriginales { get; set; } = true;

    /// <summary>
    /// Carpeta de los originales. Vacía = ".originales" dentro de PhotoSettings:BasePath (con punto inicial, como
    /// ".label-captures").
    /// </summary>
    public string? OriginalesPath { get; set; }

    /// <summary>Luz media (0-255) por debajo de la cual la foto se aclara.</summary>
    public double LuzOscura { get; set; } = 95;

    /// <summary>
    /// Luz media por debajo de la cual la foto está prácticamente negra (lente tapado, bolsa, cuarto sin luz) y NO se toca:
    /// no hay detalle que recuperar, aclararla solo convierte el ruido del sensor en una imagen que pesa 40 % más.
    /// </summary>
    public double LuzMinima { get; set; } = 20;

    /// <summary>Contraste (percentil 95 menos percentil 5) por debajo del cual se recupera contraste local.</summary>
    public double ContrasteBajo { get; set; } = 80;

    /// <summary>Nitidez (varianza del Laplaciano de la mejor zona) por debajo de la cual se afila suavemente.</summary>
    public double NitidezBaja { get; set; } = 200;
}

/// <summary>Lo que se midió de una foto: luz media, contraste y nitidez, todo a una escala de 1280 px de lado mayor.</summary>
public sealed record FotoMejoraMedicion(double Luz, double Contraste, double Nitidez);

public sealed record FotoMejoraResultado(
    bool Modificada,
    byte[] Bytes,
    string Detalle,
    FotoMejoraMedicion? Antes,
    FotoMejoraMedicion? Despues,
    long Milisegundos);

/// <summary>
/// Mejora clásica de fotos de evidencia: corrige luz, contraste local y nitidez con procesado fotométrico (OpenCV),
/// sin redes neuronales.
///
/// Por qué sin IA: probados DarkIR y NAFNet con fotos reales de bodega, ninguno recupera una foto desenfocada, DarkIR
/// quemaba las que ya tenían buena luz y NAFNet corrompió una foto completa. Esto no inventa detalle: solo reparte mejor
/// lo que la cámara ya capturó, así que es seguro para evidencia de aduana.
///
/// Una foto que no lo necesita se devuelve idéntica (mismo arreglo de bytes, sin recomprimir). Nada de aquí lanza
/// excepciones hacia fuera: ante cualquier problema el resultado es la foto original.
/// </summary>
public sealed class FotoMejoraService
{
    private const int LadoMedicion = 1280;

    private readonly FotoMejoraOptions _options;
    private readonly ILogger<FotoMejoraService> _logger;
    private readonly object _bloqueoInicio = new();
    private bool? _disponible;

    public FotoMejoraService(FotoMejoraOptions options, ILogger<FotoMejoraService> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Comprueba una sola vez que OpenCV carga (las DLL nativas viajan en OpenCvSharp4.runtime.win y piden el
    /// Visual C++ 2015-2022 x64). Si no carga, la mejora se apaga sola y las fotos se guardan como siempre.
    /// </summary>
    public bool IntentarIniciar()
    {
        if (_disponible.HasValue) return _disponible.Value;
        lock (_bloqueoInicio)
        {
            if (_disponible.HasValue) return _disponible.Value;
            try
            {
                Cv2.SetNumThreads(Math.Clamp(_options.OpenCvThreads, 1, Environment.ProcessorCount));
                using Mat prueba = new(2, 2, MatType.CV_8UC3, Scalar.All(0));
                using Mat gris = new();
                Cv2.CvtColor(prueba, gris, ColorConversionCodes.BGR2GRAY);
                _disponible = true;
            }
            catch (Exception ex)
            {
                _disponible = false;
                _logger.LogError(ex,
                    "OpenCV no se pudo cargar: la mejora automática de fotos queda apagada y las fotos se guardan sin mejorar. " +
                    "Revisa que el servidor tenga el Visual C++ 2015-2022 x64 y las DLL de OpenCvSharp4.runtime.win.");
            }

            return _disponible.Value;
        }
    }

    /// <summary>Qué correcciones pide una foto según lo medido. Separado para poder probar la regla sin imágenes.</summary>
    public (bool Oscura, bool PocoContraste, bool Suave) Evaluar(FotoMejoraMedicion medicion) =>
        (medicion.Luz < _options.LuzOscura,
         medicion.Contraste < _options.ContrasteBajo,
         medicion.Nitidez < _options.NitidezBaja);

    public FotoMejoraResultado Mejorar(byte[] original)
    {
        var reloj = Stopwatch.StartNew();
        if (original.Length == 0) return Sin(original, "vacía", null, reloj);
        if (!IntentarIniciar()) return Sin(original, "OpenCV no disponible", null, reloj);

        try
        {
            return MejorarInterno(original, reloj);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo mejorar una foto; se conserva la original.");
            return Sin(original, "error: " + ex.Message, null, reloj);
        }
    }

    private FotoMejoraResultado MejorarInterno(byte[] original, Stopwatch reloj)
    {
        using Mat bgr = Cv2.ImDecode(original, ImreadModes.Color);
        if (bgr.Empty()) return Sin(original, "no se pudo decodificar", null, reloj);

        if ((long)bgr.Width * bgr.Height > _options.MaxPixels)
            return Sin(original, "demasiado grande", null, reloj);

        FotoMejoraMedicion antes = Medir(bgr);
        if (antes.Luz < _options.LuzMinima)
            return Sin(original, "casi negra: no hay detalle que recuperar", antes, reloj);

        var (oscura, pocoContraste, suave) = Evaluar(antes);
        if (!oscura && !pocoContraste && !suave)
            return Sin(original, "no lo necesita", antes, reloj);

        using Mat mejorada = Aplicar(bgr, oscura, pocoContraste, suave);
        FotoMejoraMedicion despues = Medir(mejorada);

        // Red de seguridad: es procesado determinista y no debería fallar, pero la evidencia no se arriesga.
        if (mejorada.Width != bgr.Width || mejorada.Height != bgr.Height
            || double.IsNaN(despues.Luz) || despues.Luz < antes.Luz * 0.6 || despues.Luz > 235)
            return Sin(original, "el resultado no pasó la verificación", antes, reloj);

        int calidad = Math.Clamp(_options.JpegQuality, 50, 100);
        byte[] codificada = Codificar(mejorada, calidad);

        // Tope de peso. La mejora es de luz y contraste, no debe costar más almacenamiento ni más descarga. Aclarar y afilar
        // una foto con mucho detalle la engorda: en la prueba con el Pixel una foto de 1.0 MB pasó a 1.47 MB (+45 %) con
        // calidad 85. Si se pasa del tope, se baja la calidad por pasos hasta el piso; la diferencia no se nota y el peso vuelve.
        long tope = (long)(original.Length * Math.Max(_options.MaxCrecimientoPeso, 1.0));
        int piso = Math.Clamp(_options.JpegQualityMin, 50, calidad);
        while (codificada.Length > tope && calidad > piso)
        {
            calidad = Math.Max(calidad - 5, piso);
            codificada = Codificar(mejorada, calidad);
        }

        // Tope absoluto: aun con la calidad mínima, si la mejora engorda la foto más de la mitad (+50 KB de holgura para fotos
        // ya muy comprimidas, que siempre crecen algo), no vale la pena: se deja la original.
        double topeAbsoluto = Math.Max(_options.MaxCrecimientoPeso, 1.5);
        if (codificada.Length == 0 || codificada.Length > original.Length * topeAbsoluto + 50_000)
            return Sin(original, "el resultado pesaba demasiado", antes, reloj);

        string acciones = string.Join("+", new[]
        {
            oscura ? "luz" : null,
            pocoContraste ? "contraste" : null,
            suave ? "nitidez" : null
        }.Where(x => x != null));

        return new FotoMejoraResultado(true, codificada, acciones, antes, despues, reloj.ElapsedMilliseconds);
    }

    /// <summary>
    /// Gamma (solo si es oscura), contraste local de radio grande ("claridad") sobre la luminancia, reducción de ruido y
    /// afilado suave. Se validó a ojo con fotos reales de bodega Y A TAMAÑO REAL (100 %): la primera versión usaba CLAHE y
    /// solo se había mirado reducida, así que no se vio que inflaba el grano de las superficies lisas hasta que una foto
    /// limpia del Pixel pasó por el API y pesó 45 % más.
    /// </summary>
    private static Mat Aplicar(Mat bgr, bool oscura, bool pocoContraste, bool suave)
    {
        using Mat lab = new();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        Mat[] canales = Cv2.Split(lab);
        try
        {
            Mat luminancia = canales[0];
            double mediaL = Math.Max(Cv2.Mean(luminancia).Val0 / 255.0, 1e-3);

            if (oscura)
            {
                // Lleva la luminancia media hacia ~0.42 sin pasarse.
                double gamma = Math.Clamp(Math.Log(0.42) / Math.Log(mediaL), 0.45, 1.0);
                byte[] tabla = new byte[256];
                for (int i = 0; i < 256; i++)
                    tabla[i] = (byte)Math.Round(Math.Pow(i / 255.0, gamma) * 255.0);
                using Mat tablaMat = Mat.FromArray(tabla);
                using Mat aclarada = new();
                Cv2.LUT(luminancia, tablaMat, aclarada);
                aclarada.CopyTo(luminancia);
            }

            // Contraste local de radio grande ("claridad"): L + k (L - desenfoque(L, sigma grande)). Levanta letras, sellos y
            // bordes de etiquetas (el gamma aclara pero aplasta el contraste del texto) SIN estirar el grano fino de las
            // superficies lisas. Antes se usaba CLAHE, que en la prueba con una foto limpia del Pixel convirtió una madera lisa
            // en grano visible (nitidez medida de 846 a 6467) y engordó la foto un 45 %: amplificaba ~2.8x las micro-variaciones.
            double sigmaClaridad = Math.Max(bgr.Width, bgr.Height) / 60.0;
            double kClaridad = pocoContraste ? 0.70 : 0.45;
            using (Mat difuminadaL = new())
            using (Mat conClaridad = new())
            {
                Cv2.GaussianBlur(luminancia, difuminadaL, new Size(0, 0), sigmaClaridad);
                Cv2.AddWeighted(luminancia, 1.0 + kClaridad, difuminadaL, -kClaridad, 0, conClaridad);
                conClaridad.CopyTo(luminancia);
            }

            // Ruido de color: casi gratis de quitar y el ojo no distingue detalle en el color (el propio JPEG lo submuestrea),
            // pero ahorra bytes: sin esto, afilar y subir contraste hacía pesar ~40 % más a las fotos suaves.
            Cv2.GaussianBlur(canales[1], canales[1], new Size(0, 0), 1.2);
            Cv2.GaussianBlur(canales[2], canales[2], new Size(0, 0), 1.2);

            using Mat lab2 = new();
            Cv2.Merge(canales, lab2);
            using Mat color = new();
            Cv2.CvtColor(lab2, color, ColorConversionCodes.Lab2BGR);

            // Ruido: solo en fotos oscuras, que es donde sobra (y donde el aclarado lo realza). Es lo más caro del proceso
            // (~7 s por foto de 5 MP con 2 hilos) y en fotos con buena luz no se nota, así que ahí se omite.
            using Mat sinRuido = new();
            if (oscura)
            {
                float h = mediaL < 0.25 ? 6f : 4f;
                Cv2.FastNlMeansDenoisingColored(color, sinRuido, h, h, 5, 15);
            }
            else
            {
                // Bilateral: limpia el grano sin borrar bordes y cuesta una fracción del reductor de ruido. Sin esto, afilar y
                // subir contraste hace pesar bastante más a la foto, porque el grano también se realza.
                Cv2.BilateralFilter(color, sinRuido, 5, 18, 4);
            }

            // Afilado suave (unsharp mask) para devolver el borde que quitó el denoise.
            // Menos si solo estaba oscura: afilar una foto recién aclarada realza su grano.
            double cantidad = suave ? 0.55 : (pocoContraste ? 0.40 : 0.30);
            using Mat difuminada = new();
            Cv2.GaussianBlur(sinRuido, difuminada, new Size(0, 0), 1.4);
            Mat resultado = new();
            Cv2.AddWeighted(sinRuido, 1.0 + cantidad, difuminada, -cantidad, 0, resultado);
            return resultado;
        }
        finally
        {
            foreach (Mat canal in canales) canal.Dispose();
        }
    }

    /// <summary>
    /// Luz media, contraste y nitidez medidos sobre una versión de 1280 px de lado mayor en gris, para que el número no
    /// dependa de la resolución de la cámara. La nitidez es la varianza del Laplaciano de la MEJOR zona de una rejilla de
    /// 4x4 (percentil 90): una foto con una parte nítida no está movida aunque el fondo sea liso.
    /// </summary>
    public static FotoMejoraMedicion Medir(Mat bgr)
    {
        using Mat gris = new();
        Cv2.CvtColor(bgr, gris, ColorConversionCodes.BGR2GRAY);
        using Mat chica = new();
        int mayor = Math.Max(gris.Width, gris.Height);
        if (mayor > LadoMedicion)
        {
            double razon = LadoMedicion / (double)mayor;
            Cv2.Resize(gris, chica, new Size(0, 0), razon, razon, InterpolationFlags.Area);
        }
        else
        {
            gris.CopyTo(chica);
        }

        chica.GetArray(out byte[] datos);
        var histograma = new long[256];
        double suma = 0;
        foreach (byte v in datos)
        {
            histograma[v]++;
            suma += v;
        }

        double luz = suma / datos.Length;
        double contraste = Percentil(histograma, datos.Length, 95) - Percentil(histograma, datos.Length, 5);

        using Mat laplaciano = new();
        Cv2.Laplacian(chica, laplaciano, MatType.CV_64F);
        int altoZona = chica.Height / 4, anchoZona = chica.Width / 4;
        var varianzas = new List<double>(16);
        if (altoZona > 0 && anchoZona > 0)
        {
            for (int fila = 0; fila < 4; fila++)
            for (int columna = 0; columna < 4; columna++)
            {
                using Mat zona = new(laplaciano, new Rect(columna * anchoZona, fila * altoZona, anchoZona, altoZona));
                Cv2.MeanStdDev(zona, out _, out Scalar desviacion);
                varianzas.Add(desviacion.Val0 * desviacion.Val0);
            }
        }

        double nitidez = PercentilLineal(varianzas, 90);
        return new FotoMejoraMedicion(luz, contraste, nitidez);
    }

    private static byte[] Codificar(Mat imagen, int calidad)
    {
        Cv2.ImEncode(".jpg", imagen, out byte[] bytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, calidad));
        return bytes;
    }

    private static double Percentil(long[] histograma, long total, double porcentaje)
    {
        double objetivo = porcentaje / 100.0 * (total - 1);
        long acumulado = 0;
        for (int valor = 0; valor < histograma.Length; valor++)
        {
            acumulado += histograma[valor];
            if (acumulado > objetivo) return valor;
        }

        return 255;
    }

    private static double PercentilLineal(List<double> valores, double porcentaje)
    {
        if (valores.Count == 0) return 0;
        valores.Sort();
        double posicion = porcentaje / 100.0 * (valores.Count - 1);
        int bajo = (int)Math.Floor(posicion);
        int alto = Math.Min(bajo + 1, valores.Count - 1);
        return valores[bajo] + (valores[alto] - valores[bajo]) * (posicion - bajo);
    }

    private static FotoMejoraResultado Sin(byte[] original, string detalle, FotoMejoraMedicion? antes, Stopwatch reloj) =>
        new(false, original, detalle, antes, null, reloj.ElapsedMilliseconds);
}
