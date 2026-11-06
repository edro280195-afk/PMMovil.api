namespace PMMovil.Api.Models;

/// <summary>Respuesta al generar un link de "Seguimiento de entrada".</summary>
public class EntradaShareLinkResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Url { get; set; }
    public DateTime? Expira { get; set; }
}

/// <summary>Respuesta al generar un link temporal de reporte PDF (vivo o congelado).</summary>
public class ReportShareLinkResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Url { get; set; }
    public DateTime? Expira { get; set; }
}

/// <summary>
/// Lo que necesita la página pública para pintarse -- nunca datos
/// financieros, y nunca datos de revisión (estatus, revisor, discrepancias):
/// esta página es de la ENTRADA, un proceso distinto y anterior a la
/// revisión. Ver EntradaShareService para la explicación completa.
/// </summary>
public class EntradaShareResumen
{
    public string CrefAgrupada { get; set; } = string.Empty;
    public string NombreCliente { get; set; } = string.Empty;
    public string NombreTransportista { get; set; } = string.Empty;
    public string Unidad { get; set; } = string.Empty;
    public string Ubicacion { get; set; } = string.Empty;
    public string? FechaLlegada { get; set; }
    public DateTime GeneradoEn { get; set; }
    public DateTime Expira { get; set; }

    /// <summary>Fotos de evidencia de la entrada (ntipodocto 100).</summary>
    public List<EntradaShareFoto> Fotos { get; set; } = new();

    /// <summary>La entrada se declaró con mercancía dañada (<c>traf3.danada</c>).</summary>
    public bool Danada { get; set; }

    /// <summary>Descripción libre que escribió bodega. La página la puede ocultar (<c>PublicLinks:MostrarDescripcionDanioEntrada</c>).</summary>
    public string DanadaDescripcion { get; set; } = string.Empty;

    /// <summary>
    /// Fotos de daño: <c>fotos</c> con <c>ntipodocto = 103</c> («DAÑADA»). Solo se llenan si la entrada se declaró dañada.
    /// </summary>
    public List<EntradaShareFoto> FotosDanio { get; set; } = new();

    /// <summary>Fotos de daño que el servidor espera y todavía no llegan (la cola de la app las sube después de guardar).</summary>
    public int FotosDanioPendientes { get; set; }
}

public class EntradaShareFoto
{
    public int NLineNo { get; set; }
    public string? Caption { get; set; }

    /// <summary>Hora (HH:mm) en que se subió la foto; vacía si no se sabe.</summary>
    public string Hora { get; set; } = string.Empty;
}
