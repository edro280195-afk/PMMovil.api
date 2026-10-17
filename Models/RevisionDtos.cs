using System.Text.Json;

namespace PMMovil.Api.Models;

public class AccionFacturaDto
{
    public string cnofact { get; set; } = "";
}

public class AgregarProductoDto
{
    public string cnofact { get; set; } = "";
    public string cref { get; set; } = "";
}

public class GuardarCampoFacturaDto
{
    public string cnofact { get; set; } = "";
    public string campo { get; set; } = "";
    public JsonElement valor { get; set; }
}

public class GuardarCampoPartidaDto
{
    public int id { get; set; }
    public string campo { get; set; } = "";
    public JsonElement valor { get; set; }
}

/// <summary>
/// Guarda cantidad comercial y unidad en conjunto, como lo hace el formulario
/// legacy: el cliente envía el código visible y el servidor resuelve el
/// umcint/cunidadfactura canónicos desde pmmtraf.unidades.
/// </summary>
public class GuardarCantidadComercialDto
{
    public int id { get; set; }
    public decimal cantidad { get; set; }
    public string unidad { get; set; } = "";
}

public class SolicitarDesbloqueoDto
{
    public string cnofact { get; set; } = "";
    public string motivo { get; set; } = "";
}

/// <summary>Observación general de TODA la revisión (traf3.observacion_revision) --
/// no por factura, por eso va por cref_agrupada y no por cnofact.</summary>
public class GuardarObservacionEntradaDto
{
    public string crefAgrupada { get; set; } = "";
    public string valor { get; set; } = "";
}

/// <summary>Datos del rombo de mercancía peligrosa, SEPARADOS como vienen impresos:
/// número UN arriba, clase en el vértice y grupo de embalaje al pie. Van a traf3,
/// que es por referencia, no por partida.</summary>
public class GuardarHazmatRevisionDto
{
    public string crefAgrupada { get; set; } = "";
    /// <summary>Solo el número, sin el prefijo "UN": 1866, 3082.</summary>
    public string? un { get; set; }
    /// <summary>Clase de peligro del catálogo clase_hazmat: 3, 4.1, 6.1.</summary>
    public string? clase { get; set; }
    /// <summary>Grupo de embalaje: I, II o III.</summary>
    public string? grupo { get; set; }
}
