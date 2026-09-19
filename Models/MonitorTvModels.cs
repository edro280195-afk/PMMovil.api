using System;
using System.Collections.Generic;

namespace PMMovil.Api.Models;

/// <summary>
/// Datos mínimos que se muestran en la pantalla pública de operación.
/// No incluye facturas, fotografías, documentos ni acciones de escritura.
/// </summary>
public sealed class MonitorTvSnapshot
{
    public DateTime GeneradoEn { get; init; }
    public IReadOnlyList<MonitorTvRevisor> Revisores { get; init; } = Array.Empty<MonitorTvRevisor>();
    public MonitorTvJornada Jornada { get; init; } = new();
    public IReadOnlyList<MonitorTvSolicitud> Solicitudes { get; init; } = Array.Empty<MonitorTvSolicitud>();
    public IReadOnlyList<MonitorTvEntrada> ColaSinAsignar { get; init; } = Array.Empty<MonitorTvEntrada>();
}

public sealed class MonitorTvRevisor
{
    public string Creferencia { get; init; } = string.Empty;
    public string Usuario { get; init; } = string.Empty;
    public string NombreCliente { get; init; } = string.Empty;
    public string Ubicacion { get; init; } = string.Empty;
    public DateTime? InicioRevision { get; init; }
    public DateTime? FinRevision { get; init; }
    public DateTime? TomaDocumento { get; init; }
    public int EstatusFinal { get; init; }
    public int TotalFacturas { get; init; }
    public int FacturasCompletas { get; init; }
    public int TotalPartidas { get; init; }
    public int PartidasRevisadas { get; init; }
    public int FacturasDiscrepancia { get; init; }
    public string UltimaDiscrepancia { get; init; } = string.Empty;
    public int Hazmat { get; init; }
    public bool RelacionCerrada { get; init; }
    public long SegundosAcumulados { get; init; }
    public DateTime? InicioSesionActual { get; init; }
    public string Estado { get; init; } = "INACTIVA";
    public bool RecienTerminada { get; init; }
}

public sealed class MonitorTvSolicitud
{
    public int Id { get; init; }
    public string Cnofact { get; init; } = string.Empty;
    public string Usuario { get; init; } = string.Empty;
    public string Motivo { get; init; } = string.Empty;
    public DateTime? FechaSolicitud { get; init; }
}

public sealed class MonitorTvEntrada
{
    public string CrefAgrupada { get; init; } = string.Empty;
    public string NombreCliente { get; init; } = string.Empty;
    public DateTime? DFecha { get; init; }
    public int TotalFacturas { get; init; }
    public int FacturasCompletas { get; init; }
    public int TotalPartidas { get; init; }
    public int PartidasRevisadas { get; init; }
    public bool TodasRelacionCompleta { get; init; }
    public DateTime? InicioRevision { get; init; }
    public string? UsuarioRevisando { get; init; }
    public string? UsuarioTomoDocumento { get; init; }
    public bool EsAsignada { get; init; }
    public int? AsignPrioridad { get; init; }
}

public sealed class MonitorTvJornada
{
    public string Fecha { get; init; } = string.Empty;
    public int TurnoInicioHora { get; init; }
    public int TurnoFinHora { get; init; }
    public int Meta { get; init; }
    public IReadOnlyList<MonitorTvCierre> Cierres { get; init; } = Array.Empty<MonitorTvCierre>();
}

public sealed class MonitorTvCierre
{
    public string Creferencia { get; init; } = string.Empty;
    public string Fecha { get; init; } = string.Empty;
    public string Usuario { get; init; } = string.Empty;
    public string NombreCliente { get; init; } = string.Empty;
    public bool Automatico { get; init; }
}

public sealed class MonitorTvLinkItem
{
    public long Id { get; init; }
    public string Nombre { get; init; } = string.Empty;
    public string TokenHint { get; init; } = string.Empty;
    public string UsuarioCrea { get; init; } = string.Empty;
    public DateTime FechaCreacion { get; init; }
    public DateTime? FechaExpiracion { get; init; }
    public bool Revocado { get; init; }
}

public sealed class CrearMonitorTvLinkRequest
{
    public string? Nombre { get; init; }
    /// <summary>0 significa sin fecha de expiración; el enlace se revoca manualmente.</summary>
    public int ExpiraEnDias { get; init; }
}

public sealed class CrearMonitorTvLinkResult
{
    public long Id { get; init; }
    public string Token { get; init; } = string.Empty;
    public string TokenHint { get; init; } = string.Empty;
    public string Nombre { get; init; } = string.Empty;
    public DateTime? FechaExpiracion { get; init; }
}
