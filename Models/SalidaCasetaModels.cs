namespace PMMovil.Api.Models;

public class SalidaCajaItem
{
    public string CasetaId { get; set; } = string.Empty;
    public string NumCaja { get; set; } = string.Empty; public string LineaCaja { get; set; } = string.Empty; public string NumCamion { get; set; } = string.Empty; public string Placas { get; set; } = string.Empty;
    public string NombreCliente { get; set; } = string.Empty; public string TipoUnidad { get; set; } = string.Empty; public string TipoMovimiento { get; set; } = string.Empty; public string TipoOp { get; set; } = string.Empty; public string Planta { get; set; } = string.Empty;
    public DateTime? FechaArribo { get; set; }
    public int DiasEnPatio { get; set; }
}

public class SalidaCajaPendientesResult
{
    public bool Success { get; set; }
    public List<SalidaCajaItem> Pendientes { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

public class RevisarRelacionResult
{
    public bool Success { get; set; }
    public string Relacion { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class RegistrarSalidaCajaRequest
{
    public string CasetaId { get; set; } = string.Empty;
    public string Observaciones { get; set; } = string.Empty;
}

public class RegistrarSalidaCajaResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
