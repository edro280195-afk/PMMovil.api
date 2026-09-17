namespace PMMovil.Api.Models;

public class SubirFotoRequest
{
    public string creferencia { get; set; } = "";
    public string cclienteid { get; set; } = "";
    public string image { get; set; } = "";
    public string? extension { get; set; }

    /// <summary>
    /// true = evidencia tomada dentro del flujo de "Registrar discrepancia"
    /// (se guarda con ntipodocto=103, el mismo que usa Entrada Bodega para
    /// fotos de daño — decisión de equipo: Entrada y Revisión comparten
    /// apartado en el PMM/FoxPro). false = evidencia normal de revisión
    /// (ntipodocto=107).
    /// </summary>
    public bool EsDiscrepancia { get; set; }
}
