using System;
using System.Collections.Generic;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Las definiciones de las cinco cifras del Tablero y sus umbrales, sin tocar la base. Cada cifra es el largo de la
/// lista que se abre al tocar su estación (misma consulta), salvo donde se dice lo contrario.
/// </summary>
public static class HomeSummaryRules
{
    /// <summary>Una caja con más días que estos en el patio es excepción. Decidido con Eduardo el 26-oct (siete).</summary>
    public const int OldYardDays = 7;

    /// <summary>
    /// «Mis tomadas»: una revisión mía sin cerrar solo cuenta si tuvo actividad en estos días. «Mis activas» no pone
    /// límite y la mayoría de los revisores arrastra decenas de sesiones pausadas viejas: sin esto la cifra sería diez
    /// para todos, siempre.
    /// </summary>
    public const int TakenWindowDays = 7;

    /// <summary>Segundos que el servidor conserva cada cifra. Los conteos cambian en minutos, no en segundos.</summary>
    public const int CacheSeconds = 30;

    /// <summary>
    /// Estatus de carga de la lista del módulo Relación (<c>SalidaService.GetPendientesAutorizarAsync</c>): lo que un
    /// supervisor tiene por revisar. No incluye CARGANDO ni AUTORIZADA. Si esa lista cambia, esta también.
    /// </summary>
    public static readonly IReadOnlyList<string> SupervisionStatuses = new[]
    {
        "CARGADA", "INCOMPLETA", "DIFERENCIA EN BULTOS-ETIQUETAS", "NO DISP ETIQUETAS EN MCIA",
        "DEMASIADO RAPIDO", "EN ESPERA", "BLOQUEO", "BLOQUEO REIMPRESION"
    };

    /// <summary>De esos estatus, los que no avanzan solos: alguien tiene que resolverlos.</summary>
    public static readonly IReadOnlyList<string> BlockedStatuses = new[]
    {
        "INCOMPLETA", "DIFERENCIA EN BULTOS-ETIQUETAS", "NO DISP ETIQUETAS EN MCIA",
        "DEMASIADO RAPIDO", "BLOQUEO", "BLOQUEO REIMPRESION"
    };

    /// <summary>
    /// Los módulos que dan acceso a cada estación (basta Lectura en cualquiera). Son los mismos que usa la app para
    /// atenuar la estación; el servidor los vuelve a exigir para no calcular lo que el usuario no puede ver.
    /// </summary>
    public static IReadOnlyList<int> ModulesOf(string stationId) => stationId switch
    {
        TraceStationId.Caseta => new[] { MobileModuleIds.Gate },
        TraceStationId.Entrada => new[] { MobileModuleIds.WarehouseOperations },
        TraceStationId.Revision => new[] { MobileModuleIds.Review },
        TraceStationId.Relacion => new[] { MobileModuleIds.WarehouseRelation },
        TraceStationId.Salida => new[] { MobileModuleIds.GateRelationExit, MobileModuleIds.GatePhysicalExit },
        _ => Array.Empty<int>()
    };

    /// <summary>La excepción de una estación; <c>null</c> si no hay ningún caso (nunca se muestra un «0 …»).</summary>
    public static HomeStationException? ExceptionOf(string kind, string code, long count)
        => count > 0 ? new HomeStationException { Kind = kind, Code = code, Count = ToCount(count) } : null;

    public static HomeStationSummary Summary(long count, HomeStationException? exception = null)
        => new() { Count = ToCount(count), Exception = exception };

    /// <summary>Un conteo de la base cabe en un entero; uno negativo (no debería pasar) se lee como cero.</summary>
    public static int ToCount(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
}
