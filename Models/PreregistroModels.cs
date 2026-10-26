using System.ComponentModel.DataAnnotations;

namespace PMMovil.Api.Models;

/// <summary>
/// Valores posibles de <c>caseta_preregistro.estatus</c>. Texto plano, igual
/// que el resto del esquema legacy (p.ej. <c>caseta.estatus</c>).
/// </summary>
public static class PreregistroEstatus
{
    public const string Vigente = "VIGENTE";
    public const string Usado = "USADO";
    public const string Expirado = "EXPIRADO";
    public const string Cancelado = "CANCELADO";
    public const string NoEncontrado = "NO_ENCONTRADO";
}

/// <summary>Eventos de <c>preregistro_auditoria.evento</c>.</summary>
public static class PreregistroEvento
{
    public const string LoginOk = "LOGIN_OK";
    public const string LoginFallido = "LOGIN_FALLIDO";
    public const string LoginBloqueado = "LOGIN_BLOQUEADO";
    public const string CreaPrereg = "CREA_PREREG";
    public const string CancelaPrereg = "CANCELA_PREREG";
    public const string ValidaQr = "VALIDA_QR";
    public const string UsaQr = "USA_QR";
    /// <summary>Primera senal de ubicacion dentro de la geocerca de la planta: el arribo real.</summary>
    public const string ArriboGeocerca = "ARRIBO_GEOCERCA";
    public const string PlacasNoCoinciden = "PLACAS_NO_COINCIDEN";
    public const string AltaCuenta = "ALTA_CUENTA";
    public const string BajaCuenta = "BAJA_CUENTA";
    public const string ReactivaCuenta = "REACTIVA_CUENTA";
    public const string ResetPass = "RESET_PASS";
    public const string AlertaAtendida = "ALERTA_ATENDIDA";
}

public static class PreregistroAlertaEstado
{
    public const string Pendiente = "PENDIENTE";
    public const string Atendida = "ATENDIDA";
    public const string Resuelta = "RESUELTA";
}

public static class PreregistroAlertaTipo
{
    public const string ArriboGeocerca = "ARRIBO_GEOCERCA";
    public const string EtaCritico = "ETA_CRITICO";
    public const string SenalVencida = "SENAL_VENCIDA";
    public const string SenalSinReporte = "SENAL_SIN_REPORTE";
}

// ── Portal: login ──────────────────────────────────────────────────────────

public class PreregistroLoginRequest
{
    [Required]
    public string Usuario { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

public class PreregistroLoginResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string NombreLinea { get; set; } = string.Empty;
    public DateTime ExpiraUtc { get; set; }
}

// ── Portal: crear pre-registro ───────────────────────────────────────────────

public class PreregistroCrearRequest
{
    [Required, MaxLength(30)]
    public string Nunidad { get; set; } = string.Empty;

    [Required, MaxLength(25)]
    public string Placas { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Operador { get; set; } = string.Empty;

    [Required, MaxLength(5)]
    public string IdBodega { get; set; } = string.Empty;

    /// <summary>Opcional: ubicacion capturada al momento de crear.</summary>
    public double? Lat { get; set; }
    public double? Lon { get; set; }
}

public class PreregistroCrearResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Folio { get; set; } = string.Empty;
    /// <summary>Payload completo a codificar en el QR: <c>PMMPR|folio|token</c>.</summary>
    public string QrPayload { get; set; } = string.Empty;
    public DateTime FechaExpira { get; set; }
    public int? EtaMinutos { get; set; }
    public double? EtaKm { get; set; }
    public string? IdBodega { get; set; }
    public string? NombreBodega { get; set; }
    public string? DireccionBodega { get; set; }
    public string? ReferenciaEntrada { get; set; }
    public double? LatBodega { get; set; }
    public double? LonBodega { get; set; }
    public string? GooglePlaceId { get; set; }
}

// ── Portal: listado y ubicacion ──────────────────────────────────────────────

public class PreregistroItem
{
    public string Folio { get; set; } = string.Empty;
    public string Nunidad { get; set; } = string.Empty;
    public string Placas { get; set; } = string.Empty;
    public string Operador { get; set; } = string.Empty;
    public string NombreLinea { get; set; } = string.Empty;
    public string Estatus { get; set; } = string.Empty;
    public DateTime FechaCrea { get; set; }
    public DateTime FechaExpira { get; set; }
    public int? EtaMinutos { get; set; }
    public double? EtaKm { get; set; }
    public DateTime? FechaUbicacion { get; set; }
    public string? CasetaId { get; set; }
    public DateTime? FechaUso { get; set; }
    public string? QrPayload { get; set; }
    public string? IdBodega { get; set; }
    public string? NombreBodega { get; set; }
    public string? DireccionBodega { get; set; }
    public string? ReferenciaEntrada { get; set; }
    public double? LatBodega { get; set; }
    public double? LonBodega { get; set; }
    public string? GooglePlaceId { get; set; }
}

public class PreregistroAdminItem
{
    public string Folio { get; set; } = string.Empty;
    public string Nunidad { get; set; } = string.Empty;
    public string Placas { get; set; } = string.Empty;
    public string Operador { get; set; } = string.Empty;
    public string NombreLinea { get; set; } = string.Empty;
    public string? IdBodega { get; set; }
    public string Estatus { get; set; } = string.Empty;
    public DateTime FechaCrea { get; set; }
    public DateTime FechaExpira { get; set; }
    public int? EtaMinutos { get; set; }
    public double? EtaKm { get; set; }
    public DateTime? FechaUbicacion { get; set; }
    public DateTime? FechaUso { get; set; }
    public string? CasetaId { get; set; }
    public string? UsuarioUso { get; set; }
}

public class PreregistroUbicacionRequest
{
    [Range(-90, 90)]
    public double Lat { get; set; }

    [Range(-180, 180)]
    public double Lon { get; set; }

    /// <summary>Radio de incertidumbre en metros que reporta el GPS del dispositivo.</summary>
    [Range(0, 10000)]
    public int? PrecisionM { get; set; }

    /// <summary>km/h reportados por el dispositivo. Null si el navegador no los da.</summary>
    [Range(0, 250)]
    public double? Velocidad { get; set; }

    /// <summary>
    /// Momento en que el dispositivo tomo la lectura. Puede venir mas viejo que
    /// "ahora" cuando el portal acumulo senales sin red. Si viene null o con
    /// fecha futura, el API usa su propio reloj.
    /// </summary>
    public DateTime? Fecha { get; set; }

    /// <summary>PORTAL | APP | TELEMATICA. Default PORTAL.</summary>
    [MaxLength(12)]
    public string? Origen { get; set; }
}

public class PreregistroUbicacionResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? EtaMinutos { get; set; }
    public double? EtaKm { get; set; }

    /// <summary>Distancia en linea recta a la bodega destino al momento de esta senal.</summary>
    public double? DistanciaKm { get; set; }

    /// <summary>La senal cayo dentro de la geocerca de la planta.</summary>
    public bool DentroGeocerca { get; set; }

    /// <summary>
    /// true solo en la senal que ENTRA a la geocerca (la anterior estaba fuera).
    /// Es el sello de arribo real; el cliente lo usa para avisar al chofer y el
    /// API para notificar a la garita.
    /// </summary>
    public bool ArriboDetectado { get; set; }
}

/// <summary>Una miga del recorrido, para la pestana "Recorrido" de la ficha del folio.</summary>
public class PreregistroUbicacionItem
{
    public DateTime Fecha { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public int? PrecisionM { get; set; }
    public double? Velocidad { get; set; }
    public string Origen { get; set; } = string.Empty;
    public int? EtaMinutos { get; set; }
    public double? DistanciaKm { get; set; }
    public bool DentroGeocerca { get; set; }
}

// ── Garita ───────────────────────────────────────────────────────────────────

public class PreregistroValidarResponse
{
    public bool Success { get; set; }
    /// <summary>VIGENTE | USADO | EXPIRADO | CANCELADO | NO_ENCONTRADO.</summary>
    public string Estatus { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Folio { get; set; } = string.Empty;
    public string Nunidad { get; set; } = string.Empty;
    public string Placas { get; set; } = string.Empty;
    public string Operador { get; set; } = string.Empty;
    public string IdTransporu { get; set; } = string.Empty;
    public string NombreLinea { get; set; } = string.Empty;
    public string IdBodega { get; set; } = string.Empty;
    public DateTime? FechaExpira { get; set; }
    public DateTime? FechaUso { get; set; }
    public string? UsuarioUso { get; set; }
    public string? CasetaId { get; set; }
}

public class PreregistroUsarRequest
{
    /// <summary>Folio de caseta generado al promover el registro (CasetaEntradaGuardarResult.CasetaId).</summary>
    [Required, MaxLength(80)]
    public string CasetaId { get; set; } = string.Empty;

    /// <summary>El guardia marca explicitamente si coteja placas fisicas y no coinciden (pantalla 5e). No bloquea.</summary>
    public bool PlacasNoCoinciden { get; set; }
}

public class PreregistroUsarResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

// ── Camiones en camino ───────────────────────────────────────────────────────

public class PreregistroEnCaminoItem
{
    public DateTime? FechaArriboGeocerca { get; set; }
    public bool DentroGeocerca { get; set; }
    public double? DistanciaInicialKm { get; set; }
    public string Folio { get; set; } = string.Empty;
    public string Nunidad { get; set; } = string.Empty;
    public string Placas { get; set; } = string.Empty;
    public string NombreLinea { get; set; } = string.Empty;
    public string Operador { get; set; } = string.Empty;
    public string IdBodega { get; set; } = string.Empty;
    public int? EtaMinutos { get; set; }
    public double? EtaKm { get; set; }
    public DateTime? FechaUbicacion { get; set; }

    /// <summary>
    /// Última ubicación reportada por el chofer (portal), la misma que
    /// alimenta el ETA. Null si nunca reportó ubicación -- el mapa lo debe
    /// mostrar en la lista pero no como punto.
    /// </summary>
    public double? Lat { get; set; }
    public double? Lon { get; set; }
}

/// <summary>Coordenadas de una bodega/planta, para centrar el mapa de "Camiones en camino".</summary>
public class BodegaCoordenadasResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public double? Lat { get; set; }
    public double? Lon { get; set; }

    /// <summary>Coordenada independiente para evaluar la geocerca.</summary>
    public double? GeocercaLat { get; set; }
    public double? GeocercaLon { get; set; }

    public string? IdBodega { get; set; }
    public string? Nombre { get; set; }
    public string? Direccion { get; set; }
    public string? ReferenciaEntrada { get; set; }
    public string? GooglePlaceId { get; set; }

    /// <summary>Radio de la geocerca en metros, para dibujarla igual que como se evalua.</summary>
    public int GeocercaMetros { get; set; }
}

/// <summary>Destino operativo administrable desde el panel de pre-registro.</summary>
public class PreregistroBodegaItem
{
    public string IdBodega { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string? Direccion { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double GeocercaLat { get; set; }
    public double GeocercaLon { get; set; }
    public int GeocercaMetros { get; set; }
    public string? GooglePlaceId { get; set; }
    public string? ReferenciaEntrada { get; set; }
    public bool Activo { get; set; }
    public DateTime? FechaModificacion { get; set; }
    public string? UsuarioModificacion { get; set; }
}

public class PreregistroBodegaGuardarRequest
{
    [Required, MaxLength(5)]
    public string IdBodega { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Nombre { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Direccion { get; set; }

    [Range(-90, 90)]
    public double Lat { get; set; }

    [Range(-180, 180)]
    public double Lon { get; set; }

    [Range(-90, 90)]
    public double? GeocercaLat { get; set; }

    [Range(-180, 180)]
    public double? GeocercaLon { get; set; }

    [Range(50, 5000)]
    public int GeocercaMetros { get; set; } = 800;

    [MaxLength(255)]
    public string? GooglePlaceId { get; set; }

    [MaxLength(500)]
    public string? ReferenciaEntrada { get; set; }

    public bool Activo { get; set; } = true;
}

// ── Administracion de cuentas (Fase 7) ───────────────────────────────────────

public class PreregistroCuentaListItem
{
    public int Id { get; set; }
    public string Usuario { get; set; } = string.Empty;
    public string CidTranspor { get; set; } = string.Empty;
    public string NombreLinea { get; set; } = string.Empty;
    public string? Contacto { get; set; }
    public bool Activo { get; set; }
    public DateTime? FUltimoAcceso { get; set; }
    public DateTime? FBloqueo { get; set; }
    public int Vigentes { get; set; }
}

public class PreregistroCuentaCrearRequest
{
    [MaxLength(30)]
    public string Usuario { get; set; } = string.Empty;

    [Required, MaxLength(10)]
    public string CidTranspor { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string NombreLinea { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Contacto { get; set; }
}

public class PreregistroCuentaCrearResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int Id { get; set; }
    /// <summary>Usuario generado automáticamente cuando no se captura uno.</summary>
    public string? Usuario { get; set; }
    /// <summary>Se muestra una sola vez; no se puede volver a consultar despues.</summary>
    public string? PasswordInicial { get; set; }
}

public class PreregistroResetPasswordResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? PasswordNueva { get; set; }
}

public class PreregistroAuditoriaItem
{
    public DateTime Fecha { get; set; }
    public string Evento { get; set; } = string.Empty;
    public string? Usuario { get; set; }
    public string? Folio { get; set; }
    public string? Ip { get; set; }
    public string? Detalle { get; set; }
}

public class PreregistroAlertaItem
{
    public long Id { get; set; }
    public string Tipo { get; set; } = string.Empty;
    public string Prioridad { get; set; } = string.Empty;
    public string Folio { get; set; } = string.Empty;
    public string? IdBodega { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public string Detalle { get; set; } = string.Empty;
    public string Estado { get; set; } = PreregistroAlertaEstado.Pendiente;
    public DateTime FechaCrea { get; set; }
    public DateTime? FechaAtiende { get; set; }
    public string? UsuarioAtiende { get; set; }
}

public class PreregistroAuditoriaFiltro
{
    public int? IdCuenta { get; set; }
    public string? Evento { get; set; }
    public DateTime? Desde { get; set; }
    public DateTime? Hasta { get; set; }
}
