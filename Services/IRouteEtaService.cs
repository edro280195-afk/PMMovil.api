namespace PMMovil.Api.Services;

public record RouteEtaResult(int Minutos, double Km);

/// <summary>
/// Abstrae el motor de rutas usado para el ETA del pre-registro. Implementacion
/// actual: <see cref="GoogleRoutesEtaService"/> (Google Routes API, computeRoutes).
/// Nunca debe lanzar hacia el llamador: si el motor falla, se guarda el
/// pre-registro sin ETA. Ver docs/google-routes-api-setup.md.
/// </summary>
public interface IRouteEtaService
{
    /// <summary>
    /// Calcula tiempo y distancia por carretera entre origen y destino.
    /// Devuelve null si el motor no responde o la configuracion (API key,
    /// coordenadas de bodega) no esta lista — nunca lanza.
    /// </summary>
    Task<RouteEtaResult?> ComputeEtaAsync(
        double originLat, double originLon,
        double destLat, double destLon,
        CancellationToken ct = default);

    /// <summary>
    /// Coordenadas de navegación configuradas para una bodega. Se consultan
    /// desde el catálogo operativo y se conserva appsettings como fallback de
    /// transición.
    /// </summary>
    Task<(double Lat, double Lon)?> ObtenerCoordenadasBodegaAsync(
        string idBodega,
        CancellationToken ct = default);
}
