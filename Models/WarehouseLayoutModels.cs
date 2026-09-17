using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PMMovil.Api.Models;

public class WarehouseLayoutResponse
{
    public bool Success { get; set; }
    public string WarehouseId { get; set; } = string.Empty;
    public JsonNode? Layout { get; set; }
    public string Source { get; set; } = string.Empty;
    public int Version { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTime? UpdatedAt { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class WarehouseLayoutSaveRequest
{
    public JsonElement Layout { get; set; }
}

public class WarehouseLayoutSaveResponse
{
    public bool Success { get; set; }
    public string WarehouseId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Ocupacion viva de las ubicaciones. Se calcula al vuelo cruzando trafdet,
/// traf y etiqueta_mcia. Solo incluye ubicaciones ocupadas; las que no aparecen
/// se consideran libres en el cliente.
/// </summary>
public class WarehouseOccupancyResponse
{
    public bool Success { get; set; }
    public string WarehouseId { get; set; } = string.Empty;
    public string GeneratedAt { get; set; } = string.Empty;
    public Dictionary<string, LocationOccupancy> Locations { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

public class LocationOccupancy
{
    public string Status { get; set; } = "ocupada";
    public string Confidence { get; set; } = "alta";
    public int Bultos { get; set; }
    public int EtiquetasPendientes { get; set; }
    public int EtiquetasCargadas { get; set; }
    public int EtiquetasSalida { get; set; }
    public int BultosSinEtiqueta { get; set; }
    public int DirectasSinEtiqueta { get; set; }
    public int EntradasCount { get; set; }
    public int EntradasOmitidas { get; set; }
    public string ClasePrincipal { get; set; } = string.Empty;
    public List<ClaseOccupancy> Clases { get; set; } = new();
    public List<OccupancyEntry> Entradas { get; set; } = new();
}

public class OccupancyEntry
{
    public string Creferencia { get; set; } = string.Empty;
    public string Ref { get; set; } = string.Empty;
    public string Tipop { get; set; } = string.Empty;
    public string TipoOperacion { get; set; } = string.Empty;
    public int Linea { get; set; }
    public string Cliente { get; set; } = string.Empty;
    public string ClienteId { get; set; } = string.Empty;
    public int Bultos { get; set; }
    public int EtiquetasPendientes { get; set; }
    public int EtiquetasCargadas { get; set; }
    public int EtiquetasSalida { get; set; }
    public int BultosSinEtiqueta { get; set; }
    public int DirectasSinEtiqueta { get; set; }
    public string Estado { get; set; } = string.Empty;
    public string Confidence { get; set; } = "alta";
    public string FechaArribo { get; set; } = string.Empty;
    public string Descripcion { get; set; } = string.Empty;
    public string Clase { get; set; } = string.Empty;
}

/// <summary>
/// Cantidad de bultos por clase de mercancia dentro de una ubicacion.
/// Permite al mapa dibujar el modelo 3D correspondiente al tipo de bulto.
/// </summary>
public class ClaseOccupancy
{
    public string Clase { get; set; } = string.Empty;
    public int Bultos { get; set; }
}
