using System;
using System.Collections.Generic;

namespace PMMovil.Api.Models;

/// <summary>Revisador (de la lista oficial) para el buscador del historial.</summary>
public sealed class AuditReviewerDto
{
    public string Usuario { get; init; } = string.Empty;
    public string Nombre { get; init; } = string.Empty;
    public string Area { get; init; } = string.Empty;
    /// <summary>false cuando el usuario ya fue dado de baja; sigue apareciendo para poder auditar su historial.</summary>
    public bool Activo { get; init; }
}

/// <summary>Actividad de un revisor dentro de un rango de días.</summary>
public sealed class AuditUserSummaryDto
{
    public string Usuario { get; init; } = string.Empty;
    public string Nombre { get; init; } = string.Empty;
    public string Area { get; init; } = string.Empty;
    public bool Activo { get; init; } = true;
    /// <summary>Entradas distintas en las que tuvo algún movimiento.</summary>
    public int Entradas { get; init; }
    public int EntradasCerradas { get; init; }
    public int FacturasCompletadas { get; init; }
    public int FacturasDiscrepancia { get; init; }
    public int PartidasRevisadas { get; init; }
    /// <summary>Campos de factura y de partida capturados o corregidos.</summary>
    public int Ediciones { get; init; }
    public int LecturasCodigo { get; init; }
    /// <summary>Pausas que hizo la persona; las automáticas del sistema no cuentan.</summary>
    public int Pausas { get; init; }
    public int Sesiones { get; init; }
    public DateTime? PrimeraActividad { get; init; }
    public DateTime? UltimaActividad { get; init; }
    /// <summary>Suma de sesiones cerradas válidas (misma regla que el Monitor y el Timer).</summary>
    public long SegundosEnRevision { get; init; }
}

public sealed class AuditSummaryDto
{
    public bool Success { get; init; } = true;
    public string Desde { get; init; } = string.Empty;
    public string Hasta { get; init; } = string.Empty;
    public IReadOnlyList<AuditUserSummaryDto> Items { get; init; } = Array.Empty<AuditUserSummaryDto>();
    /// <summary>Totales sin duplicar: una entrada que revisaron dos personas cuenta una sola vez.</summary>
    public AuditUserSummaryDto Totales { get; init; } = new();
}

/// <summary>Lo que un revisor hizo en una entrada durante un día.</summary>
public sealed class AuditEntryDto
{
    public string Creferencia { get; init; } = string.Empty;
    public string NombreCliente { get; init; } = string.Empty;
    public string Usuario { get; init; } = string.Empty;
    /// <summary>Día calendario, yyyy-MM-dd.</summary>
    public string Dia { get; init; } = string.Empty;
    public DateTime Primera { get; init; }
    public DateTime Ultima { get; init; }
    public int FacturasCompletadas { get; init; }
    public int FacturasDiscrepancia { get; init; }
    public int PartidasRevisadas { get; init; }
    public int Ediciones { get; init; }
    public int LecturasCodigo { get; init; }
    public int Pausas { get; init; }
    public int Sesiones { get; init; }
    public bool Finalizada { get; init; }
}

public sealed class AuditEntriesDto
{
    public bool Success { get; init; } = true;
    public IReadOnlyList<AuditEntryDto> Items { get; init; } = Array.Empty<AuditEntryDto>();
}

public sealed class AuditMovementDto
{
    public long Id { get; init; }
    public string Creferencia { get; init; } = string.Empty;
    public string Usuario { get; init; } = string.Empty;
    /// <summary>Nombre de la persona cuando la clave existe en <c>usuarios</c>; vacío para el sistema.</summary>
    public string Nombre { get; init; } = string.Empty;
    /// <summary>true cuando lo hizo el sistema por su cuenta (cierre o pausa automática).</summary>
    public bool EsSistema { get; init; }
    public string Tipo { get; init; } = string.Empty;
    public string? Cnofact { get; init; }
    public int? IdRevdet { get; init; }
    public string? Detalle { get; init; }
    public DateTime Fecha { get; init; }
    public string Origen { get; init; } = string.Empty;
}

public sealed class AuditPaginationDto
{
    public int Limit { get; init; }
    public bool HasMore { get; init; }
    public long? NextCursor { get; init; }
}

public sealed class AuditMovementsDto
{
    public bool Success { get; init; } = true;
    public IReadOnlyList<AuditMovementDto> Items { get; init; } = Array.Empty<AuditMovementDto>();
    public AuditPaginationDto Pagination { get; init; } = new();
}

public sealed class AuditParticipantDto
{
    public string Usuario { get; init; } = string.Empty;
    public string Nombre { get; init; } = string.Empty;
    public bool EsRevisor { get; init; }
    public bool EsSistema { get; init; }
    public DateTime Primera { get; init; }
    public DateTime Ultima { get; init; }
    public int FacturasCompletadas { get; init; }
    public int FacturasDiscrepancia { get; init; }
    public int PartidasRevisadas { get; init; }
    public int Movimientos { get; init; }
}

public sealed class AuditAssignmentDto
{
    public int Id { get; init; }
    public string UsuarioAsignado { get; init; } = string.Empty;
    public string NombreAsignado { get; init; } = string.Empty;
    public string UsuarioAsigna { get; init; } = string.Empty;
    public string NombreAsigna { get; init; } = string.Empty;
    public DateTime FechaAsignacion { get; init; }
    public DateTime? FechaVisto { get; init; }
    public DateTime? FechaCompletada { get; init; }
    public int Prioridad { get; init; }
    public string Nota { get; init; } = string.Empty;
    /// <summary>0 asignada, 1 vista, 2 completada, 3 cancelada.</summary>
    public int Estatus { get; init; }
}

public sealed class AuditUnlockDto
{
    public int Id { get; init; }
    /// <summary>Factura a la que se pidió el permiso; vacío cuando fue para la entrada completa.</summary>
    public string Cnofact { get; init; } = string.Empty;
    public string UsuarioSolicita { get; init; } = string.Empty;
    public string NombreSolicita { get; init; } = string.Empty;
    public string Motivo { get; init; } = string.Empty;
    /// <summary>0 pendiente, 1 autorizado, 2 rechazado (o vencido sin respuesta).</summary>
    public int Estatus { get; init; }
    public string UsuarioAutoriza { get; init; } = string.Empty;
    public string NombreAutoriza { get; init; } = string.Empty;
    /// <summary>true cuando nadie respondió y el sistema lo cerró a las 24 horas.</summary>
    public bool Vencido { get; init; }
    public DateTime? FechaSolicitud { get; init; }
    public DateTime? FechaAutorizacion { get; init; }
    public DateTime? FechaExpiracion { get; init; }
}

public sealed class AuditReferenceDto
{
    public bool Success { get; init; } = true;
    public string Creferencia { get; init; } = string.Empty;
    public string NombreCliente { get; init; } = string.Empty;
    public int TotalFacturas { get; init; }
    public int FacturasCompletas { get; init; }
    public int FacturasDiscrepancia { get; init; }
    /// <summary>EN_REVISION, DOCUMENTOS_TOMADOS, FINALIZADA, PAUSADA o SIN_REVISION.</summary>
    public string Estado { get; init; } = "SIN_REVISION";
    public int Sesiones { get; init; }
    public long SegundosEnRevision { get; init; }
    public string CerradaPor { get; init; } = string.Empty;
    public string CerradaPorNombre { get; init; } = string.Empty;
    public DateTime? CerradaEn { get; init; }
    public DateTime? PrimeraActividad { get; init; }
    public DateTime? UltimaActividad { get; init; }
    public IReadOnlyList<AuditParticipantDto> Participantes { get; init; } = Array.Empty<AuditParticipantDto>();
    public IReadOnlyList<AuditAssignmentDto> Asignaciones { get; init; } = Array.Empty<AuditAssignmentDto>();
    public IReadOnlyList<AuditUnlockDto> Desbloqueos { get; init; } = Array.Empty<AuditUnlockDto>();
}
