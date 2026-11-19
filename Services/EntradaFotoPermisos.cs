using PMMovil.Api.Attributes;

namespace PMMovil.Api.Services;

/// <summary>
/// Reglas de permisos para subir y eliminar fotografías de Entrada Bodega (módulo 153).
/// En el sistema PMM el permiso «Borrar» del módulo 14 equivale a eliminar toda la recepción (<c>traf</c>),
/// por lo que los operadores de bodega normalmente solo tienen «Agregar» y/o «Modificar». Quien puede capturar o
/// editar una entrada también puede agregarle fotos o corregir/eliminar fotos tomadas (mientras que un usuario
/// de solo «Lectura» no puede ni subir ni borrar).
/// </summary>
public static class EntradaFotoPermisos
{
    public static bool PuedeSubir(bool puedeAgregar, bool puedeModificar)
        => puedeAgregar || puedeModificar;

    public static bool PuedeEliminar(bool puedeAgregar, bool puedeModificar, bool puedeBorrar)
        => puedeAgregar || puedeModificar || puedeBorrar;

    public static async Task<bool> PuedeSubirAsync(
        IModulePermissionService permissionService,
        string username,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        bool agregar = await permissionService.HasPermissionAsync(
            username, MobileModuleIds.WarehouseOperations, "Agregar", ct);
        if (agregar) return true;
        bool modificar = await permissionService.HasPermissionAsync(
            username, MobileModuleIds.WarehouseOperations, "Modificar", ct);
        return PuedeSubir(agregar, modificar);
    }

    public static async Task<bool> PuedeEliminarAsync(
        IModulePermissionService permissionService,
        string username,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        bool borrar = await permissionService.HasPermissionAsync(
            username, MobileModuleIds.WarehouseOperations, "Borrar", ct);
        if (borrar) return true;
        bool modificar = await permissionService.HasPermissionAsync(
            username, MobileModuleIds.WarehouseOperations, "Modificar", ct);
        if (modificar) return true;
        bool agregar = await permissionService.HasPermissionAsync(
            username, MobileModuleIds.WarehouseOperations, "Agregar", ct);
        return PuedeEliminar(agregar, modificar, borrar);
    }
}
