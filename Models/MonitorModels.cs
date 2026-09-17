namespace PMMovil.Api.Models;

public class MetaDiariaRequest
{
    public int Meta { get; set; }
}

public class MonitorConfiguracionRequest
{
    public int Meta { get; set; }
    public int TurnoInicioHora { get; set; }
    public int TurnoFinHora { get; set; }
}

public class ForzarCierreRequest
{
    public string Motivo { get; set; } = "";
}

public class MonitorItem
{
    public string creferencia { get; set; } = "";
    public string usuario { get; set; } = "";
    public string nombreCliente { get; set; } = "";
    public string ubicacion { get; set; } = "";
    public string inicioRevision { get; set; } = "";
    public string finRevision { get; set; } = "";
    public string tomaDocumento { get; set; } = "";
    public int estatusFinal { get; set; }
    public int totalFacturas { get; set; }
    public int facturasCompletas { get; set; }
    public int facturasDiscrepancia { get; set; }
    public string ultimaDiscrepancia { get; set; } = "";
    public int hazmat { get; set; }
    public bool relacionCerrada { get; set; }
    public long segundosAcumulados { get; set; }
    public bool recienTerminada { get; set; }
}
