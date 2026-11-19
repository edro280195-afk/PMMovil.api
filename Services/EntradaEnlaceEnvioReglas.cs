namespace PMMovil.Api.Services;

/// <summary>
/// Reglas puras (sin base de datos) del enlace que se crea al registrar una entrada de bodega y que el otro sistema
/// (PMM / VFP) toma de <c>pmmutileria.entrada_enlaces_envio</c>. Ver <see cref="EntradaShareService.AsegurarEnlaceEnvioAsync"/>.
/// </summary>
public static class EntradaEnlaceEnvioReglas
{
    /// <summary>Vigencia del enlace de envío: 3 meses calendario (el de «Compartir» del teléfono sigue siendo de 72 h).</summary>
    public const int VigenciaMeses = 3;

    /// <summary>Ruta pública de «Seguimiento de entrada» (PublicoController): base + ruta + token.</summary>
    public const string RutaPublica = "/api/publico/entrada/";

    /// <summary>
    /// Cuándo vence un enlace creado en <paramref name="ahora"/>. Es calendario, no 90 días: el 10-nov vence el 10-feb, y el
    /// 30-nov vence el 28-feb (el mes corto se recorta al último día, nunca se pasa al mes siguiente).
    /// </summary>
    public static DateTime Expira(DateTime ahora) => ahora.AddMonths(VigenciaMeses);

    /// <summary>
    /// URL completa del enlace, o null si la base no sirve (vacía, relativa o que no sea http/https): preferimos no crear el
    /// enlace a dejarle al otro sistema una URL que no abre.
    /// </summary>
    public static string? ConstruirUrl(string? baseUrl, string token)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string baseLimpia = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseLimpia, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return baseLimpia + RutaPublica + token.Trim();
    }
}
