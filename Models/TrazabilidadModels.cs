using System.Collections.Generic;

namespace PMMovil.Api.Models;

/// <summary>Cuerpo de <c>POST api/Trazabilidad/resolver</c>: el código tal como lo leyó el escáner.</summary>
public sealed class TraceRequest
{
    public string? Code { get; set; }
}

/// <summary>Qué es el código leído, según su forma. Es lo que decide por dónde se recorre la cadena.</summary>
public static class TraceKind
{
    public const string Caja = "CAJA";
    public const string Bulto = "BULTO";
    public const string Carpeta = "CARPETA";
    public const string Relacion = "RELACION";
    public const string PreRegistro = "PREREGISTRO";
}

/// <summary>Las cinco estaciones de La Línea, en el orden en que se recorren.</summary>
public static class TraceStationId
{
    public const string Caseta = "CASETA";
    public const string Entrada = "ENTRADA";
    public const string Revision = "REVISION";
    public const string Relacion = "RELACION";
    public const string Salida = "SALIDA";

    public static readonly string[] Order = { Caseta, Entrada, Revision, Relacion, Salida };
}

/// <summary>Estado de una estación. <c>SIN_DATO</c> significa "no se puede saber", no "todavía no".</summary>
public static class TraceState
{
    public const string Hecha = "HECHA";
    public const string EnCurso = "EN_CURSO";
    public const string Pendiente = "PENDIENTE";
    public const string Atencion = "ATENCION";
    public const string SinDato = "SIN_DATO";
    public const string NoAplica = "NO_APLICA";
}

/// <summary>Señal global del código, para el encabezado de la ficha y las marcas del AR.</summary>
public static class TraceSignal
{
    public const string Alto = "ALTO";
    public const string Atencion = "ATENCION";
    public const string Listo = "LISTO";
    public const string Aviso = "AVISO";
    public const string Cerrado = "CERRADO";
    public const string SinDato = "SIN_DATO";
}

/// <summary>Banderas de la ficha. El servidor da códigos; la app pone las frases.</summary>
public static class TraceFlag
{
    public const string SinEntradaVinculada = "SIN_ENTRADA_VINCULADA";
    public const string EntradaPorRelacion = "ENTRADA_POR_RELACION";
    public const string SinRelacion = "SIN_RELACION";
    public const string Danada = "DANADA";
    public const string Hazmat = "HAZMAT";
    public const string Discrepancia = "DISCREPANCIA";
    public const string ListaRecortada = "LISTA_RECORTADA";
    public const string Cancelada = "CANCELADA";
    public const string RelacionOcupada = "RELACION_OCUPADA";
    public const string RegistroAbierto = "REGISTRO_ABIERTO";
    public const string EtiquetaNoRegistrada = "ETIQUETA_NO_REGISTRADA";
}

/// <summary>
/// Acciones que el estado del código permite, con los mismos nombres que <c>ScanActionId</c> en la app.
/// La primera de la lista es la sugerida como principal. Que el usuario pueda hacerlas lo decide la app con sus permisos.
/// </summary>
public static class TraceAction
{
    public const string CreateEntry = "CREATE_ENTRY";
    public const string ExitGate = "EXIT_GATE";
    public const string FindInYard = "FIND_IN_YARD";
    public const string OpenRelation = "OPEN_RELATION";
    public const string OpenEntry = "OPEN_ENTRY";
    public const string ReviewEntry = "REVIEW_ENTRY";
    public const string ShowOnMap = "SHOW_ON_MAP";
    public const string RegisterPreRegistro = "REGISTER_PREREGISTRO";
}

/// <summary>Por qué un código no produjo ficha.</summary>
public static class TraceReason
{
    public const string Empty = "VACIO";
    public const string Unsupported = "CODIGO_NO_SOPORTADO";
    public const string BadgeNotLookedUp = "GAFETE_NO_SE_CONSULTA";
    public const string NotFound = "NO_ENCONTRADO";
}

public sealed class TraceStation
{
    public string Id { get; set; } = string.Empty;
    public string State { get; set; } = TraceState.SinDato;
    public string? At { get; set; }
}

public sealed class TraceCaja
{
    public string CasetaId { get; set; } = string.Empty;
    public string NumCaja { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string Linea { get; set; } = string.Empty;
    public string Planta { get; set; } = string.Empty;
    public string TipoOp { get; set; } = string.Empty;
    public string Estatus { get; set; } = string.Empty;
    public string Relacion { get; set; } = string.Empty;
    public string? ArrivalAt { get; set; }
    public string? DepartureAt { get; set; }
}

public sealed class TraceEntrada
{
    public string Reference { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string? ArrivalAt { get; set; }

    /// <summary>De dónde salió la entrada: <c>DIRECTA</c> (traf3.ccasetaid), <c>RELACION</c> o <c>CODIGO</c> (el código leído).</summary>
    public string Via { get; set; } = "CODIGO";

    public string ReviewStatus { get; set; } = "SIN_REVISAR";
    public int TotalInvoices { get; set; }
    public int CompletedInvoices { get; set; }
    public int TotalLines { get; set; }
    public int ReviewedLines { get; set; }
    public bool HasHazmat { get; set; }
    public bool HasDamage { get; set; }
}

public sealed class TraceRelacion
{
    public string Relno { get; set; } = string.Empty;
    public string EstatusCarga { get; set; } = string.Empty;
    public string Caja { get; set; } = string.Empty;
    public string Transfer { get; set; } = string.Empty;
    public string? AuthorizedAt { get; set; }
    public string? DepartureAt { get; set; }
    public int TotalEntradas { get; set; }
}

public sealed class TraceBulto
{
    public int Ordinal { get; set; }
    public int Total { get; set; }

    /// <summary>La etiqueta existe en la base con esa posición exacta.</summary>
    public bool Registered { get; set; }

    public string LabelStatus { get; set; } = string.Empty;
    public string Relacion { get; set; } = string.Empty;
}

public sealed class TracePreregistro
{
    public string Folio { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Linea { get; set; } = string.Empty;
    public string Unidad { get; set; } = string.Empty;
    public string? ExpiresAt { get; set; }
    public string? UsedAt { get; set; }
    public string CasetaId { get; set; } = string.Empty;
}

/// <summary>
/// La ficha de un código: qué es, en qué punto de La Línea va y qué se puede hacer. Solo datos y códigos de estado.
/// </summary>
public sealed class TraceResult
{
    public bool Success { get; set; }

    /// <summary>Qué clase de código era; vacío si no se pudo reconocer.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Código normalizado. Vacío en el gafete: nunca se devuelve ni se guarda.</summary>
    public string Code { get; set; } = string.Empty;

    public bool Found { get; set; }
    public string? Reason { get; set; }
    public string Signal { get; set; } = TraceSignal.SinDato;
    public List<TraceStation> Stations { get; set; } = new();
    public List<string> Flags { get; set; } = new();
    public List<string> Actions { get; set; } = new();
    public TraceCaja? Caja { get; set; }
    public List<TraceEntrada> Entradas { get; set; } = new();
    public TraceRelacion? Relacion { get; set; }
    public TraceBulto? Bulto { get; set; }
    public TracePreregistro? Preregistro { get; set; }
}
