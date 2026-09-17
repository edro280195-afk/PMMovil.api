using System;

namespace PMMovil.Api.Models;

public class AsignarRequest
{
    public string CrefAgrupada { get; set; } = "";
    public string Usuario { get; set; } = "";
    public int Prioridad { get; set; }
    public string? Nota { get; set; }
}

public class AsignacionRevision
{
    public int IdAsignacion { get; set; }
    public string CrefAgrupada { get; set; } = "";
    public string UsuarioAsignado { get; set; } = "";
    public string UsuarioAsigna { get; set; } = "";
    public DateTime FechaAsignacion { get; set; }
    public DateTime? FechaVisto { get; set; }
    public DateTime? FechaCompletada { get; set; }
    public int Prioridad { get; set; }
    public string? Nota { get; set; }
    public int Estatus { get; set; }
}
