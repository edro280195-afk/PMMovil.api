namespace PMMovil.Api.Models;

public class SalidaCajaItem
{
    public string CasetaId { get; set; } = string.Empty;
    public string NumCaja { get; set; } = string.Empty; public string LineaCaja { get; set; } = string.Empty; public string NumCamion { get; set; } = string.Empty; public string Placas { get; set; } = string.Empty;
    public string NombreCliente { get; set; } = string.Empty; public string TipoUnidad { get; set; } = string.Empty; public string TipoMovimiento { get; set; } = string.Empty; public string TipoOp { get; set; } = string.Empty; public string Planta { get; set; } = string.Empty;
    public DateTime? FechaArribo { get; set; }
    public int DiasEnPatio { get; set; }
}

public class SalidaCajaPendientesResult
{
    public bool Success { get; set; }
    public List<SalidaCajaItem> Pendientes { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

public class RevisarRelacionResult
{
    public bool Success { get; set; }
    public string Relacion { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class RegistrarSalidaCajaRequest
{
    public string CasetaId { get; set; } = string.Empty;
    public string Observaciones { get; set; } = string.Empty;
}

public class RegistrarSalidaCajaResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

// ── Pantalla unificada de salida de caseta ───────────────────────────────────
// Cada caja del patio cae en UN carril según su relación. El servidor decide el
// carril para que la app nunca tenga que adivinarlo (ni pueda saltarse la regla).

public static class SalidaCarril
{
    /// <summary>Relación AUTORIZADA, libre y sin cruce: sale por confirmación de relación.</summary>
    public const string Lista = "LISTA";
    /// <summary>Relación en espera/cargando/cargada/bloqueada/ocupada: no puede salir todavía.</summary>
    public const string Proceso = "PROCESO";
    /// <summary>Sin relación: salida manual.</summary>
    public const string Manual = "MANUAL";
    /// <summary>La relación ya salió pero la caja sigue en el patio: solo se cierra el registro.</summary>
    public const string Cerrar = "CERRAR";
}

public class SalidaPatioCaja
{
    public string CasetaId { get; set; } = string.Empty;
    public string NumCaja { get; set; } = string.Empty;
    public string LineaCaja { get; set; } = string.Empty;
    public string NumCamion { get; set; } = string.Empty;
    public string Placas { get; set; } = string.Empty;
    public string TipoUnidad { get; set; } = string.Empty;
    public string EstatusCaseta { get; set; } = string.Empty;
    /// <summary>Índices (fotos.nlineno, ntipodocto 106) de las fotos de llegada, para Foto/caseta/ver.</summary>
    public List<int> FotosEntrada { get; set; } = new();
}

public class SalidaPatioItem
{
    /// <summary>relno para relaciones; casetaId para salidas manuales.</summary>
    public string Id { get; set; } = string.Empty;
    public string Carril { get; set; } = SalidaCarril.Manual;
    public string Relno { get; set; } = string.Empty;
    /// <summary>EN ESPERA, CARGANDO, CARGADA, AUTORIZADA, SALIDA, BLOQUEO… (vacío si aún no inicia).</summary>
    public string EstatusCarga { get; set; } = string.Empty;
    /// <summary>Por qué un carril PROCESO no puede salir (texto listo para mostrar).</summary>
    public string Motivo { get; set; } = string.Empty;
    public List<SalidaPatioCaja> Cajas { get; set; } = new();
    public List<string> Clientes { get; set; } = new();
    public string TipoOp { get; set; } = string.Empty;
    public string TipoMovimiento { get; set; } = string.Empty;
    public bool EsTractocamion { get; set; }
    public string Planta { get; set; } = string.Empty;
    public DateTime? FechaArribo { get; set; }
    public int DiasEnPatio { get; set; }
    public int HorasEnPatio { get; set; }

    // Datos de la relación (vacíos en carril MANUAL).
    public string Transfer { get; set; } = string.Empty;
    public string Operador { get; set; } = string.Empty;
    public string Tractor { get; set; } = string.Empty;
    public string Gafete { get; set; } = string.Empty;
    public string Sellos { get; set; } = string.Empty;
    public string CajaRelacion { get; set; } = string.Empty;
    public string UsuAutoriza { get; set; } = string.Empty;
    public string UsuCarga { get; set; } = string.Empty;
    public string UsuEdit { get; set; } = string.Empty;
    public DateTime? AutorizaCarga { get; set; }
    public int? MinutosEspera { get; set; }
    public DateTime? InicioCarga { get; set; }
    public int? MinutosCargando { get; set; }
    public DateTime? SalidaCaseta { get; set; }
    public int BultosCargados { get; set; }
    public int BultosTotal { get; set; }
}

public class SalidaPatioHoy
{
    public int ConRelacion { get; set; }
    public int Manuales { get; set; }
}

public class SalidaPatioResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<SalidaPatioItem> Items { get; set; } = new();
    public SalidaPatioHoy Hoy { get; set; } = new();
}

public class CerrarRegistroRequest
{
    public string Relno { get; set; } = string.Empty;
}
