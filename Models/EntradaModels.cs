namespace PMMovil.Api.Models;

public class CatalogoEntradaItem
{
    public string Value { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public class CatalogosEntradaResult
{
    public List<CatalogoEntradaItem> Bodegas { get; set; } = new();
    public List<CatalogoEntradaItem> ClasesHazmat { get; set; } = new();
    public List<CatalogoEntradaItem> Manejos { get; set; } = new();
    public List<CatalogoEntradaItem> CajasArribo { get; set; } = new();
    public List<CatalogoEntradaItem> Recibidores { get; set; } = new();
    public List<CatalogoEntradaItem> Ubicaciones { get; set; } = new();
    public List<CatalogoEntradaItem> Clases { get; set; } = new();
}

public class ConsecutivoResult
{
    public bool Success { get; set; }
    public string Numero { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class DetalleRecepcion
{
    public decimal Cantidad { get; set; }
    public string Clase { get; set; } = string.Empty;
    public string Descripcion { get; set; } = string.Empty;
    public string Atados { get; set; } = string.Empty;
    public string Ubicacion { get; set; } = string.Empty;
}

public class GuardarRecepcionRequest
{
    public string Ref1 { get; set; } = string.Empty;
    public string Ref2 { get; set; } = string.Empty;
    public string Ref3 { get; set; } = string.Empty;
    public string Ref4 { get; set; } = string.Empty;
    public string Ref5 { get; set; } = string.Empty;
    public string Ref6 { get; set; } = string.Empty;
    public string Fecha { get; set; } = string.Empty;
    public string Bodega { get; set; } = string.Empty;
    public string CodCliente { get; set; } = string.Empty;
    public string NomCliente { get; set; } = string.Empty;
    public bool Hazmat { get; set; }
    public string Clase { get; set; } = string.Empty;
    public string UN { get; set; } = string.Empty;
    public bool Danada { get; set; }
    public string DanadaDescripcion { get; set; } = string.Empty;
    public string CodTransporte { get; set; } = string.Empty;
    public string NomTransporte { get; set; } = string.Empty;
    public string Manejo { get; set; } = string.Empty;
    public string Guia { get; set; } = string.Empty;
    public string CajaArribo { get; set; } = string.Empty;
    public string? CasetaId { get; set; }
    public bool InBond { get; set; }
    public string InBondVal { get; set; } = string.Empty;
    public decimal TotalRecibido { get; set; }
    public decimal PesoLbs { get; set; }
    public decimal PesoKgs { get; set; }
    public string Observaciones { get; set; } = string.Empty;
    public string Recibio { get; set; } = string.Empty;
    public bool MaderaSello { get; set; }
    public int MaderaSelloCant { get; set; }
    public bool MaderaSinSello { get; set; }
    public int MaderaSinSelloCant { get; set; }
    public bool Aserrin { get; set; }
    public int AserrinCant { get; set; }
    public bool NoSello { get; set; }
    public int NoSelloCant { get; set; }
    public List<DetalleRecepcion> Detalles { get; set; } = new();
    public List<string> Fotos { get; set; } = new();
    public List<string> FotosDanio { get; set; } = new();
}

public class EntradaFrequentItem
{
    public string Id { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public int Uso { get; set; }
    public DateTime? UltimoUso { get; set; }
}

public class ResultadoOperacion
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? RecepcionId { get; set; }
}
