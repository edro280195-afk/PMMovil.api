using System;
using System.Collections.Generic;

namespace PMMovil.Api.Models;

/// <summary>
/// Fila cruda leída de pmmtraf antes de derivar EsSubdivision/FueraVentana/etc.
/// Deliberadamente sin calcular nada en SQL: el volumen (cientos de filas por
/// consulta) es bajo, así que igual que <c>VelocidadRevisor</c> en
/// <c>MonitorController</c>, se agrega en memoria — más fácil de auditar y de
/// corregir cuando una regla de negocio cambia.
/// </summary>
public class MetricaRevisionRow
{
    public string Creferencia { get; set; } = "";
    public DateTime Arribo { get; set; }
    public string? ClienteId { get; set; }
    public string? Cliente { get; set; }
    public string? Manejo { get; set; }
    public string Planta { get; set; } = "";
    public int Status { get; set; }
    public DateTime? FechaFactura { get; set; }
    public DateTime? FechaPrimeraFactura { get; set; }
    public DateTime? FInicioRevision { get; set; }
    public DateTime? FFinRevision { get; set; }
    public string? Revisor { get; set; }
    public bool TieneSesionApp { get; set; }
    public bool TieneTiemposPropios { get; set; }
    public string? Observaciones { get; set; }
    public decimal? Peso { get; set; }
    public decimal? Cantidad { get; set; }
    public int TotalFacturas { get; set; }
    public int FacturasPendientesRevision { get; set; }
    public int Discrepancias { get; set; }
    public string? OficinaOrigen { get; set; }
}

public class KpisDto
{
    public int Total { get; set; }
    public int NoRequiere { get; set; }
    public int Requiere { get; set; }
    public int ConFactura { get; set; }
    public int FueraVentana { get; set; }
    public int ConComentarios { get; set; }
    public int Revisadas { get; set; }
    public int Pendientes { get; set; }
    public int Extraordinarias { get; set; }
    public int Subdivisiones { get; set; }
    public int ViaApp { get; set; }
    public int ViaManual { get; set; }
    /// <summary>Entradas PIPA que quedaron fuera del resumen (no cuentan en ningún número).</summary>
    public int Pipa { get; set; }
    /// <summary>Entradas que llegaron después de las 17:00 del día de arribo.</summary>
    public int EntradasTarde { get; set; }
    /// <summary>Unión: entrada tardía y/o factura fuera de ventana (cada entrada cuenta una sola vez).</summary>
    public int DespuesDeCinco { get; set; }
    public int ConFacturaEnVentana { get; set; }
    public int RevisadasConFacturaEnVentana { get; set; }
    public double PctFacturaRevisada { get; set; }
    public double PctVentana { get; set; }
    public double PctApp { get; set; }
}

public class TrendPointDto
{
    public string Fecha { get; set; } = "";
    public int Recibidas { get; set; }
    public int Revisadas { get; set; }
}

public class AdopcionPointDto
{
    public string Fecha { get; set; } = "";
    public int ViaApp { get; set; }
    public int ViaManual { get; set; }
}

public class AntiguedadDto
{
    public int Menos4h { get; set; }
    public int Entre4y24h { get; set; }
    public int Mas24h { get; set; }
}

public class RevisorStatDto
{
    public string Usuario { get; set; } = "";
    public int Cantidad { get; set; }
    public double TiempoPromedioMin { get; set; }
    public double? PctApp { get; set; }
}

public class MetricasResumenResponse
{
    public bool Success { get; set; } = true;
    public int FilasConsideradas { get; set; }
    public KpisDto Kpis { get; set; } = new();
    public List<TrendPointDto> Tendencia { get; set; } = new();
    public List<AdopcionPointDto> Adopcion { get; set; } = new();
    public AntiguedadDto Antiguedad { get; set; } = new();
    public List<RevisorStatDto> Revisores { get; set; } = new();
}

public class EvidenciaRowDto
{
    public string Creferencia { get; set; } = "";
    public string? Cliente { get; set; }
    public DateTime Arribo { get; set; }
    public DateTime? FechaFactura { get; set; }
    public DateTime? FechaPrimeraFactura { get; set; }
    public bool TieneFactura { get; set; }
    public bool FueraVentana { get; set; }
    public string? Manejo { get; set; }
    public string Estatus { get; set; } = "";
    public string? Origen { get; set; }
    public string? Revisor { get; set; }
    public DateTime? FechaInicioRevision { get; set; }
    public DateTime? FechaFinRevision { get; set; }
    public int? TiempoMin { get; set; }
    public bool Extraordinaria { get; set; }
    public bool EsSubdivision { get; set; }
    public int FacturasPendientesRevision { get; set; }
    public int Discrepancias { get; set; }
    public string? OficinaOrigen { get; set; }
}

public class EvidenciaResponse
{
    public bool Success { get; set; } = true;
    public int Total { get; set; }
    public bool Truncado { get; set; }
    public List<EvidenciaRowDto> Items { get; set; } = new();
}
