namespace PMMovil.Api.Models;

public class ImexEntrada
{
    public string Entrada { get; set; } = "";
    public decimal Cantidad { get; set; }
    public string Clase { get; set; } = "";
    public string Descripcion { get; set; } = "";
    public decimal Peso { get; set; }
    public string Ubicacion { get; set; } = "";
    public string Observaciones { get; set; } = "";
    public string Relacion { get; set; } = "";
    public string Manejo { get; set; } = "";
}

public class ImexCaseta
{
    public string Casetaid { get; set; } = "";
    public string Caja { get; set; } = "";
    public string Placas { get; set; } = "";
    public string Idlinea { get; set; } = "";
    public string RazonLinea { get; set; } = "";
}

public class LineaTransportista
{
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
}

public class ImexRelacion
{
    public string Relacion { get; set; } = "";
    public string StatusRelacion { get; set; } = "";
    public string StatusCarga { get; set; } = "";
    public List<ImexEntrada> Entradas { get; set; } = [];
    public string UsuarioRegistro { get; set; } = "";
    public string UsuarioModifica { get; set; } = "";
    public DateTime FechaRegistro { get; set; }
    public DateTime FechaModifica { get; set; }
    public DateTime FechaCruce { get; set; }
    public string Observaciones { get; set; } = "";
    public string Caja { get; set; } = "";
    public string Placas { get; set; } = "";
    public string Aduana { get; set; } = "";
    public string Transporte { get; set; } = "";
    public string NombreTransporte { get; set; } = "";
    public string Bodega { get; set; } = "";
    public string Tipop { get; set; } = "";
    public int VersionHoja { get; set; } = 1;
    public string Casetaid { get; set; } = "";
}

public class GuardarImexRelacionRequest
{
    public string Relacion { get; set; } = "";
    public string? Bodega { get; set; }
    public string? Tipo { get; set; }
    public string? Anio { get; set; }
    public string? Numero { get; set; }
    public string Observaciones { get; set; } = "";
    public string Caja { get; set; } = "";
    public string Placas { get; set; } = "";
    public string Transporte { get; set; } = "";
    public string NombreTransporte { get; set; } = "";
    public string StatusRelacion { get; set; } = "";
    public string StatusCarga { get; set; } = "EN ESPERA";
    public int VersionHoja { get; set; } = 1;
    public string? Casetaid { get; set; }
    public string? Aduana { get; set; }
    public DateTime FechaCruce { get; set; }
    public List<ImexEntrada> Entradas { get; set; } = [];
}

public class ImexResult
{
    public bool Success { get; set; } = true;
    public string Message { get; set; } = "";
}
