namespace PMMovil.Api.Models;

public class CasetaScanResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? CasetaId { get; set; }
    public string? NumCaja { get; set; }
    public string? ClienteId { get; set; }
    public string? NombreCliente { get; set; }
    public string? TransporteId { get; set; }
    public string? NombreTransporte { get; set; }
    public string? Placas { get; set; }
    public string? Sellos { get; set; }
    public string? Operador { get; set; }
    public string? Planta { get; set; }
    public string? FechaArribo { get; set; }
    public int FotosCount { get; set; }
    public List<CasetaFotoInfo> Fotos { get; set; } = new();

    public bool EntradaExistente { get; set; }
    public string? ReferenciaExistente { get; set; }
}

public class CasetaFotoInfo
{
    public int Indice { get; set; }
    public string NombreArchivo { get; set; } = "";
}
