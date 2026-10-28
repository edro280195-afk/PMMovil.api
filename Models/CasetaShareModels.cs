namespace PMMovil.Api.Models;

public sealed class CasetaShareResumen
{
    public string Referencia { get; init; } = string.Empty;
    public string Tipo { get; init; } = string.Empty;
    public DateTime GeneradoEn { get; init; }
    public DateTime Expira { get; init; }
    public List<CasetaShareFoto> Fotos { get; init; } = [];

    /// <summary>
    /// Datos de la relación (salida) o del registro de caseta (entrada) que acompañan a las fotos. Es `null` si no se
    /// pudieron leer: la página abre igual, solo con las fotos.
    /// </summary>
    public CasetaShareDetalle? Detalle { get; init; }
}

public sealed class CasetaShareFoto
{
    public int IdFotos { get; init; }
    public int Indice { get; init; }

    /// <summary>`dd/MM/yyyy HH:mm:ss` en que se subió la foto.</summary>
    public string Fecha { get; init; } = string.Empty;
}

/// <summary>
/// Lo que la página pública muestra junto a las fotos. Deliberadamente SIN clientes ni pedimentos: el mismo enlace llega a
/// todos los clientes de una relación, y cada uno solo debe ver lo suyo en su correo.
/// </summary>
public sealed class CasetaShareDetalle
{
    /// <summary>Caja de la relación (salida) o equipo del registro (entrada).</summary>
    public string Caja { get; init; } = string.Empty;

    /// <summary>Transportista de la relación (salida) o línea de la caja (entrada).</summary>
    public string Transportista { get; init; } = string.Empty;

    // Solo salida.
    public string Operador { get; init; } = string.Empty;
    public string Tractor { get; init; } = string.Empty;
    public string Sellos { get; init; } = string.Empty;
    public DateTime? Autorizada { get; init; }
    public DateTime? Salida { get; init; }

    // Solo entrada.
    public string Placas { get; init; } = string.Empty;
    public string Movimiento { get; init; } = string.Empty;
    public DateTime? Registrada { get; init; }

    /// <summary>Cuando la caja llegó a caseta (ambos tipos; en salida puede faltar si la caja nunca se registró).</summary>
    public DateTime? Llegada { get; init; }
}
