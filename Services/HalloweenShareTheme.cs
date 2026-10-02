namespace PMMovil.Api.Services;

/// <summary>
/// Las piezas de HTML que un tema agrega a una página pública. Todas son cadenas ya armadas (el tema se compone
/// una sola vez al iniciar), así que atender una visita es concatenar. Con <see cref="ShareTheme.None"/> todas
/// van vacías y la página sale exactamente igual que antes de existir los temas.
/// </summary>
public sealed class ShareThemeParts
{
    /// <summary>Sin tema: ninguna pieza.</summary>
    public static readonly ShareThemeParts None = new();

    /// <summary>Hoja de estilos adicional, con su etiqueta style (va después de la hoja base).</summary>
    public string Css { get; init; } = string.Empty;

    /// <summary>Atributos del body (la clase que activa el tema).</summary>
    public string BodyAttributes { get; init; } = string.Empty;

    /// <summary>Fondo y cabecera ilustrados, justo al abrir el body.</summary>
    public string Header { get; init; } = string.Empty;

    /// <summary>Icono antes de la etiqueta de la tarjeta de resumen.</summary>
    public string Eyebrow { get; init; } = string.Empty;

    /// <summary>Adornos de la esquina de la tarjeta de resumen.</summary>
    public string CardDecor { get; init; } = string.Empty;

    /// <summary>Adorno sobre la tarjeta del enlace vencido.</summary>
    public string Ghost { get; init; } = string.Empty;

    /// <summary>Ilustración del pie de página.</summary>
    public string Footer { get; init; } = string.Empty;

    /// <summary>Script del tema (parallax de respaldo), al final del body.</summary>
    public string Script { get; init; } = string.Empty;

    /// <summary>Piezas del tema para la página de resumen o para la de enlace vencido.</summary>
    public static ShareThemeParts For(ShareTheme theme, bool expired) => theme switch
    {
        ShareTheme.Halloween => expired ? HalloweenShareTheme.Expired : HalloweenShareTheme.Summary,
        _ => None,
    };
}

/// <summary>
/// Tema Halloween: cabecera nocturna en capas (luna, colinas, casa embrujada, murciélagos) con parallax al
/// hacer scroll, cielo estrellado fijo, tarjetas de papel y un cementerio que asoma al llegar al final.
/// No agrega ni cambia ningún texto de la página; todo lo ilustrado queda oculto para lectores de pantalla.
/// El diseño vive en <see cref="HalloweenShareAssets"/>.
/// </summary>
internal static class HalloweenShareTheme
{
    private static readonly string CssBlock = "\n<style>" + HalloweenShareAssets.Css + "</style>";

    private static readonly string Header =
        HalloweenShareAssets.Sprite + "\n"
        + "<div class=\"hw-cielo\" aria-hidden=\"true\">" + HalloweenShareAssets.FixedSky + "</div>\n"
        + "<div class=\"hw-escena\" aria-hidden=\"true\">\n" + HalloweenShareAssets.HeaderLayers + "\n</div>\n";

    private static readonly string Footer =
        "<div class=\"hw-cementerio\" aria-hidden=\"true\">\n" + HalloweenShareAssets.FooterLayers + "\n</div>\n";

    private static readonly string Script = "\n<script>" + HalloweenShareAssets.Script + "</script>";

    public static readonly ShareThemeParts Summary = new()
    {
        Css = CssBlock,
        BodyAttributes = " class=\"tema-halloween\"",
        Header = Header,
        Eyebrow = HalloweenShareAssets.PumpkinIcon,
        CardDecor = "\n    " + HalloweenShareAssets.Cobweb + HalloweenShareAssets.HangingSpider,
        Footer = Footer,
        Script = Script,
    };

    public static readonly ShareThemeParts Expired = new()
    {
        Css = CssBlock,
        BodyAttributes = " class=\"tema-halloween pagina-vencida\"",
        Header = Header,
        Ghost = HalloweenShareAssets.Ghost,
        Footer = "\n" + Footer,
        Script = Script,
    };
}
