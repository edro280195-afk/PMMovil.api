namespace PMMovil.Api.Attributes;

/// <summary>
/// Identificadores reservados para los módulos propios de PMMovil.
/// Deben existir con los mismos valores en pmmtraf.modulos antes de asignar
/// permisos explícitos a personal ajeno a Sistemas.
/// </summary>
public static class MobileModuleIds
{
    /// <summary>
    /// Operación general de caseta: tablero, entradas, pre-registro y edición.
    ///
    /// Reutiliza a propósito el módulo histórico 12 "Caseta" en vez de crear
    /// uno nuevo en el rango móvil: ya existe en el catálogo, está activo y
    /// significa exactamente lo mismo. Así la caseta queda separada de las
    /// operaciones de bodega, que antes compartían
    /// <see cref="WarehouseOperations"/> y hacían imposible dar acceso a una
    /// sin dar acceso a la otra.
    /// </summary>
    public const int Gate = 12;

    /// <summary>
    /// Salida de una relación autorizada. Es la migración de SalidaCaseta.aspx:
    /// cambia la relación a SALIDA, registra la salida de caseta y notifica.
    /// </summary>
    public const int GateRelationExit = 165;

    /// <summary>
    /// Salida física de una caja que aún aparece dentro del patio. Solo
    /// actualiza el registro de caseta y no sustituye la salida de relación.
    /// </summary>
    public const int GatePhysicalExit = 166;

    public const int WarehouseExit = 151;
    /// <summary>Relaciones de carga pendientes de supervisión/autorización.</summary>
    public const int WarehouseRelation = 167;
    public const int Providers = 152;
    public const int WarehouseOperations = 153;
    public const int PreInspection = 154;
    public const int CartaPorte = 155;
    public const int Imex = 156;
    public const int Inventory = 158;
    public const int Review = 159;
    public const int SystemsMonitor = 160;
    public const int SystemsAnalytics = 161;
    public const int Reports = 162;

    /// <summary>
    /// Administracion de cuentas del portal de pre-registro de transportistas
    /// (alta/baja, reseteo de contrasena, auditoria). Solo para personal de
    /// trafico/sistemas; no tiene relacion con las cuentas de las lineas
    /// transportistas en si (esas viven en preregistro_cuentas y usan un JWT
    /// separado, con audiencia propia, que nunca sirve para este ni ningun
    /// otro modulo interno).
    ///
    /// Alta en pmmtraf.modulos: scripts/04-registrar-modulo-preregistro-163.sql.
    /// </summary>
    public const int TransporterPreRegistration = 163;

    /// <summary>
    /// Validación por dirección de los reportes de revisión física (PDF)
    /// antes de que puedan salir a un cliente. No confundir con
    /// <see cref="Reports"/> (162), que es el catálogo de reportes de
    /// negocio genéricos -- este módulo es exclusivamente para aprobar o
    /// rechazar el reporte de revisión de una entrada.
    ///
    /// Alta pendiente en pmmtraf.modulos:
    /// scripts/05-registrar-modulo-validacion-reportes-164.sql (sin
    /// ejecutar -- requiere revisión del responsable de BD, igual que 163).
    /// </summary>
    public const int ReportValidation = 164;

    /// <summary>
    /// Monitoreo operativo para coordinadores de tráfico (Nuevo Laredo).
    /// Permite dar seguimiento al avance de los revisadores y a las entradas
    /// ya revisadas sin otorgar permisos de supervisión (sin kill switch, autorizaciones ni edición de metas).
    ///
    /// Alta en pmmtraf.modulos: scripts/11-registrar-modulo-coordinacion-trafico-168.sql.
    /// Permisos: solo con fila propia en accesos (no hereda del módulo 28 "Tráfico");
    /// hoy los coordinadores tatis y amedina, ver
    /// scripts/12-asignar-permisos-coordinacion-trafico-168.sql.
    /// </summary>
    public const int TrafficCoordinationMonitor = 168;

    /// <summary>
    /// Panel web de entradas de bodega: lista con filtros, detalle, visor de fotos, alta y baja de fotos.
    /// Lectura = ver y exportar, Agregar = subir fotos (y recibir el aviso de entrada capturada),
    /// Borrar = eliminar fotos. Sin herencia: solo Sistemas y filas propias en accesos.
    ///
    /// Alta en pmmtraf.modulos: Database/Migrations/20261120_registrar_modulo_entradas_panel_172.sql.
    /// </summary>
    public const int EntriesPanel = 172;
}
