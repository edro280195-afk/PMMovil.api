using PMMovil.Api.Attributes;

namespace PMMovil.Api.Services;

/// <summary>
/// Permisos efectivos sobre un módulo.
/// </summary>
public readonly record struct ModuleAccess(
    bool Lectura,
    bool Agregar,
    bool Modificar,
    bool Borrar)
{
    public static readonly ModuleAccess None = new(false, false, false, false);

    public bool HasAny => Lectura || Agregar || Modificar || Borrar;

    /// <summary>
    /// Une dos permisos quedándose con lo más permisivo de cada acción, que es
    /// el mismo criterio del <c>MAX()</c> que ya usaba la consulta original
    /// cuando un usuario tenía varias filas para el mismo módulo.
    /// </summary>
    public ModuleAccess Merge(ModuleAccess other) => new(
        Lectura || other.Lectura,
        Agregar || other.Agregar,
        Modificar || other.Modificar,
        Borrar || other.Borrar);
}

/// <summary>
/// Puente entre el catálogo de módulos del sistema web (identificadores
/// históricos) y los módulos propios de PMMovil (151-167).
///
/// Los módulos móviles existen en <c>pmmtraf.modulos</c>, pero nunca se
/// poblaron en <c>pmmtraf.accesos</c>: sin este puente, cualquier usuario
/// ajeno al departamento de Sistemas recibe los doce módulos en cero y la
/// aplicación se queda sin ninguna opción visible.
///
/// El sistema web sigue siendo la única fuente de verdad de los permisos.
/// Aquí solo se traduce lo que el usuario ya tiene concedido allí, de forma
/// que no haya dos catálogos que mantener sincronizados a mano.
/// </summary>
public static class LegacyModuleMap
{
    /// <summary>
    /// Módulo móvil -> módulos del sistema web que lo habilitan.
    ///
    /// Tener cualquiera de los módulos de la derecha concede sobre el módulo
    /// móvil la suma de sus permisos. Conceder acceso por aproximación abriría
    /// la aplicación a gente que hoy no debería entrar, así que lo que no
    /// tiene una equivalencia clara no se mapea.
    ///
    /// Deliberadamente fuera de esta tabla:
    /// <list type="bullet">
    ///   <item>Revisión, que se concede por departamento (ver
    ///   <see cref="DepartmentGrants"/>).</item>
    ///   <item>Monitor, Analítica y Reportes de Sistemas, que ya cubre el
    ///   acceso integral del departamento de Sistemas.</item>
    /// </list>
    /// </summary>
    private static readonly Dictionary<int, int[]> Equivalences = new()
    {
        // Entradas de bodega y mapa de ubicaciones. La caseta se resuelve
        // aparte, en MobileModuleIds.Gate: mezclarlas aquí obligaba a dar
        // acceso a las entradas de bodega para poder usar la caseta.
        [MobileModuleIds.WarehouseOperations] =
        [
            14,  // Entrada de Bodega
            100, // Entrada de Bodega (Imex)
            102  // Entrada de Bodega (Imex2)
        ],

        // Operación general de caseta. El propio módulo 12 se toma directo
        // por acceso directo; 17 y 19 mantienen visible el hub para usuarios
        // que solo tienen el catálogo histórico de caseta.
        [MobileModuleIds.Gate] =
        [
            17, // Entrada a Caseta
            19  // Salida de Caseta
        ],

        // SalidaCaseta.aspx: salida de una relación previamente autorizada.
        [MobileModuleIds.GateRelationExit] =
        [
            19 // Salida de Caseta
        ],

        // Salida física de patio: conserva compatibilidad con el módulo
        // histórico 12 "Caseta", pero queda separado en el catálogo móvil.
        [MobileModuleIds.GatePhysicalExit] =
        [
            12 // Caseta
        ],

        // Cargar mercancía: captura y descarga de la relación. La autorización
        // del supervisor vive en WarehouseRelation y no se hereda aquí.
        [MobileModuleIds.WarehouseExit] =
        [
            30, // Relacion de carga
            42, // Relacion de carga Imp
            99  // Relacion de carga Imex
        ],

        // Relación: bandeja y acciones del supervisor.
        [MobileModuleIds.WarehouseRelation] =
        [
            92 // Autorizar Salidas
        ],

        [MobileModuleIds.Providers] =
        [
            9 // Proveedores
        ],

        [MobileModuleIds.PreInspection] =
        [
            121, // Control de Previo
            122  // Bodega previo
        ],

        [MobileModuleIds.CartaPorte] =
        [
            136 // Ubicacion CPorte
        ],

        [MobileModuleIds.Imex] =
        [
            99,  // Relacion de carga Imex
            100, // Entrada de Bodega (Imex)
            102, // Entrada de Bodega (Imex2)
            120  // Iventario IMEX
        ],

        [MobileModuleIds.Inventory] =
        [
            120, // Iventario IMEX
            140, // Inventario de Patio
            142, // Inventario de Sellos
            147  // Inventario
        ]
    };

    /// <summary>Departamento de Revisión en <c>pmmtraf.departamentos</c>.</summary>
    public const int ReviewDepartmentId = 12;

    /// <summary>
    /// Módulos que se conceden por pertenecer a un departamento.
    ///
    /// La revisión de mercancía con escaneo es funcionalidad nueva de la
    /// aplicación móvil: no hay ningún módulo en el catálogo histórico que
    /// signifique lo mismo (los que se le parecen por nombre son de partes de
    /// pedimento, calidad o facturación). Colgarla de "Entrada de Bodega"
    /// habría abierto la revisión a 136 personas, así que se concede por
    /// departamento, igual que ya se hacía con Sistemas.
    ///
    /// Da las cuatro acciones porque los endpoints de revisión usan las
    /// cuatro: leer pendientes, agregar lecturas, modificar y borrar.
    /// </summary>
    private static readonly Dictionary<int, int[]> DepartmentGrants = new()
    {
        [MobileModuleIds.Review] = [ReviewDepartmentId]
    };

    /// <summary>True si el departamento concede el módulo por sí solo.</summary>
    public static bool IsGrantedByDepartment(int mobileModuleId, int departmentId) =>
        DepartmentGrants.TryGetValue(mobileModuleId, out var departments) &&
        Array.IndexOf(departments, departmentId) >= 0;

    /// <summary>Módulos del sistema web que habilitan un módulo móvil.</summary>
    public static IReadOnlyList<int> LegacyIdsFor(int mobileModuleId) =>
        Equivalences.TryGetValue(mobileModuleId, out var ids) ? ids : [];

    /// <summary>True si el módulo tiene equivalencia declarada.</summary>
    public static bool HasEquivalence(int mobileModuleId) =>
        Equivalences.ContainsKey(mobileModuleId);

    /// <summary>
    /// Permisos efectivos sobre <paramref name="moduleId"/> combinando el
    /// acceso directo (si algún día se puebla el catálogo móvil) con el
    /// heredado de los módulos equivalentes del sistema web.
    /// </summary>
    public static ModuleAccess Resolve(
        int moduleId,
        IReadOnlyDictionary<int, ModuleAccess> accessByModule,
        int departmentId)
    {
        // Una fila explícita, incluso con todos sus valores en cero, es una
        // decisión deliberada del administrador y debe sobrescribir la
        // herencia histórica.
        if (accessByModule.TryGetValue(moduleId, out var direct))
            return direct;

        if (IsGrantedByDepartment(moduleId, departmentId))
            return new ModuleAccess(true, true, true, true);

        var effective = ModuleAccess.None;

        foreach (var legacyId in LegacyIdsFor(moduleId))
        {
            if (accessByModule.TryGetValue(legacyId, out var inherited))
                effective = effective.Merge(inherited);
        }

        return effective;
    }
}
