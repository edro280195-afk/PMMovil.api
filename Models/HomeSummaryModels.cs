namespace PMMovil.Api.Models;

/// <summary>Códigos de la excepción de una estación del Tablero. El servidor da códigos; la app pone las frases.</summary>
public static class HomeExceptionCode
{
    /// <summary>Cajas que llevan más de <see cref="PMMovil.Api.Services.HomeSummaryRules.OldYardDays"/> días en el patio.</summary>
    public const string OldInYard = "OLD_IN_YARD";

    /// <summary>De mis entradas, las que tienen una factura revisada con discrepancias.</summary>
    public const string Discrepancies = "DISCREPANCIAS";

    /// <summary>Relaciones bloqueadas o con diferencia que un supervisor debe resolver.</summary>
    public const string Blocked = "BLOQUEADAS";

    /// <summary>Relaciones autorizadas que ya pueden salir de la caseta.</summary>
    public const string ReadyToExit = "LISTAS_PARA_SALIR";
}

/// <summary>Lo que cuelga como ramal bajo una estación: el caso más fuerte. Solo existe si hay al menos uno.</summary>
public sealed class HomeStationException
{
    /// <summary>Señal con los mismos valores de <see cref="TraceSignal"/>: ALTO, ATENCION, LISTO o AVISO.</summary>
    public string Kind { get; set; } = TraceSignal.Aviso;

    /// <summary>Uno de <see cref="HomeExceptionCode"/>.</summary>
    public string Code { get; set; } = string.Empty;

    public int Count { get; set; }
}

/// <summary>La cifra de una estación y, si hay, su excepción.</summary>
public sealed class HomeStationSummary
{
    public int Count { get; set; }
    public HomeStationException? Exception { get; set; }
}

/// <summary>
/// El resumen del Tablero del inicio. Cada estación es <c>null</c> si el usuario no tiene su módulo o si no se pudo
/// calcular: una consulta que falla nunca tira a las demás ni al inicio.
/// </summary>
public sealed class HomeSummaryResult
{
    public bool Success { get; set; } = true;
    public HomeStationSummary? Caseta { get; set; }
    public HomeStationSummary? Entrada { get; set; }
    public HomeStationSummary? Revision { get; set; }
    public HomeStationSummary? Relacion { get; set; }
    public HomeStationSummary? Salida { get; set; }

    /// <summary>Cuánto vale la respuesta del servidor antes de recalcularse; la app no necesita preguntar más seguido.</summary>
    public int CacheSeconds { get; set; }
}
