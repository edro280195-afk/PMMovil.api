namespace PMMovil.Api.Models;

public class BodegaActivoItem
{
    public string Bodeguero { get; set; } = "";
    public string NombreCompleto { get; set; } = "";
    public string Relno { get; set; } = "";
    public string Caja { get; set; } = "";
    public string Bodega { get; set; } = "";
    public string Estatus { get; set; } = "";
    public string InicioCarga { get; set; } = "";
    public string Observa { get; set; } = "";
    public string Placas { get; set; } = "";
    public string Sellos { get; set; } = "";
    public string Creferencia { get; set; } = "";
    public string CargaObserva { get; set; } = "";
    public int TotalBultos { get; set; }
}

public class BodegaEntradaDto
{
    public string Creferencia { get; set; } = "";
    public string? Ref { get; set; }
    public string? FArribo { get; set; }
    public string? ClaveCte { get; set; }
    public string? Cliente { get; set; }
    public string? Caja { get; set; }
    public string? Guia { get; set; }
    public string? Recibio { get; set; }
    public string? Transportista { get; set; }
    public string? ClaveProv { get; set; }
    public string? Proveedor { get; set; }
    public string? Via { get; set; }
    public string? Manejo { get; set; }
    public string? TipoMcia { get; set; }
    public bool Inbond { get; set; }
    public bool Hazmat { get; set; }
    public string? FCreacion { get; set; }
    public string? FModi { get; set; }
}

public class BodegaActividadEvent
{
    public string Tipo { get; set; } = ""; public string Relno { get; set; } = "";
    public string Bodeguero { get; set; } = "";
    public string Mensaje { get; set; } = "";
    public string Timestamp { get; set; } = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
}
