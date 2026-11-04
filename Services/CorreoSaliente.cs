using System.Text.Json;

using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MimeKit;

namespace PMMovil.Api.Services;

/// <summary>Quien entrega un mensaje ya armado al servidor SMTP. Lanza si no lo logra.</summary>
public interface ITransporteCorreo
{
    Task EnviarAsync(MimeMessage mensaje, CancellationToken ct);
}

/// <summary>
/// Cola durable de correo saliente. El mensaje se guarda en disco ANTES de intentar mandarlo y un trabajador lo reintenta
/// con espera creciente hasta que el servidor SMTP lo acepte, aunque el API se reinicie, se copien archivos nuevos al
/// servidor o Mailgun no conteste en ese momento. Antes un fallo de SMTP perdía la notificación sin dejar rastro.
/// </summary>
public interface ICorreoSalienteCola
{
    /// <summary>
    /// Verdadero cuando el trabajador de la cola está corriendo en este proceso. Los modos admin de línea de comandos
    /// no lo tienen: ahí el envío es directo y un fallo se reporta en pantalla, no se deja en una cola que nadie vacía.
    /// </summary>
    bool Activa { get; }

    /// <summary>Deja el mensaje en disco y despierta al trabajador. Falso solo si no se pudo escribir.</summary>
    Task<bool> EncolarAsync(MimeMessage mensaje, string tipo, string referencia, CancellationToken ct = default);
}

/// <summary>SMTP real (Mailgun): misma configuración que el resto del API.</summary>
public sealed class TransporteSmtp : ITransporteCorreo
{
    private readonly IConfiguration _config;

    public TransporteSmtp(IConfiguration config)
    {
        _config = config;
    }

    public async Task EnviarAsync(MimeMessage mensaje, CancellationToken ct)
    {
        string host = _config["Smtp:Host"] ?? throw new InvalidOperationException("Falta configurar Smtp:Host.");
        int port = int.TryParse(_config["Smtp:Port"], out int parsed) && parsed > 0 ? parsed : 587;
        string user = _config["Smtp:User"] ?? string.Empty;
        string pass = _config["Smtp:Password"] ?? string.Empty;

        SecureSocketOptions seguridad = SecureSocketOptions.Auto;
        if (_config["Smtp:UseSsl"] == "true") seguridad = SecureSocketOptions.SslOnConnect;
        else if (_config["Smtp:UseStartTls"] != "false") seguridad = SecureSocketOptions.StartTls;

        // 30 s y no 10: un apretón de manos TLS lento contra Mailgun no debe contar como fallo.
        using var client = new SmtpClient { Timeout = 30_000 };
        await client.ConnectAsync(host, port, seguridad, ct);
        if (!string.IsNullOrWhiteSpace(user))
        {
            await client.AuthenticateAsync(user, pass, ct);
        }

        await client.SendAsync(mensaje, ct);
        await client.DisconnectAsync(true, ct);
    }
}

public sealed class ColaDeCorreo : BackgroundService, ICorreoSalienteCola
{
    /// <summary>Espera antes de cada reintento: 30 s, 1, 2, 5, 10, 20, 30 min, 1, 2 y 4 h (unas 8 h en total).</summary>
    private static readonly TimeSpan[] Esperas =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
        TimeSpan.FromHours(2), TimeSpan.FromHours(4)
    ];

    public static int MaxIntentos => Esperas.Length;

    private static readonly TimeSpan CandadoVencido = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetencionEnviados = TimeSpan.FromDays(14);
    private static readonly TimeSpan RetencionFallidos = TimeSpan.FromDays(60);

    private readonly ITransporteCorreo _transporte;
    private readonly ILogger<ColaDeCorreo> _logger;
    private readonly Func<DateTime> _ahora;
    private readonly string _pendientes;
    private readonly string _enviados;
    private readonly string _fallidos;
    private readonly SemaphoreSlim _senal = new(0);
    private readonly SemaphoreSlim _turno = new(1, 1);
    private volatile bool _activa;

    public ColaDeCorreo(ITransporteCorreo transporte, string directorio, ILogger<ColaDeCorreo> logger, Func<DateTime>? ahora = null)
    {
        _transporte = transporte;
        _logger = logger;
        _ahora = ahora ?? (() => DateTime.Now);
        _pendientes = Path.Combine(directorio, "pendientes");
        _enviados = Path.Combine(directorio, "enviados");
        _fallidos = Path.Combine(directorio, "fallidos");
        Directory.CreateDirectory(_pendientes);
        Directory.CreateDirectory(_enviados);
        Directory.CreateDirectory(_fallidos);
    }

    public bool Activa => _activa;

    /// <summary>Correos que esperan su turno o su próximo reintento.</summary>
    public int Pendientes => Directory.EnumerateFiles(_pendientes, "*.json").Count();

    /// <summary>Correos que se agotaron los reintentos: piden atención de una persona.</summary>
    public int Fallidos => Directory.EnumerateFiles(_fallidos, "*.json").Count();

    public async Task<bool> EncolarAsync(MimeMessage mensaje, string tipo, string referencia, CancellationToken ct = default)
    {
        try
        {
            DateTime ahora = _ahora();
            string id = $"{ahora:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var meta = new MetaCorreo
            {
                Id = id,
                Tipo = tipo,
                Referencia = referencia,
                Asunto = mensaje.Subject ?? string.Empty,
                Creado = ahora,
                ProximoIntento = ahora,
                // Algunos transportes quitan el Bcc al serializar: se guarda aparte y se repone al enviar.
                Bcc = mensaje.Bcc.Mailboxes.Select(m => m.ToString(true)).ToList()
            };

            string eml = Path.Combine(_pendientes, id + ".eml");
            string json = Path.Combine(_pendientes, id + ".json");

            // Primero el mensaje y al final los datos de control: un .json es la señal de que el par está completo.
            await using (var flujo = File.Create(eml + ".tmp"))
            {
                await mensaje.WriteToAsync(flujo, ct);
            }
            File.Move(eml + ".tmp", eml);
            await File.WriteAllTextAsync(json + ".tmp", JsonSerializer.Serialize(meta), ct);
            File.Move(json + ".tmp", json);

            _logger.LogInformation("ColaDeCorreo: encolado '{Asunto}' ({Tipo} {Referencia}), id {Id}.", meta.Asunto, tipo, referencia, id);
            _senal.Release();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ColaDeCorreo: no se pudo guardar en disco el correo '{Asunto}' ({Tipo} {Referencia}).", mensaje.Subject, tipo, referencia);
            return false;
        }
    }

    /// <summary>
    /// Manda los correos cuyo turno ya llegó. Devuelve cuántos salieron. Público para poder probarlo sin esperar al
    /// reloj del trabajador.
    /// </summary>
    public async Task<int> ProcesarPendientesAsync(CancellationToken ct)
    {
        await _turno.WaitAsync(ct);
        try
        {
            int enviados = 0;
            foreach (string json in Directory.EnumerateFiles(_pendientes, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();

                MetaCorreo? meta = LeerMeta(json);
                if (meta == null)
                {
                    _logger.LogError("ColaDeCorreo: el archivo de control {Archivo} está dañado; se aparta en fallidos.", json);
                    Apartar(json, _fallidos);
                    continue;
                }

                if (meta.ProximoIntento > _ahora()) continue;

                string candado = Path.ChangeExtension(json, ".lock");
                if (!TomarCandado(candado)) continue;
                try
                {
                    if (await IntentarAsync(json, meta, ct)) enviados++;
                }
                finally
                {
                    try { File.Delete(candado); } catch (IOException) { }
                }
            }

            Depurar();
            return enviados;
        }
        finally
        {
            _turno.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _activa = true;
        _logger.LogInformation(
            "ColaDeCorreo: activa. Pendientes={Pendientes}, fallidos que piden atención={Fallidos}.", Pendientes, Fallidos);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcesarPendientesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ColaDeCorreo: fallo no controlado procesando la cola; se vuelve a intentar.");
                }

                try
                {
                    // Despierta al encolar; si nadie avisa, revisa cada 20 s por si ya toca algún reintento.
                    await _senal.WaitAsync(TimeSpan.FromSeconds(20), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            _activa = false;
        }
    }

    private async Task<bool> IntentarAsync(string json, MetaCorreo meta, CancellationToken ct)
    {
        string eml = Path.ChangeExtension(json, ".eml");
        try
        {
            MimeMessage mensaje;
            await using (var flujo = File.OpenRead(eml))
            {
                mensaje = await MimeMessage.LoadAsync(flujo, ct);
            }

            mensaje.Bcc.Clear();
            foreach (string oculta in meta.Bcc)
            {
                mensaje.Bcc.Add(MailboxAddress.Parse(oculta));
            }

            await _transporte.EnviarAsync(mensaje, ct);

            meta.Intentos++;
            meta.Enviado = _ahora();
            meta.UltimoError = null;
            File.WriteAllText(json, JsonSerializer.Serialize(meta));
            Apartar(json, _enviados);
            _logger.LogInformation(
                "ColaDeCorreo: enviado '{Asunto}' ({Tipo} {Referencia}) en el intento {Intento}.",
                meta.Asunto, meta.Tipo, meta.Referencia, meta.Intentos);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            meta.Intentos++;
            meta.UltimoError = $"{ex.GetType().Name}: {ex.Message}";

            if (meta.Intentos >= MaxIntentos)
            {
                File.WriteAllText(json, JsonSerializer.Serialize(meta));
                Apartar(json, _fallidos);
                _logger.LogCritical(
                    ex,
                    "ColaDeCorreo: SE RINDIÓ '{Asunto}' ({Tipo} {Referencia}) tras {Intentos} intentos. Queda en {Carpeta}; hay que mandarlo a mano.",
                    meta.Asunto, meta.Tipo, meta.Referencia, meta.Intentos, _fallidos);
                return false;
            }

            TimeSpan espera = Esperas[Math.Min(meta.Intentos - 1, Esperas.Length - 1)];
            meta.ProximoIntento = _ahora() + espera;
            File.WriteAllText(json, JsonSerializer.Serialize(meta));
            _logger.LogWarning(
                ex,
                "ColaDeCorreo: no salió '{Asunto}' ({Tipo} {Referencia}), intento {Intento} de {Max}; se reintenta en {Espera}.",
                meta.Asunto, meta.Tipo, meta.Referencia, meta.Intentos, MaxIntentos, espera);
            return false;
        }
    }

    private static MetaCorreo? LeerMeta(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MetaCorreo>(File.ReadAllText(json));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Evita que dos procesos (por ejemplo, el viejo y el nuevo durante un reciclaje de IIS) manden el mismo correo.</summary>
    private bool TomarCandado(string candado)
    {
        for (int intento = 0; intento < 2; intento++)
        {
            try
            {
                using (File.Open(candado, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                return true;
            }
            catch (IOException)
            {
                // Un candado de un proceso que murió a media operación no debe bloquear el correo para siempre.
                try
                {
                    if (_ahora() - File.GetLastWriteTime(candado) > CandadoVencido) File.Delete(candado);
                    else return false;
                }
                catch (IOException)
                {
                    return false;
                }
            }
        }
        return false;
    }

    private static void Apartar(string json, string destino)
    {
        string eml = Path.ChangeExtension(json, ".eml");
        if (File.Exists(eml)) File.Move(eml, Path.Combine(destino, Path.GetFileName(eml)), true);
        File.Move(json, Path.Combine(destino, Path.GetFileName(json)), true);
    }

    private void Depurar()
    {
        Borrar(_enviados, RetencionEnviados);
        Borrar(_fallidos, RetencionFallidos);
    }

    private void Borrar(string carpeta, TimeSpan retencion)
    {
        try
        {
            DateTime limite = _ahora() - retencion;
            foreach (string archivo in Directory.EnumerateFiles(carpeta))
            {
                if (File.GetLastWriteTime(archivo) < limite) File.Delete(archivo);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "ColaDeCorreo: no se pudo depurar {Carpeta}.", carpeta);
        }
    }

    private sealed class MetaCorreo
    {
        public string Id { get; set; } = string.Empty;
        public string Tipo { get; set; } = string.Empty;
        public string Referencia { get; set; } = string.Empty;
        public string Asunto { get; set; } = string.Empty;
        public DateTime Creado { get; set; }
        public DateTime ProximoIntento { get; set; }
        public DateTime? Enviado { get; set; }
        public int Intentos { get; set; }
        public string? UltimoError { get; set; }
        public List<string> Bcc { get; set; } = [];
    }
}
