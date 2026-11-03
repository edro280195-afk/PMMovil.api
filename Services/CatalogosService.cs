using System.Data;
using System.Security.Cryptography;
using System.Text;

using Dapper;

using Microsoft.Extensions.Caching.Memory;

using PMMovil.Api.Data;

namespace PMMovil.Api.Services;

/// <summary>Un renglón de un catálogo maestro. <c>Extra</c> guarda el dato que un módulo necesita además del nombre.</summary>
public sealed class CatalogoMaestroItem
{
    public string Id { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string Extra { get; set; } = string.Empty;
}

/// <summary>Un catálogo completo con su versión: la tableta lo guarda y solo lo vuelve a bajar cuando la versión cambia.</summary>
public sealed class CatalogoMaestro
{
    public string Nombre { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<CatalogoMaestroItem> Items { get; set; } = [];
}

/// <summary>
/// Catálogos que casi no cambian (clientes, líneas de transporte, tipos de caja, países, unidades…) y que la app usaba
/// pidiendo la lista completa en CADA pantalla. Aquí cada uno tiene un nombre y una versión (una huella de su contenido):
/// la tableta pregunta las versiones (unos cientos de bytes), baja solo los que cambiaron y busca en su base local.
/// Una sola definición por catálogo sirve a todos los módulos (Caseta, Entrada de bodega, Revisión).
/// </summary>
public sealed class CatalogosService
{
    /// <summary>Cuánto se reutiliza una lectura de la base: con varias tabletas preguntando, la base se consulta una vez.</summary>
    private static readonly TimeSpan VigenciaCache = TimeSpan.FromSeconds(30);

    public const string Clientes = "clientes";
    public const string Lineas = "lineas";
    public const string TiposCaja = "tipos_caja";
    public const string BodegasCaseta = "bodegas_caseta";
    public const string PaisesCaseta = "paises_caseta";
    public const string Paises = "paises";
    public const string UnidadesComerciales = "unidades_comerciales";
    public const string EntradaBodegas = "entrada_bodegas";
    public const string EntradaHazmat = "entrada_hazmat";
    public const string EntradaManejos = "entrada_manejos";
    public const string EntradaRecibidores = "entrada_recibidores";
    public const string EntradaUbicaciones = "entrada_ubicaciones";
    public const string EntradaClases = "entrada_clases";

    /// <summary>Marca en <c>Extra</c> de un cliente que pertenece a los que ve Caseta (el resto solo lo ve Entrada de bodega).</summary>
    public const string ClienteDeCaseta = "C";

    private readonly BaseRepository _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CatalogosService> _logger;

    private readonly Dictionary<string, Func<Task<List<CatalogoMaestroItem>>>> _fuentes;

    public CatalogosService(BaseRepository db, IMemoryCache cache, ILogger<CatalogosService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
        _fuentes = new Dictionary<string, Func<Task<List<CatalogoMaestroItem>>>>(StringComparer.OrdinalIgnoreCase)
        {
            [Clientes] = LeerClientesAsync,
            [Lineas] = () => LeerAsync(@"
SELECT cidtranspor AS Id, cnombre AS Nombre, '' AS Extra
FROM transpor
WHERE Inactivo = 0
ORDER BY cnombre, cidtranspor"),
            [TiposCaja] = () => LeerAsync(@"
SELECT cidcamion AS Id, descripcionCaseta AS Nombre, '' AS Extra
FROM camiones
WHERE descripcionCaseta != ''
ORDER BY ordenDescripcion ASC, cidcamion"),
            [BodegasCaseta] = () => Task.FromResult(BodegaCatalogo.Todas
                .Select(b => new CatalogoMaestroItem { Id = b.Id, Nombre = b.Nombre }).ToList()),
            [PaisesCaseta] = () => Task.FromResult(CasetaService.ObtenerPaisesEntrada()
                .Select(p => new CatalogoMaestroItem { Id = p.Id, Nombre = p.Nombre }).ToList()),
            [Paises] = () => LeerAsync(@"
SELECT Codigo AS Id, Descrip AS Nombre, '' AS Extra
FROM view_catalogo3
WHERE UPPER(Tabla) = 'PAISES'
ORDER BY Descrip, Codigo"),
            [UnidadesComerciales] = () => LeerAsync(@"
SELECT TRIM(cidunidad) AS Id, TRIM(COALESCE(cnombre, cidunidad)) AS Nombre, TRIM(umcint) AS Extra
FROM unidades
WHERE inactivo = 0
  AND sistema IN ('M3', 'AMBOS')
  AND TRIM(COALESCE(cidunidad, '')) <> ''
  AND TRIM(COALESCE(umcint, '')) <> ''
ORDER BY cnombre, cidunidad"),
            [EntradaBodegas] = () => Task.FromResult(new List<CatalogoMaestroItem>
            {
                new() { Id = "1", Nombre = "MAQUILA" },
                new() { Id = "2", Nombre = "ATLANTA" },
                new() { Id = "3", Nombre = "BODEGA 3" },
                new() { Id = "4", Nombre = "TRADE CTR" }
            }),
            [EntradaHazmat] = () => LeerAsync(@"
SELECT clase_hazmat AS Id, CONCAT(clase_hazmat, ' - ', cdescribe) AS Nombre, '' AS Extra
FROM clase_hazmat
WHERE inactivo = 0
ORDER BY clase_hazmat"),
            [EntradaManejos] = () => LeerAsync(@"
SELECT cidmanejo AS Id, cdescmanejo AS Nombre, '' AS Extra
FROM manejo
WHERE inactivo = 0
ORDER BY cidmanejo"),
            [EntradaRecibidores] = () => LeerAsync(@"
SELECT claveusuario AS Id, CONCAT(TRIM(nombre), ' ', TRIM(apaterno)) AS Nombre, '' AS Extra
FROM usuarios
WHERE recibe_ent = 1 AND inactivo = 0
ORDER BY claveusuario"),
            [EntradaUbicaciones] = () => LeerAsync(@"
SELECT cidubica AS Id, cdescribe AS Nombre, '' AS Extra
FROM ubicas
ORDER BY cidubica"),
            [EntradaClases] = () => LeerAsync(@"
SELECT cclaseid AS Id, cdescribe AS Nombre, '' AS Extra
FROM clases
WHERE inactivo = 0
ORDER BY cclaseid")
        };
    }

    public IReadOnlyCollection<string> Nombres => _fuentes.Keys;

    /// <summary>Nombre -> versión de cada catálogo. Es lo único que la tableta pide en cada apertura.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ObtenerVersionesAsync()
    {
        var versiones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string nombre in _fuentes.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            CatalogoMaestro? catalogo = await ObtenerUnoAsync(nombre);
            // Un catálogo que no se pudo leer simplemente no aparece: la tableta conserva el que ya tiene.
            if (catalogo != null) versiones[nombre] = catalogo.Version;
        }

        return versiones;
    }

    /// <summary>Los catálogos pedidos, completos y con su versión. Los nombres que no existen se ignoran.</summary>
    public async Task<List<CatalogoMaestro>> ObtenerAsync(IEnumerable<string> nombres)
    {
        var resultado = new List<CatalogoMaestro>();
        foreach (string nombre in nombres.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            CatalogoMaestro? catalogo = await ObtenerUnoAsync(nombre);
            if (catalogo != null) resultado.Add(catalogo);
        }

        return resultado;
    }

    private async Task<CatalogoMaestro?> ObtenerUnoAsync(string nombre)
    {
        if (!_fuentes.TryGetValue(nombre, out var fuente)) return null;

        string clave = "catalogo-maestro:" + nombre.ToLowerInvariant();
        if (_cache.TryGetValue(clave, out CatalogoMaestro? enCache) && enCache != null) return enCache;

        try
        {
            List<CatalogoMaestroItem> items = await fuente();
            var catalogo = new CatalogoMaestro
            {
                Nombre = nombre.ToLowerInvariant(),
                Version = CalcularVersion(items),
                Items = items
            };
            _cache.Set(clave, catalogo, VigenciaCache);
            return catalogo;
        }
        catch (Exception ex)
        {
            // Un catálogo roto no debe tirar a los demás: se registra y esa tableta se queda con el que ya tenía.
            _logger.LogError(ex, "Catalogos: no se pudo leer el catalogo {Nombre}.", nombre);
            return null;
        }
    }

    private async Task<List<CatalogoMaestroItem>> LeerAsync(string sql)
    {
        using IDbConnection conn = _db.CreateConnection();
        var filas = await conn.QueryAsync<CatalogoMaestroItem>(sql);
        return filas.Select(Limpiar).ToList();
    }

    // Los clientes se arman en C# a partir de dos consultas sencillas, sin cruzar la vista con la tabla en SQL (las
    // colaciones distintas de una y otra pueden hacer fallar un JOIN en el servidor 30).
    private async Task<List<CatalogoMaestroItem>> LeerClientesAsync()
    {
        using IDbConnection conn = _db.CreateConnection();
        var activos = await conn.QueryAsync<ClienteFila>(
            "SELECT cidcte AS Id, cnombre AS Nombre, tipomv AS TipoMv FROM clientes WHERE inactivo = '0'");
        var deLaVista = await conn.QueryAsync<ClienteFila>(
            "SELECT codigo AS Id, Nombre AS Nombre, '' AS TipoMv FROM view_catalogo2 WHERE catalogo = 'CLIENTES' AND inactivo = '0'");
        return CombinarClientes(
            activos.Select(c => (c.Id ?? string.Empty, c.Nombre ?? string.Empty, c.TipoMv ?? string.Empty)),
            deLaVista.Select(c => (c.Id ?? string.Empty, c.Nombre ?? string.Empty)));
    }

    private sealed class ClienteFila
    {
        public string? Id { get; set; }
        public string? Nombre { get; set; }
        public string? TipoMv { get; set; }
    }

    /// <summary>
    /// Todos los clientes activos, y a los que ve Caseta (los de la vista de catálogo más los de tipo OPERACION, igual que
    /// antes) se les pone la marca <see cref="ClienteDeCaseta"/>. Un cliente que solo está en la vista se agrega también.
    /// </summary>
    public static List<CatalogoMaestroItem> CombinarClientes(
        IEnumerable<(string Id, string Nombre, string TipoMv)> activos,
        IEnumerable<(string Id, string Nombre)> deLaVista)
    {
        var porId = new Dictionary<string, CatalogoMaestroItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, nombre, tipoMv) in activos)
        {
            if (string.IsNullOrWhiteSpace(id) || porId.ContainsKey(id)) continue;
            porId[id] = new CatalogoMaestroItem
            {
                Id = id,
                Nombre = nombre,
                Extra = string.Equals((tipoMv ?? string.Empty).Trim(), "OPERACION", StringComparison.OrdinalIgnoreCase)
                    ? ClienteDeCaseta
                    : string.Empty
            };
        }

        foreach (var (id, nombre) in deLaVista)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (porId.TryGetValue(id, out CatalogoMaestroItem? existente))
            {
                existente.Extra = ClienteDeCaseta;
            }
            else
            {
                porId[id] = new CatalogoMaestroItem { Id = id, Nombre = nombre, Extra = ClienteDeCaseta };
            }
        }

        return porId.Values
            .OrderBy(c => c.Nombre, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Huella del contenido: cambia si cambia un nombre, un id, la marca extra, el orden o la cantidad de renglones, y es la
    /// misma en cualquier servidor. La tableta solo la compara, nunca la interpreta.
    /// </summary>
    public static string CalcularVersion(IReadOnlyList<CatalogoMaestroItem> items)
    {
        var sb = new StringBuilder(items.Count * 48);
        foreach (CatalogoMaestroItem item in items)
        {
            sb.Append(item.Id).Append('\u001f').Append(item.Nombre).Append('\u001f').Append(item.Extra).Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return items.Count + "-" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    private static CatalogoMaestroItem Limpiar(CatalogoMaestroItem item) => new()
    {
        Id = item.Id ?? string.Empty,
        Nombre = item.Nombre ?? string.Empty,
        Extra = item.Extra ?? string.Empty
    };
}
