using Dapper;

using Microsoft.Extensions.Caching.Memory;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Catálogo operativo de destinos del pre-registro. La tabla es la fuente
/// principal; la configuración vieja queda como respaldo durante la transición
/// de la base de datos para que un despliegue parcial no deje sin ETA al portal.
/// </summary>
public sealed class BodegaDestinoService
{
    private const string CachePrefix = "preregistro:bodegas:";
    private readonly BaseRepository _db;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BodegaDestinoService> _logger;

    public BodegaDestinoService(
        BaseRepository db,
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger<BodegaDestinoService> logger)
    {
        _db = db;
        _configuration = configuration;
        _cache = cache;
        _logger = logger;
    }

    public async Task<List<PreregistroBodegaItem>> ListarAsync(
        bool soloActivas = true,
        CancellationToken ct = default)
    {
        string cacheKey = CachePrefix + (soloActivas ? "activas" : "todas");
        if (_cache.TryGetValue(cacheKey, out List<PreregistroBodegaItem>? cached) && cached is not null)
            return cached.Select(Copiar).ToList();

        try
        {
            using var conn = _db.CreatePmmutileriaConnection();
            var rows = await conn.QueryAsync<PreregistroBodegaItem>(new CommandDefinition(@"
                SELECT id_bodega AS IdBodega, nombre AS Nombre, direccion AS Direccion,
                       lat AS Lat, lon AS Lon,
                       COALESCE(geocerca_lat, lat) AS GeocercaLat,
                       COALESCE(geocerca_lon, lon) AS GeocercaLon,
                       geocerca_metros AS GeocercaMetros,
                       google_place_id AS GooglePlaceId,
                       referencia_entrada AS ReferenciaEntrada,
                       activo AS Activo, fmodi AS FechaModificacion,
                       usumodi AS UsuarioModificacion
                FROM preregistro_bodegas
                WHERE (@soloActivas = 0 OR activo = 1)
                ORDER BY nombre, id_bodega",
                new { soloActivas = soloActivas ? 1 : 0 }, cancellationToken: ct));

            var result = rows.ToList();
            _cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
            return result.Select(Copiar).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo consultar preregistro_bodegas; se usará el catálogo de configuración.");
            var fallback = DesdeConfiguracion();
            return soloActivas ? fallback.Where(x => x.Activo).ToList() : fallback;
        }
    }

    public async Task<PreregistroBodegaItem?> ObtenerAsync(string? idBodega, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idBodega))
            return null;

        string id = idBodega.Trim();
        var bodega = (await ListarAsync(false, ct))
            .FirstOrDefault(x => string.Equals(x.IdBodega, id, StringComparison.OrdinalIgnoreCase));

        return bodega is null ? null : Copiar(bodega);
    }

    public async Task<PreregistroBodegaItem> GuardarAsync(
        PreregistroBodegaGuardarRequest request,
        string usuario,
        CancellationToken ct = default)
    {
        string id = request.IdBodega.Trim();
        string nombre = request.Nombre.Trim();
        double geocercaLat = request.GeocercaLat ?? request.Lat;
        double geocercaLon = request.GeocercaLon ?? request.Lon;

        if (id.Length == 0 || nombre.Length == 0)
            throw new ArgumentException("El código y nombre de la bodega son obligatorios.");
        if (!EsCoordenadaValida(request.Lat, request.Lon)
            || !EsCoordenadaValida(geocercaLat, geocercaLon))
            throw new ArgumentException("Las coordenadas no son válidas.");

        using var conn = _db.CreatePmmutileriaConnection();
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO preregistro_bodegas
                (id_bodega, nombre, direccion, lat, lon, geocerca_lat, geocerca_lon,
                 geocerca_metros, google_place_id, referencia_entrada, activo, fmodi, usumodi)
            VALUES
                (@id, @nombre, @direccion, @lat, @lon, @geocercaLat, @geocercaLon,
                 @geocercaMetros, @placeId, @referencia, @activo, NOW(), @usuario)
            ON DUPLICATE KEY UPDATE
                nombre = VALUES(nombre), direccion = VALUES(direccion),
                lat = VALUES(lat), lon = VALUES(lon),
                geocerca_lat = VALUES(geocerca_lat), geocerca_lon = VALUES(geocerca_lon),
                geocerca_metros = VALUES(geocerca_metros),
                google_place_id = VALUES(google_place_id),
                referencia_entrada = VALUES(referencia_entrada),
                activo = VALUES(activo), fmodi = NOW(), usumodi = VALUES(usumodi)",
            new
            {
                id,
                nombre,
                direccion = string.IsNullOrWhiteSpace(request.Direccion) ? null : request.Direccion.Trim(),
                lat = request.Lat,
                lon = request.Lon,
                geocercaLat,
                geocercaLon,
                geocercaMetros = request.GeocercaMetros,
                placeId = string.IsNullOrWhiteSpace(request.GooglePlaceId) ? null : request.GooglePlaceId.Trim(),
                referencia = string.IsNullOrWhiteSpace(request.ReferenciaEntrada) ? null : request.ReferenciaEntrada.Trim(),
                activo = request.Activo ? 1 : 0,
                usuario = string.IsNullOrWhiteSpace(usuario) ? "sistema" : usuario.Trim()
            }, cancellationToken: ct));

        InvalidarCache();
        return (await ObtenerAsync(id, ct))
            ?? throw new InvalidOperationException("La bodega se guardó, pero no pudo volver a consultarse.");
    }

    private List<PreregistroBodegaItem> DesdeConfiguracion()
    {
        var nombres = BodegaCatalogo.Todas.ToDictionary(x => x.Id, x => x.Nombre, StringComparer.OrdinalIgnoreCase);
        var result = new List<PreregistroBodegaItem>();
        foreach (var section in _configuration.GetSection("GoogleRoutes:Bodegas").GetChildren())
        {
            double? lat = section.GetValue<double?>("Lat");
            double? lon = section.GetValue<double?>("Lon");
            if (!lat.HasValue || !lon.HasValue || !EsCoordenadaValida(lat.Value, lon.Value))
                continue;

            result.Add(new PreregistroBodegaItem
            {
                IdBodega = section.Key,
                Nombre = nombres.TryGetValue(section.Key, out string? nombre) ? nombre : section.Key,
                Lat = lat.Value,
                Lon = lon.Value,
                GeocercaLat = lat.Value,
                GeocercaLon = lon.Value,
                GeocercaMetros = _configuration.GetValue<int?>("GoogleRoutes:GeocercaMetros") ?? 800,
                Activo = true
            });
        }

        return result;
    }

    private void InvalidarCache()
    {
        _cache.Remove(CachePrefix + "activas");
        _cache.Remove(CachePrefix + "todas");
    }

    private static bool EsCoordenadaValida(double lat, double lon) =>
        lat is >= -90 and <= 90
        && lon is >= -180 and <= 180
        && !double.IsNaN(lat) && !double.IsNaN(lon)
        && !double.IsInfinity(lat) && !double.IsInfinity(lon);

    private static PreregistroBodegaItem Copiar(PreregistroBodegaItem item) => new()
    {
        IdBodega = item.IdBodega,
        Nombre = item.Nombre,
        Direccion = item.Direccion,
        Lat = item.Lat,
        Lon = item.Lon,
        GeocercaLat = item.GeocercaLat,
        GeocercaLon = item.GeocercaLon,
        GeocercaMetros = item.GeocercaMetros,
        GooglePlaceId = item.GooglePlaceId,
        ReferenciaEntrada = item.ReferenciaEntrada,
        Activo = item.Activo,
        FechaModificacion = item.FechaModificacion,
        UsuarioModificacion = item.UsuarioModificacion
    };
}
