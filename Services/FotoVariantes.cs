using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PMMovil.Api.Services;

/// <summary>Miniatura y vista de una foto, ya como JPEG.</summary>
public sealed record FotoVariantesResultado(byte[] Mini, byte[] Vista, int AnchoOriginal, int AltoOriginal);

/// <summary>
/// Versiones ligeras de una foto de evidencia para mostrarla en la app sin mover el original.
///
/// Medido el 18-nov-2026 con fotos reales de Caseta (1920×2560, ~800 KB): la miniatura de 400 px a calidad 70 pesa
/// ~14 KB (2 %) y la vista de 1280 px a calidad 80 ~132 KB (17 %). La miniatura es para cuadrículas y tiras; la vista,
/// para ver la foto en pantalla; el original solo viaja cuando alguien hace zoom fuerte o descarga.
///
/// Usa System.Drawing, que el API ya trae (el servidor es Windows). En otra plataforma devuelve null y quien llama
/// sirve el original.
/// </summary>
public static class FotoVariantes
{
    public const int MiniLado = 400;
    public const int MiniCalidad = 70;
    public const int VistaLado = 1280;
    public const int VistaCalidad = 80;

    /// <summary>Etiqueta EXIF de orientación (las fotos que no pasaron por la app pueden traerla).</summary>
    private const int ExifOrientacion = 0x0112;

    /// <summary>
    /// Genera miniatura y vista leyendo la foto una sola vez. Null si la imagen no se puede leer o la plataforma no
    /// tiene System.Drawing.
    /// </summary>
    public static FotoVariantesResultado? Generar(byte[] original)
    {
        if (!OperatingSystem.IsWindows() || original.Length == 0)
            return null;

        return GenerarEnWindows(original);
    }

    [SupportedOSPlatform("windows")]
    private static FotoVariantesResultado? GenerarEnWindows(byte[] original)
    {
        try
        {
            using var entrada = new MemoryStream(original, writable: false);
            using Image imagen = Image.FromStream(entrada, useEmbeddedColorManagement: false, validateImageData: true);
            AplicarOrientacion(imagen);

            int ancho = imagen.Width;
            int alto = imagen.Height;

            // La miniatura sale de la vista (ya reducida) y no del original: mismo resultado a la vista y mucho menos trabajo.
            using Bitmap vista = Reducir(imagen, VistaLado);
            using Bitmap mini = Reducir(vista, MiniLado);

            return new FotoVariantesResultado(
                Mini: Jpeg(mini, MiniCalidad),
                Vista: Jpeg(vista, VistaCalidad),
                AnchoOriginal: ancho,
                AltoOriginal: alto);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException or InvalidOperationException)
        {
            // GDI+ responde "OutOfMemory" o "parámetro no válido" a un archivo que no es imagen o está truncado.
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static Bitmap Reducir(Image origen, int ladoMaximo)
    {
        double razon = Math.Min(1.0, (double)ladoMaximo / Math.Max(origen.Width, origen.Height));
        int ancho = Math.Max(1, (int)Math.Round(origen.Width * razon));
        int alto = Math.Max(1, (int)Math.Round(origen.Height * razon));

        var destino = new Bitmap(ancho, alto, PixelFormat.Format24bppRgb);
        using Graphics g = Graphics.FromImage(destino);
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;

        // Sin esto el bicúbico promedia contra "afuera" de la imagen y deja un filo oscuro de un píxel en los bordes.
        using var atributos = new ImageAttributes();
        atributos.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(origen, new Rectangle(0, 0, ancho, alto), 0, 0, origen.Width, origen.Height, GraphicsUnit.Pixel, atributos);
        return destino;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Jpeg(Image imagen, int calidad)
    {
        ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parametros = new EncoderParameters(1);
        parametros.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)calidad);
        using var salida = new MemoryStream();
        imagen.Save(salida, codec, parametros);
        return salida.ToArray();
    }

    /// <summary>Endereza la imagen según su EXIF: la variante sale sin EXIF, así que la rotación tiene que ir en los píxeles.</summary>
    [SupportedOSPlatform("windows")]
    private static void AplicarOrientacion(Image imagen)
    {
        if (!imagen.PropertyIdList.Contains(ExifOrientacion))
            return;

        PropertyItem? propiedad = imagen.GetPropertyItem(ExifOrientacion);
        if (propiedad?.Value is not { Length: > 0 } valor)
            return;

        RotateFlipType giro = valor[0] switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone
        };

        if (giro != RotateFlipType.RotateNoneFlipNone)
            imagen.RotateFlip(giro);
    }
}
