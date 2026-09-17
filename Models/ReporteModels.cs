namespace PMMovil.Api.Models;

public class ReporteInfo
{
    public string IdReporte { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Descripcion { get; set; } = "";

    public bool TieneRangoFechas { get; set; }
    public bool TieneTipoFecha { get; set; }
    public bool TieneCliente { get; set; }
    public bool TieneBodega { get; set; }
    public bool TieneProveedor { get; set; }
    public bool TieneExtension { get; set; }
    public bool TieneFacturo { get; set; }
    public bool TieneNaviera { get; set; }
    public bool TieneModelo { get; set; }
    public bool TieneConsigna { get; set; }
    public bool TienePatente { get; set; }
    public bool TieneAduana { get; set; }
    public bool TieneClavePedimento { get; set; }
    public bool TieneTipoOp { get; set; }
    public bool TienePedimento { get; set; }
    public bool TieneTrafico { get; set; }
    public bool TieneFraccion { get; set; }
    public bool TieneOrdenamiento { get; set; }
    public bool TieneOtro { get; set; }
    public bool TieneAcumulado { get; set; }
    public bool TieneInactiva { get; set; }
    public bool TieneTipoDocumento { get; set; }
}

public class QueryDefinicion
{
    public int Id { get; set; }
    public string ConnectionString { get; set; } = "";
    public string Select { get; set; } = "";
    public string Caption { get; set; } = ""; public string Where { get; set; } = ""; public string GroupOrder { get; set; } = ""; public string FieldsWhere { get; set; } = ""; public int FechaEsp { get; set; }
    public string Titulo { get; set; } = ""; public int Orden { get; set; }
}

public class GenerarReporteRequest
{
    public string IdReporte { get; set; } = "";
    public string? IdCliente { get; set; }
    public string? FechaInicio { get; set; }
    public string? FechaFin { get; set; }
    public string? TipoFecha { get; set; }

    public string? Bodega { get; set; }
    public string? Proveedor { get; set; }
    public string? Extension { get; set; }
    public string? Facturo { get; set; }
    public string? Naviera { get; set; }
    public string? Modelo { get; set; }
    public string? Consigna { get; set; }
    public string? Patente { get; set; }
    public string? Aduana { get; set; }
    public string? ClavePedimento { get; set; }
    public string? Ordenamiento { get; set; }
    public string? OtroCampo { get; set; }
    public string? OtroValor { get; set; }
    public string? Pedimento { get; set; }
    public string? Trafico { get; set; }
    public string? Fraccion { get; set; }
    public string? TipoDocumento { get; set; }
    public List<string> TiposOp { get; set; } = []; public bool? Inactiva { get; set; }
    public bool? Acumulado { get; set; }

    public string Formato { get; set; } = "json";
}

public class TablaReporte
{
    public string Titulo { get; set; } = "";
    public List<string> Columnas { get; set; } = [];
    public List<Dictionary<string, object?>> Filas { get; set; } = [];
    public int TotalFilas { get; set; }
}

public class ReporteResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string IdReporte { get; set; } = "";
    public string Nombre { get; set; } = "";
    public DateTime FechaGeneracion { get; set; } = DateTime.Now;
    public List<TablaReporte> Tablas { get; set; } = [];
}

public class FiltroOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}
