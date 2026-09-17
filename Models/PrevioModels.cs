namespace PMMovil.Api.Models;

public class PrevioTraficoInfo
{
    public string Creferencia { get; set; } = string.Empty;
    public string Cclienteidtraf { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string Bodega { get; set; } = string.Empty;
    public string Tipop { get; set; } = string.Empty;
    public string Cnocaja { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string Correo { get; set; } = string.Empty;
    public string Factura { get; set; } = string.Empty;
    public string Proveedor { get; set; } = string.Empty;
    public string Pedido { get; set; } = string.Empty;
    public string Material { get; set; } = string.Empty;
    public int Idtraf { get; set; }
}

public class EnviarNotificacionRequest
{
    public string Referencia { get; set; } = string.Empty;
    public string Observaciones { get; set; } = string.Empty;
}

public class NuevoAvisoRequest
{
    public string Caja { get; set; } = string.Empty;
    public string ClienteId { get; set; } = string.Empty;
    public string FechaArribo { get; set; } = string.Empty;
    public string Hora { get; set; } = "12";
    public string Minuto { get; set; } = "00";
    public string Observaciones { get; set; } = string.Empty;
}

public class ClienteExpoInfo
{
    public string IdCliente { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string Carpeta { get; set; } = string.Empty;
}

public class PrevioResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class BuscarTraficoResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public PrevioTraficoInfo? Trafico { get; set; }
}
