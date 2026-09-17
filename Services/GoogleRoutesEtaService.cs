using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PMMovil.Api.Services;

/// <summary>
/// Implementacion de <see cref="IRouteEtaService"/> contra Google Routes API
/// (endpoint computeRoutes, routingPreference=TRAFFIC_AWARE). Guia de
/// instalacion: docs/google-routes-api-setup.md.
///
/// La API key NUNCA viene de appsettings.json en claro: se lee de la variable
/// de entorno nombrada en GoogleRoutes:ApiKeyEnvVar (por defecto
/// PMMOVIL_GOOGLE_ROUTES_KEY). Si no esta configurada, el servicio queda
/// "apagado" — ComputeEtaAsync devuelve null sin intentar la llamada.
/// </summary>
public class GoogleRoutesEtaService : IRouteEtaService
{
    private const string ComputeRoutesUrl = "https://routes.googleapis.com/directions/v2:computeRoutes";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GoogleRoutesEtaService> _logger;
    private readonly BodegaDestinoService _bodegaDestinoService;
    private readonly string? _apiKey;
    private readonly int _marginMinutes;

    public GoogleRoutesEtaService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GoogleRoutesEtaService> logger,
        BodegaDestinoService bodegaDestinoService)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _bodegaDestinoService = bodegaDestinoService;

        var envVarName = _configuration["GoogleRoutes:ApiKeyEnvVar"] ?? "PMMOVIL_GOOGLE_ROUTES_KEY";
        _apiKey = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning(
                "GoogleRoutesEtaService: variable de entorno {EnvVar} no configurada. " +
                "El ETA del pre-registro quedara deshabilitado hasta que se configure " +
                "(ver docs/google-routes-api-setup.md).",
                envVarName);
        }

        _marginMinutes = _configuration.GetValue<int?>("GoogleRoutes:MarginMinutes") ?? 15;
    }

    public async Task<(double Lat, double Lon)?> ObtenerCoordenadasBodegaAsync(
        string idBodega,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idBodega))
            return null;

        var bodega = await _bodegaDestinoService.ObtenerAsync(idBodega, ct);
        return bodega is null ? null : (bodega.Lat, bodega.Lon);
    }

    public async Task<RouteEtaResult?> ComputeEtaAsync(
        double originLat, double originLon,
        double destLat, double destLon,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient("GoogleRoutesClient");
            client.DefaultRequestHeaders.Remove("X-Goog-Api-Key");
            client.DefaultRequestHeaders.Add("X-Goog-Api-Key", _apiKey);
            client.DefaultRequestHeaders.Remove("X-Goog-FieldMask");
            client.DefaultRequestHeaders.Add("X-Goog-FieldMask", "routes.duration,routes.distanceMeters");

            var body = new
            {
                origin = new { location = new { latLng = new { latitude = originLat, longitude = originLon } } },
                destination = new { location = new { latLng = new { latitude = destLat, longitude = destLon } } },
                travelMode = "DRIVE",
                routingPreference = "TRAFFIC_AWARE"
            };

            using var response = await client.PostAsJsonAsync(ComputeRoutesUrl, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "GoogleRoutesEtaService: respuesta {Status} de computeRoutes. Body: {Body}",
                    response.StatusCode, errorBody);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<ComputeRoutesResponse>(cancellationToken: ct);
            var ruta = payload?.Routes?.FirstOrDefault();
            if (ruta is null)
                return null;

            int segundos = ParseDuration(ruta.Duration);
            int minutos = (int)Math.Ceiling(segundos / 60.0) + _marginMinutes;
            double km = ruta.DistanceMeters / 1000.0;

            return new RouteEtaResult(minutos, Math.Round(km, 2));
        }
        catch (Exception ex)
        {
            // Nunca debe tumbar al llamador: el pre-registro se guarda sin ETA.
            _logger.LogWarning(ex, "GoogleRoutesEtaService: error consultando computeRoutes.");
            return null;
        }
    }

    /// <summary>
    /// Google devuelve la duracion como string tipo "1234s". Formato documentado
    /// en https://developers.google.com/maps/documentation/routes/reference/rest/v2/TopLevel/computeRoutes
    /// </summary>
    private static int ParseDuration(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
            return 0;

        var trimmed = duration.TrimEnd('s');
        return int.TryParse(trimmed, out int segundos) ? segundos : 0;
    }

    private class ComputeRoutesResponse
    {
        [JsonPropertyName("routes")]
        public List<ComputeRoutesRoute>? Routes { get; set; }
    }

    private class ComputeRoutesRoute
    {
        [JsonPropertyName("duration")]
        public string? Duration { get; set; }

        [JsonPropertyName("distanceMeters")]
        public int DistanceMeters { get; set; }
    }
}
