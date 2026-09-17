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
    public List<EntradaShareFoto> Fotos { get; set; } = new();
}

public class EntradaShareFoto
{
    public int NLineNo { get; set; }
    public string? Caption { get; set; }
}
