using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

using Dapper;

using Microsoft.Extensions.Caching.Memory;

using MySqlConnector;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Acceso facial de produccion.
///
/// La diferencia de fondo contra BiometriaLabService es QUIEN DECIDE. En el
/// laboratorio el dispositivo se bajaba el banco completo y comparaba en
/// local; eso servia para medir, pero para entrar es inviable: un APK
/// descompilado podria afirmar "soy Fulano" sin haber visto una cara. Aqui el
/// dispositivo solo manda 512 bytes y el servidor busca, decide y firma.
///
/// El banco NUNCA sale del servidor.
/// </summary>
public class FaceService
{
    private readonly BaseRepository _repository;
    private readonly IAuthService _authService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<FaceService> _logger;

    private const string CacheKeyGaleria = "face:galeria";
    private const string CacheKeyConfig = "face:config";

    /// <summary>
    /// El banco entero cabe en memoria de sobra (200 personas x 12 plantillas
    /// x 512 bytes = 1.2 MB) y traerlo de MySQL en cada intento seria tirar el
    /// tiempo del camino critico. Se refresca solo, y ademas se tira a mano
    /// cuando alguien se enrola o se revoca.
    /// </summary>
    private static readonly TimeSpan VidaGaleria = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan VidaConfig = TimeSpan.FromSeconds(30);

    /// <summary>128 floats little-endian.</summary>
    private const int DimsEsperadas = 128;

    /// <summary>
    /// Techo del PNG de la firma. Un trazo real anda por los 10-30 KB; 256 KB
    /// deja lugar de sobra para una pantalla grande y sigue estando lejos de
    /// que alguien use la columna para subir cualquier otra cosa.
    /// </summary>
    private const int MaxBytesFirma = 256 * 1024;

    public FaceService(
        BaseRepository repository,
        IAuthService authService,
        IMemoryCache cache,
        ILogger<FaceService> logger)
    {
        _repository = repository;
        _authService = authService;
        _cache = cache;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Configuracion viva
    // ─────────────────────────────────────────────────────────────────────

    private sealed class ConfigRow
    {
        public string Clave { get; set; } = string.Empty;
        public string Valor { get; set; } = string.Empty;
    }

    public async Task<FaceConfig> GetConfigAsync()
    {
        if (_cache.TryGetValue(CacheKeyConfig, out FaceConfig? guardada) && guardada != null)
        {
            return guardada;
        }

        FaceConfig config = new FaceConfig();
        try
        {
            using IDbConnection conn = _repository.CreateReportesConnection();
            IEnumerable<ConfigRow> filas = await conn.QueryAsync<ConfigRow>(
                "SELECT clave AS Clave, valor AS Valor FROM face_config");

            Dictionary<string, string> mapa = filas.ToDictionary(
                f => f.Clave, f => f.Valor, StringComparer.OrdinalIgnoreCase);

            config.Umbral = LeerFloat(mapa, "umbral", config.Umbral);
            config.MargenMinimo = LeerFloat(mapa, "margen_minimo", config.MargenMinimo);
            config.GaleriaPorPlanta = LeerFloat(mapa, "galeria_planta", 1f) >= 0.5f;
            config.MaxIntentos = (int)LeerFloat(mapa, "max_intentos", 2f);
            config.Activo = LeerFloat(mapa, "activo", 0f) >= 0.5f;
            // Nunca menos de 1: un cero en la tabla dejaria a todo el mundo sin
            // poder enrolarse, y eso no es un limite, es un apagon.
            config.MaxApariencias = Math.Max(1, (int)LeerFloat(mapa, "max_apariencias", 3f));
        }
        catch (Exception ex)
        {
            // Si la tabla todavia no existe, el acceso facial simplemente queda
            // apagado. No es motivo para tumbar nada mas.
            _logger.LogWarning(ex, "No se pudo leer face_config; el acceso facial queda apagado.");
            config.Activo = false;
        }

        _cache.Set(CacheKeyConfig, config, VidaConfig);
        return config;
    }

    private static float LeerFloat(IReadOnlyDictionary<string, string> mapa, string clave, float porDefecto)
    {
        if (mapa.TryGetValue(clave, out string? texto) &&
            float.TryParse(texto, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float valor))
        {
            return valor;
        }
        return porDefecto;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Galeria en memoria
    // ─────────────────────────────────────────────────────────────────────

    private sealed class GaleriaRow
    {
        public string ClaveUsuario { get; set; } = string.Empty;
        public string? Nombre { get; set; }
        public string? Planta { get; set; }
        public byte[]? Vector { get; set; }
    }

    private sealed class PlantillaViva
    {
        public string ClaveUsuario { get; set; } = string.Empty;
        public string? Planta { get; set; }
        public float[] Vector { get; set; } = Array.Empty<float>();
    }

    private async Task<List<PlantillaViva>> GetGaleriaAsync()
    {
        if (_cache.TryGetValue(CacheKeyGaleria, out List<PlantillaViva>? guardada) && guardada != null)
        {
            return guardada;
        }

        using IDbConnection conn = _repository.CreateReportesConnection();

        // El INNER JOIN contra usuarios no es decorativo: quien ya salio de la
        // empresa (inactivo = 1) deja de existir para el reconocimiento en el
        // mismo momento, sin tener que acordarse de revocar su rostro.
        IEnumerable<GaleriaRow> filas = await conn.QueryAsync<GaleriaRow>(@"
SELECT p.claveusuario AS ClaveUsuario,
       u.planta       AS Planta,
       p.vector       AS Vector
FROM face_plantilla p
INNER JOIN face_enrolamiento e ON e.id = p.enrolamiento_id AND e.estado = 'ACTIVO'
INNER JOIN usuarios u          ON u.claveusuario = p.claveusuario AND u.inactivo = 0
WHERE p.activo = 1");

        List<PlantillaViva> galeria = new List<PlantillaViva>();
        foreach (GaleriaRow fila in filas)
        {
            float[]? vector = DecodificarVector(fila.Vector);
            if (vector == null)
            {
                continue;
            }
            galeria.Add(new PlantillaViva
            {
                ClaveUsuario = fila.ClaveUsuario,
                Planta = fila.Planta?.Trim(),
                Vector = vector
            });
        }

        _cache.Set(CacheKeyGaleria, galeria, VidaGaleria);
        return galeria;
    }

    private void InvalidarGaleria()
    {
        _cache.Remove(CacheKeyGaleria);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Vectores
    // ─────────────────────────────────────────────────────────────────────

    private static float[]? DecodificarVector(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length % 4 != 0)
        {
            return null;
        }

        float[] vector = new float[bytes.Length / 4];
        for (int i = 0; i < vector.Length; i++)
        {
            // Little-endian explicito: es como lo escribe VectorCodec en Android
            // (ByteBuffer.LITTLE_ENDIAN). No se deja al azar de la arquitectura.
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(
                bytes.AsSpan(i * 4, 4));
        }
        return vector;
    }

    private static byte[]? DecodificarBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Similitud coseno: 1.0 identicos, 0.0 sin relacion. Es la misma cuenta
    /// que hace FaceRecognizerSF con FR_COSINE, hecha aqui para no reconstruir
    /// un Mat por cada plantilla del banco.
    /// </summary>
    private static float Coseno(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0f;
        }

        double producto = 0.0;
        double normaA = 0.0;
        double normaB = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            producto += a[i] * b[i];
            normaA += a[i] * a[i];
            normaB += b[i] * b[i];
        }

        double divisor = Math.Sqrt(normaA) * Math.Sqrt(normaB);
        return divisor <= 0.0 ? 0f : (float)(producto / divisor);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Identificacion 1:N -- el camino del login
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Resultado de un intento de entrar con la cara.</summary>
    public sealed class FaceLoginOutcome
    {
        public bool Aceptado { get; set; }
        public LoginResponse? Login { get; set; }

        /// <summary>Codigo HTTP que debe devolver el controlador.</summary>
        public int HttpStatus { get; set; } = StatusCodes.Status401Unauthorized;

        /// <summary>Para la bitacora del servidor, NO para el dispositivo.</summary>
        public string? Motivo { get; set; }
    }

    public async Task<FaceLoginOutcome> IdentificarAsync(FaceLoginRequest request, string? ip)
    {
        Stopwatch reloj = Stopwatch.StartNew();
        FaceConfig config = await GetConfigAsync();

        if (!config.Activo)
        {
            return new FaceLoginOutcome
            {
                Aceptado = false,
                HttpStatus = StatusCodes.Status503ServiceUnavailable,
                Motivo = "APAGADO",
                Login = new LoginResponse
                {
                    Success = false,
                    Message = "El acceso con rostro no esta habilitado."
                }
            };
        }

        byte[]? bytes = DecodificarBase64(request.VectorBase64);
        float[]? sonda = DecodificarVector(bytes);
        if (sonda == null || sonda.Length != DimsEsperadas)
        {
            await RegistrarIntentoAsync(request, ip, config, null, 0f, null, 0f,
                "RECHAZADO", "VECTOR_INVALIDO", 0, (int)reloj.ElapsedMilliseconds);

            return Rechazo(StatusCodes.Status400BadRequest, "VECTOR_INVALIDO",
                "El vector facial no tiene el formato esperado.");
        }

        List<PlantillaViva> galeria = await GetGaleriaAsync();

        // Acotar por planta baja N de cientos a decenas, y con eso baja el
        // riesgo de que alguien del banco se parezca de mas. Es el ajuste mas
        // barato y el que mas mueve la aguja.
        string? planta = request.Planta?.Trim();
        if (config.GaleriaPorPlanta && !string.IsNullOrWhiteSpace(planta))
        {
            List<PlantillaViva> acotada = galeria
                .Where(p => string.Equals(p.Planta, planta, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Si la planta no cuadra con nadie (equipo nuevo, planta mal escrita)
            // vale mas comparar contra todos que dejar a la gente afuera.
            if (acotada.Count > 0)
            {
                galeria = acotada;
            }
        }

        if (galeria.Count == 0)
        {
            await RegistrarIntentoAsync(request, ip, config, null, 0f, null, 0f,
                "RECHAZADO", "SIN_PLANTILLAS", 0, (int)reloj.ElapsedMilliseconds);

            return Rechazo(StatusCodes.Status401Unauthorized, "SIN_PLANTILLAS",
                "No se pudo verificar el rostro.");
        }

        // Mejor plantilla POR PERSONA antes de rankear. Si no se agrupa, el
        // segundo lugar casi siempre seria otra toma de la misma persona y el
        // margen no diria nada sobre el riesgo de confundir a dos distintas.
        Dictionary<string, float> mejorPorPersona = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (PlantillaViva plantilla in galeria)
        {
            float similitud = Coseno(sonda, plantilla.Vector);
            if (!mejorPorPersona.TryGetValue(plantilla.ClaveUsuario, out float previa) || similitud > previa)
            {
                mejorPorPersona[plantilla.ClaveUsuario] = similitud;
            }
        }

        List<KeyValuePair<string, float>> ranking = mejorPorPersona
            .OrderByDescending(par => par.Value)
            .ToList();

        string top1 = ranking[0].Key;
        float sim1 = ranking[0].Value;
        string? top2 = ranking.Count > 1 ? ranking[1].Key : null;
        float sim2 = ranking.Count > 1 ? ranking[1].Value : 0f;
        float margen = sim1 - sim2;

        int personas = ranking.Count;
        int msServidor = (int)reloj.ElapsedMilliseconds;

        if (sim1 < config.Umbral)
        {
            // A proposito NO se guarda a quien se parecio: un rechazo de plano
            // no debe dejar escrito "casi eras Fulano". Eso seria un mapa para
            // el que quiera probar suerte.
            await RegistrarIntentoAsync(request, ip, config, null, sim1, null, sim2,
                "RECHAZADO", "BAJO_UMBRAL", personas, msServidor);

            return Rechazo(StatusCodes.Status401Unauthorized, "BAJO_UMBRAL",
                "No se pudo verificar el rostro.");
        }

        if (margen < config.MargenMinimo)
        {
            // Aqui si se guardan los dos nombres: es una ambiguedad real entre
            // dos personas del banco y es justo lo que hay que revisar.
            await RegistrarIntentoAsync(request, ip, config, top1, sim1, top2, sim2,
                "RECHAZADO", "MARGEN_INSUFICIENTE", personas, msServidor);

            _logger.LogWarning(
                "Rostro ambiguo entre {Top1} y {Top2}. Sim1={Sim1}, Sim2={Sim2}, Margen={Margen}",
                top1, top2, sim1, sim2, margen);

            return Rechazo(StatusCodes.Status401Unauthorized, "MARGEN_INSUFICIENTE",
                "No se pudo verificar el rostro.");
        }

        LoginResponse login = await _authService.AuthenticateByUsernameAsync(top1);
        if (!login.Success)
        {
            await RegistrarIntentoAsync(request, ip, config, top1, sim1, top2, sim2,
                "RECHAZADO", "USUARIO_NO_VALIDO", personas, msServidor);

            return Rechazo(StatusCodes.Status401Unauthorized, "USUARIO_NO_VALIDO",
                "No se pudo verificar el rostro.");
        }

        await RegistrarIntentoAsync(request, ip, config, top1, sim1, top2, sim2,
            "ACEPTADO", null, personas, msServidor);

        _logger.LogInformation(
            "Acceso facial concedido. Usuario={Usuario}, Sim1={Sim1}, Margen={Margen}, Galeria={Personas}, ElapsedMs={Ms}",
            top1, sim1, margen, personas, msServidor);

        return new FaceLoginOutcome
        {
            Aceptado = true,
            HttpStatus = StatusCodes.Status200OK,
            Login = login
        };
    }

    /// <summary>
    /// Todos los rechazos dicen exactamente lo mismo hacia afuera. Distinguir
    /// "no te pareces" de "te pareces a dos" le regalaria informacion util a
    /// quien este probando.
    /// </summary>
    private static FaceLoginOutcome Rechazo(int status, string motivo, string mensaje)
    {
        return new FaceLoginOutcome
        {
            Aceptado = false,
            HttpStatus = status,
            Motivo = motivo,
            Login = new LoginResponse { Success = false, Message = mensaje }
        };
    }

    private async Task RegistrarIntentoAsync(
        FaceLoginRequest request,
        string? ip,
        FaceConfig config,
        string? top1,
        float sim1,
        string? top2,
        float sim2,
        string decision,
        string? motivo,
        int galeriaN,
        int msServidor)
    {
        try
        {
            using IDbConnection conn = _repository.CreateReportesConnection();
            await conn.ExecuteAsync(@"
INSERT INTO face_intento
    (claveusuario_top1, sim_top1, claveusuario_top2, sim_top2, margen,
     umbral, margen_minimo, decision, motivo, intento,
     planta, galeria_n, dispositivo, ip,
     luz_lux, luma, nitidez, rostro_px, score_deteccion, yaw, pitch, roll,
     ms_dispositivo, ms_servidor)
VALUES
    (@Top1, @Sim1, @Top2, @Sim2, @Margen,
     @Umbral, @MargenMinimo, @Decision, @Motivo, @Intento,
     @Planta, @GaleriaN, @Dispositivo, @Ip,
     @LuzLux, @Luma, @Nitidez, @RostroPx, @ScoreDeteccion, @Yaw, @Pitch, @Roll,
     @MsDispositivo, @MsServidor)",
                new
                {
                    Top1 = top1,
                    Sim1 = sim1,
                    Top2 = top2,
                    Sim2 = sim2,
                    Margen = sim1 - sim2,
                    config.Umbral,
                    config.MargenMinimo,
                    Decision = decision,
                    Motivo = motivo,
                    request.Intento,
                    request.Planta,
                    GaleriaN = galeriaN,
                    request.Dispositivo,
                    Ip = ip,
                    request.LuzLux,
                    request.Luma,
                    request.Nitidez,
                    request.RostroPx,
                    request.ScoreDeteccion,
                    request.Yaw,
                    request.Pitch,
                    request.Roll,
                    request.MsDispositivo,
                    MsServidor = msServidor
                });
        }
        catch (Exception ex)
        {
            // La bitacora no puede tumbar un login. Se pierde el renglon y se
            // deja constancia en el log de la aplicacion.
            _logger.LogError(ex, "No se pudo registrar el intento facial.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Enrolamiento
    // ─────────────────────────────────────────────────────────────────────

    private sealed class EstadoRow
    {
        public long Id { get; set; }
        public string? Etiqueta { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? Dispositivo { get; set; }
        public int Plantillas { get; set; }
        public bool Firmado { get; set; }
    }

    /// <summary>
    /// Todas las apariencias activas de la persona, de la mas nueva a la mas
    /// vieja. Se devuelven TODAS y no solo la ultima porque desde que existen
    /// las apariencias, la lista es justamente lo que la persona administra.
    /// </summary>
    public async Task<FaceEstadoResponse> GetEstadoAsync(string claveusuario)
    {
        FaceConfig config = await GetConfigAsync();

        using IDbConnection conn = _repository.CreateReportesConnection();
        IEnumerable<EstadoRow> filas = await conn.QueryAsync<EstadoRow>(@"
SELECT e.id                          AS Id,
       e.etiqueta                    AS Etiqueta,
       e.created_at                  AS CreatedAt,
       e.dispositivo                 AS Dispositivo,
       COUNT(p.id)                   AS Plantillas,
       (e.firma_png IS NOT NULL)     AS Firmado
FROM face_enrolamiento e
LEFT JOIN face_plantilla p ON p.enrolamiento_id = e.id AND p.activo = 1
WHERE e.claveusuario = @claveusuario AND e.estado = 'ACTIVO'
GROUP BY e.id, e.etiqueta, e.created_at, e.dispositivo, e.firma_png
ORDER BY e.created_at DESC", new { claveusuario });

        // Un enrolamiento sin plantillas vivas no cuenta como apariencia: paso
        // algo raro al guardarlo y ofrecerlo para borrar solo confunde.
        List<EstadoRow> conPlantillas = filas.Where(f => f.Plantillas > 0).ToList();

        return new FaceEstadoResponse
        {
            Success = true,
            Disponible = config.Activo,
            Enrolado = conPlantillas.Count > 0,
            Plantillas = conPlantillas.Sum(f => f.Plantillas),
            EnroladoEl = conPlantillas.Count > 0 ? conPlantillas[0].CreatedAt : null,
            Dispositivo = conPlantillas.Count > 0 ? conPlantillas[0].Dispositivo : null,
            MaxApariencias = config.MaxApariencias,
            Apariencias = conPlantillas.Select(f => new FaceAparienciaDto
            {
                Id = f.Id,
                Etiqueta = string.IsNullOrWhiteSpace(f.Etiqueta) ? "Principal" : f.Etiqueta,
                Plantillas = f.Plantillas,
                CreatedAt = f.CreatedAt,
                Dispositivo = f.Dispositivo,
                Firmado = f.Firmado
            }).ToList()
        };
    }

    private sealed class UsuarioRow
    {
        public string? UsuarioId { get; set; }
        public string? Nombre { get; set; }
        public string? Planta { get; set; }
    }

    private sealed class AparienciaRow
    {
        public long Id { get; set; }
        public string? Etiqueta { get; set; }
    }

    /// <summary>
    /// Nombre de la apariencia nueva.
    ///
    /// La app siempre manda uno, pero el servidor no puede confiar en eso: un
    /// nombre vacio dejaria una fila que la persona no sabe cual es a la hora
    /// de borrar. Si el nombre ya existe se numera, porque dos "Con lentes" en
    /// la lista son igual de inutiles que ninguno.
    /// </summary>
    private static string ResolverEtiqueta(
        string? propuesta,
        List<AparienciaRow> existentes,
        bool reemplazar)
    {
        // Al reemplazar no queda nada contra que chocar.
        HashSet<string> tomadas = reemplazar
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                existentes.Select(e => (e.Etiqueta ?? string.Empty).Trim()),
                StringComparer.OrdinalIgnoreCase);

        string limpia = (propuesta ?? string.Empty).Trim();
        if (limpia.Length > 40)
        {
            limpia = limpia.Substring(0, 40).Trim();
        }

        if (limpia.Length == 0)
        {
            limpia = tomadas.Count == 0 ? "Principal" : $"Apariencia {tomadas.Count + 1}";
        }

        if (!tomadas.Contains(limpia))
        {
            return limpia;
        }

        for (int i = 2; i <= 99; i++)
        {
            string candidata = $"{limpia} {i}";
            if (candidata.Length > 40)
            {
                candidata = candidata.Substring(0, 40);
            }
            if (!tomadas.Contains(candidata))
            {
                return candidata;
            }
        }

        return $"Apariencia {DateTime.Now:yyyyMMddHHmm}";
    }

    public async Task<FaceResult> GuardarEnrolamientoAsync(string claveusuario, FaceEnrolamientoRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AceptacionTexto))
        {
            return new FaceResult
            {
                Success = false,
                Message = "Falta la aceptacion del aviso de datos biometricos."
            };
        }

        // La firma es obligatoria y se valida ANTES que las plantillas: si
        // falta, no hay consentimiento completo y da igual que las 26 tomas
        // esten perfectas.
        byte[]? firma = DecodificarBase64(request.FirmaPngBase64);
        if (firma == null || firma.Length == 0)
        {
            return new FaceResult
            {
                Success = false,
                Message = "Falta la firma del aviso de datos biometricos."
            };
        }

        if (firma.Length > MaxBytesFirma)
        {
            return new FaceResult
            {
                Success = false,
                Message = $"La firma pesa {firma.Length / 1024} KB y el limite son {MaxBytesFirma / 1024} KB."
            };
        }

        if (request.Plantillas == null || request.Plantillas.Count == 0)
        {
            return new FaceResult { Success = false, Message = "No llego ninguna plantilla." };
        }

        // Se validan TODAS antes de abrir la transaccion: mas vale rechazar el
        // alta completa que dejar a alguien con la mitad de sus poses.
        List<(FacePlantillaDto Dto, byte[] Bytes)> validas = new List<(FacePlantillaDto, byte[])>();
        foreach (FacePlantillaDto dto in request.Plantillas)
        {
            byte[]? bytes = DecodificarBase64(dto.VectorBase64);
            if (bytes == null || bytes.Length != DimsEsperadas * 4)
            {
                return new FaceResult
                {
                    Success = false,
                    Message = $"Una plantilla mide {bytes?.Length ?? 0} bytes y se esperaban {DimsEsperadas * 4}."
                };
            }
            validas.Add((dto, bytes));
        }

        using MySqlConnection conn = (MySqlConnection)_repository.CreateReportesConnection();
        await conn.OpenAsync();

        UsuarioRow? usuario = await conn.QueryFirstOrDefaultAsync<UsuarioRow>(@"
SELECT usuarioid                                  AS UsuarioId,
       CONCAT(TRIM(nombre), ' ', TRIM(apaterno))  AS Nombre,
       planta                                     AS Planta
FROM usuarios
WHERE claveusuario = @claveusuario AND inactivo = 0",
            new { claveusuario });

        if (usuario == null)
        {
            return new FaceResult { Success = false, Message = "El usuario no esta activo." };
        }

        FaceConfig config = await GetConfigAsync();

        // Cuantas apariencias tiene ya. Se cuenta FUERA de la transaccion a
        // proposito: es una validacion de producto, no una condicion de
        // integridad, y el caso de dos altas simultaneas de la misma persona en
        // dos equipos no existe -- para enrolarse hay que estar frente al aro.
        List<AparienciaRow> existentes = (await conn.QueryAsync<AparienciaRow>(@"
SELECT e.id        AS Id,
       e.etiqueta  AS Etiqueta
FROM face_enrolamiento e
INNER JOIN face_plantilla p ON p.enrolamiento_id = e.id AND p.activo = 1
WHERE e.claveusuario = @claveusuario AND e.estado = 'ACTIVO'
GROUP BY e.id, e.etiqueta
ORDER BY e.created_at", new { claveusuario })).ToList();

        if (!request.Reemplazar && existentes.Count >= config.MaxApariencias)
        {
            return new FaceResult
            {
                Success = false,
                Plantillas = existentes.Count,
                Message = $"Ya tienes {existentes.Count} apariencias registradas, que es el maximo. " +
                          "Borra una desde Ajustes antes de agregar otra."
            };
        }

        string etiqueta = ResolverEtiqueta(request.Etiqueta, existentes, request.Reemplazar);

        using MySqlTransaction transaccion = await conn.BeginTransactionAsync();
        try
        {
            // Por defecto se AGREGA: la persona acumula apariencias (con
            // lentes, sin barba) y el login se queda con la que mejor coincida.
            // Solo se revoca lo anterior cuando lo pidieron explicitamente.
            if (request.Reemplazar)
            {
                await conn.ExecuteAsync(@"
UPDATE face_enrolamiento
SET estado = 'REVOCADO', revocado_at = NOW()
WHERE claveusuario = @claveusuario AND estado = 'ACTIVO'",
                    new { claveusuario }, transaccion);

                await conn.ExecuteAsync(@"
UPDATE face_plantilla p
INNER JOIN face_enrolamiento e ON e.id = p.enrolamiento_id
SET p.activo = 0
WHERE e.claveusuario = @claveusuario AND e.estado = 'REVOCADO'",
                    new { claveusuario }, transaccion);
            }

            long enrolamientoId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO face_enrolamiento
    (usuarioid, claveusuario, nombre, etiqueta, modelo, dims,
     aceptacion_at, aceptacion_texto, firma_png, firma_at, dispositivo, planta, estado)
VALUES
    (@UsuarioId, @ClaveUsuario, @Nombre, @Etiqueta, @Modelo, @Dims,
     NOW(), @AceptacionTexto, @Firma, NOW(), @Dispositivo, @Planta, 'ACTIVO');
SELECT LAST_INSERT_ID();",
                new
                {
                    usuario.UsuarioId,
                    ClaveUsuario = claveusuario,
                    usuario.Nombre,
                    Etiqueta = etiqueta,
                    Modelo = string.IsNullOrWhiteSpace(request.Modelo) ? "sface-2021dec" : request.Modelo,
                    Dims = DimsEsperadas,
                    request.AceptacionTexto,
                    Firma = firma,
                    request.Dispositivo,
                    Planta = string.IsNullOrWhiteSpace(request.Planta) ? usuario.Planta : request.Planta
                }, transaccion);

            foreach ((FacePlantillaDto dto, byte[] bytes) in validas)
            {
                await conn.ExecuteAsync(@"
INSERT INTO face_plantilla
    (enrolamiento_id, claveusuario, modelo, dims, vector, pose_sector,
     yaw, pitch, roll, luma, nitidez, rostro_px, score_deteccion, activo)
VALUES
    (@EnrolamientoId, @ClaveUsuario, @Modelo, @Dims, @Vector, @PoseSector,
     @Yaw, @Pitch, @Roll, @Luma, @Nitidez, @RostroPx, @ScoreDeteccion, 1)",
                    new
                    {
                        EnrolamientoId = enrolamientoId,
                        ClaveUsuario = claveusuario,
                        Modelo = string.IsNullOrWhiteSpace(request.Modelo) ? "sface-2021dec" : request.Modelo,
                        Dims = DimsEsperadas,
                        Vector = bytes,
                        dto.PoseSector,
                        dto.Yaw,
                        dto.Pitch,
                        dto.Roll,
                        dto.Luma,
                        dto.Nitidez,
                        dto.RostroPx,
                        dto.ScoreDeteccion
                    }, transaccion);
            }

            await transaccion.CommitAsync();
            InvalidarGaleria();

            int totalApariencias = request.Reemplazar ? 1 : existentes.Count + 1;

            _logger.LogInformation(
                "Rostro enrolado. Usuario={Usuario}, Etiqueta={Etiqueta}, Plantillas={Plantillas}, " +
                "Apariencias={Apariencias}, Reemplazo={Reemplazo}, Dispositivo={Dispositivo}",
                claveusuario, etiqueta, validas.Count, totalApariencias,
                request.Reemplazar, request.Dispositivo);

            return new FaceResult
            {
                Success = true,
                Id = enrolamientoId,
                Plantillas = validas.Count,
                Message = totalApariencias > 1
                    ? $"Apariencia \"{etiqueta}\" agregada. Ahora tienes {totalApariencias}."
                    : "Rostro registrado."
            };
        }
        catch (Exception ex)
        {
            await transaccion.RollbackAsync();
            _logger.LogError(ex, "Fallo el enrolamiento facial de {Usuario}", claveusuario);
            return new FaceResult { Success = false, Message = "No se pudo guardar el rostro." };
        }
    }

    /// <summary>
    /// Borra UNA apariencia, no todas.
    ///
    /// El id va siempre acompanado de la clave del usuario en el WHERE: sin eso,
    /// mandar el id de otra persona borraria el rostro de esa persona, y el id
    /// es un entero consecutivo que cualquiera puede adivinar.
    /// </summary>
    public async Task<FaceResult> RevocarAparienciaAsync(string claveusuario, long enrolamientoId)
    {
        using IDbConnection conn = _repository.CreateReportesConnection();

        int filas = await conn.ExecuteAsync(@"
UPDATE face_enrolamiento
SET estado = 'REVOCADO', revocado_at = NOW()
WHERE id = @enrolamientoId AND claveusuario = @claveusuario AND estado = 'ACTIVO'",
            new { enrolamientoId, claveusuario });

        if (filas == 0)
        {
            return new FaceResult
            {
                Success = false,
                Message = "Esa apariencia no existe o ya estaba borrada."
            };
        }

        await conn.ExecuteAsync(@"
UPDATE face_plantilla
SET activo = 0
WHERE enrolamiento_id = @enrolamientoId AND claveusuario = @claveusuario",
            new { enrolamientoId, claveusuario });

        InvalidarGaleria();

        int restantes = await conn.ExecuteScalarAsync<int>(@"
SELECT COUNT(DISTINCT e.id)
FROM face_enrolamiento e
INNER JOIN face_plantilla p ON p.enrolamiento_id = e.id AND p.activo = 1
WHERE e.claveusuario = @claveusuario AND e.estado = 'ACTIVO'",
            new { claveusuario });

        _logger.LogInformation(
            "Apariencia facial borrada. Usuario={Usuario}, Enrolamiento={Id}, Restantes={Restantes}",
            claveusuario, enrolamientoId, restantes);

        return new FaceResult
        {
            Success = true,
            Id = enrolamientoId,
            Plantillas = restantes,
            Message = restantes > 0
                ? $"Apariencia borrada. Te quedan {restantes}."
                : "Apariencia borrada. Ya no tienes rostro registrado."
        };
    }

    public async Task<FaceResult> RevocarAsync(string claveusuario)
    {
        using IDbConnection conn = _repository.CreateReportesConnection();

        int filas = await conn.ExecuteAsync(@"
UPDATE face_enrolamiento
SET estado = 'REVOCADO', revocado_at = NOW()
WHERE claveusuario = @claveusuario AND estado = 'ACTIVO'",
            new { claveusuario });

        await conn.ExecuteAsync(@"
UPDATE face_plantilla p
INNER JOIN face_enrolamiento e ON e.id = p.enrolamiento_id
SET p.activo = 0
WHERE e.claveusuario = @claveusuario AND e.estado = 'REVOCADO'",
            new { claveusuario });

        InvalidarGaleria();

        _logger.LogInformation("Rostro revocado. Usuario={Usuario}, Enrolamientos={Filas}", claveusuario, filas);

        return new FaceResult
        {
            Success = true,
            Message = filas > 0 ? "Rostro eliminado." : "No habia rostro registrado."
        };
    }
}
