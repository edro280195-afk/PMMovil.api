using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Respaldo mínimo del catálogo de bodegas. La fuente operativa del portal es
/// ahora <c>pmmutileria.preregistro_bodegas</c>; este valor se conserva para
/// que la API siga funcionando durante la transición si todavía no se ejecuta
/// la migración en un ambiente.
/// </summary>
public static class BodegaCatalogo
{
    public static readonly List<CasetaEntradaCatalogItem> Todas =
    [
        new() { Id = "01", Nombre = "Laredo, TX" }
    ];
}
