namespace PMMovil.Api.Models;

public class InventarioBulto
{
    public string Textoetiqueta { get; set; } = "";
    public string Usuario { get; set; } = "";
    public DateTime Fecha { get; set; }
    public string Ubicacion { get; set; } = "";
}

public class InventarioScanRequest
{
    public string CodigoBarras { get; set; } = "";
    public string Ubicacion { get; set; } = "";
    public string Usuario { get; set; } = "";
}

public class InventarioEntradaResult
{
    public string Creferencia { get; set; } = "";
    public decimal? Total { get; set; }
}

public class InventarioResult
{
    public bool Success { get; set; } = true;
    public string Message { get; set; } = "";
}
