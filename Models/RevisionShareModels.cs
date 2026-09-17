namespace PMMovil.Api.Models;

/// <summary>
/// Lo que necesita la página pública de "Seguimiento de revisión" para
/// pintarse -- página SEPARADA de "Seguimiento de entrada"
/// (EntradaShareResumen): esta muestra estatus por factura, discrepancias y
/// evidencia fotográfica de revisión (fact/revdet/fotos ntipodocto=103),
/// nunca los datos de traf que muestra la de Entrada. Ver
/// RevisionShareService.
/// </summary>
public class RevisionShareResumen
{
    public string CrefAgrupada { get; set; } = string.Empty;
    public string NombreCliente { get; set; } = string.Empty;
    public int TotalFacturas { get; set; }
    public int FacturasCompletas { get; set; }
    public int TotalPartidas { get; set; }
    public int PartidasRevisadas { get; set; }
    public DateTime GeneradoEn { get; set; }
    public DateTime Expira { get; set; }
    public List<RevisionShareFactura> Facturas { get; set; } = new();
    /// <summary>Evidencia normal de revisión (ntipodocto=107).</summary>
    public List<RevisionShareFoto> FotosNormales { get; set; } = new();
    /// <summary>Evidencia de discrepancia (ntipodocto=103) -- separada a propósito de las normales.</summary>
    public List<RevisionShareFoto> FotosDiscrepancia { get; set; } = new();
}

public class RevisionShareFactura
{
    public string Cnofact { get; set; } = string.Empty;
    /// <summary>0 = Pendiente, 1 = Completa, 2 = Diferencia, 3 = En proceso (mismos códigos que en la app).</summary>
    public int StatusRev { get; set; }
    public string Revisor { get; set; } = string.Empty;
    public int TotBultos { get; set; }
    public int TotBultosDoc { get; set; }
    public string Discrepancias { get; set; } = string.Empty;
}

public class RevisionShareFoto
{
    public int IdFotos { get; set; }
    public int NLineNo { get; set; }
    /// <summary>
    /// Fecha y hora en que el API registró la foto en la base de datos.
    /// No representa necesariamente la hora del disparo de la cámara.
    /// </summary>
    public string FCrea { get; set; } = string.Empty;
}
