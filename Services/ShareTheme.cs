namespace PMMovil.Api.Services;

/// <summary>
/// Apariencia de las páginas públicas de seguimiento (los enlaces que llegan por correo).
/// Cambia solo el aspecto: los textos, las tablas y los datos son siempre los mismos.
/// </summary>
public enum ShareTheme
{
    /// <summary>La apariencia institucional de siempre.</summary>
    None,

    /// <summary>Cabecera nocturna en capas con parallax, tarjetas de papel y cementerio en el pie.</summary>
    Halloween,
}

/// <summary>
/// Decide qué apariencia lleva el enlace al abrirse (no al generarse), de modo que los enlaces ya enviados
/// también cambian y vuelven solos a la institucional al terminar la temporada.
/// </summary>
public static class ShareThemeResolver
{
    /// <summary>Clave de configuración: "auto" (por defecto), "halloween" o "ninguno".</summary>
    public const string ConfigurationKey = "PublicLinks:Tema";

    /// <summary>
    /// "halloween" lo fuerza todo el año y "ninguno" lo apaga. Con "auto", un valor vacío o uno que no se
    /// reconoce, Halloween se activa del 1 de octubre al 2 de noviembre y el resto del año queda la institucional.
    /// </summary>
    public static ShareTheme Resolve(string? setting, DateTime now) =>
        (setting ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "halloween" => ShareTheme.Halloween,
            "ninguno" or "none" or "institucional" => ShareTheme.None,
            _ => IsHalloweenSeason(now) ? ShareTheme.Halloween : ShareTheme.None,
        };

    /// <summary>Del 1 de octubre al 2 de noviembre, ambos días incluidos (la noche del 2 todavía es de temporada).</summary>
    public static bool IsHalloweenSeason(DateTime now) =>
        now.Month == 10 || (now.Month == 11 && now.Day <= 2);
}
