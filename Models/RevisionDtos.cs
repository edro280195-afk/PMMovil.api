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
