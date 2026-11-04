using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MimeKit;

namespace PMMovil.Api.Services;

public class SmtpService : ISmtpService
{
    // En Development, los avisos que usan este servicio
    // se redirigen únicamente a esta casilla, sin importar el destinatario original
    // (venga escrito en código o traído de base de datos). Evita que las pruebas
    // le lleguen a clientes o usuarios reales.
    private const string CorreoPruebasDesarrollo = "desarrollo3@grupopmm.com";

    private readonly IConfiguration _config;

    private readonly ILogger<SmtpService> _logger;

    private readonly IHostEnvironment _env;

    private readonly ICorreoSalienteCola _cola;

    public SmtpService(IConfiguration config, ILogger<SmtpService> logger, IHostEnvironment env, ICorreoSalienteCola cola)
    {
        _config = config;
        _logger = logger;
        _env = env;
        _cola = cola;
    }

    public async Task<bool> EnviarAsync(
        IEnumerable<string> destinatarios,
        string asunto,
        string cuerpoHtml,
        IEnumerable<EmailAttachment>? adjuntos = null,
        IEnumerable<string>? copias = null,
        IEnumerable<string>? copiasOcultas = null,
        bool incluirCopiasOcultasAutomaticas = true,
        CancellationToken ct = default(CancellationToken),
        bool preferirCola = false)
    {
        List<string> lista = destinatarios?.Where((string c) => !string.IsNullOrWhiteSpace(c)).Distinct().ToList() ?? new List<string>();

        bool esDesarrollo = _env.IsDevelopment();
        if (esDesarrollo)
        {
            if (lista.Count > 0)
            {
                _logger.LogInformation("SmtpService: entorno Development, se redirige el envío de '{Asunto}' (destinatarios originales: {Original}) a {Destino}.", asunto, string.Join(", ", lista), CorreoPruebasDesarrollo);
            }
            lista = new List<string> { CorreoPruebasDesarrollo };
        }

        if (lista.Count == 0)
        {
            _logger.LogWarning("SmtpService: intento de envío sin destinatarios.");
            return false;
        }
        string host = _config["Smtp:Host"];
        string portStr = _config["Smtp:Port"];
        string user = _config["Smtp:User"];
        string pass = _config["Smtp:Password"];
        string from = _config["Smtp:From"] ?? user;
        string fromName = _config["Smtp:FromName"] ?? "PMMovil";
        bool ssl;
        bool useStartTls = !bool.TryParse(_config["Smtp:UseStartTls"], out ssl) || ssl;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from))
        {
            _logger.LogError("SmtpService: configuración SMTP incompleta (Host / From).");
            return false;
        }
        if (!int.TryParse(portStr, out var port))
        {
            port = 587;
        }
        // Fuera del try para que, si el servidor SMTP falla, el mensaje ya armado pase a la cola de reintentos.
        MimeMessage? msg = null;
        bool listoParaEnviar = false;
        try
        {
            msg = new MimeMessage
            {
                From = { (InternetAddress)new MailboxAddress(fromName, from) }
            };
            foreach (string dest in lista)
            {
                try
                {
                    msg.To.Add(MailboxAddress.Parse(dest));
                }
                catch
                {
                    _logger.LogWarning("SmtpService: correo inválido ignorado: {Dest}", dest);
                }
            }
            if (msg.To.Count == 0)
            {
                _logger.LogWarning("SmtpService: no quedaron destinatarios válidos.");
                return false;
            }

            foreach (string copia in copias ?? Enumerable.Empty<string>())
            {
                try
                {
                    msg.Cc.Add(MailboxAddress.Parse(copia));
                }
                catch
                {
                    _logger.LogWarning("SmtpService: correo de copia inválido ignorado: {Copia}", copia);
                }
            }

            HashSet<string> destinatariosExistentes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MailboxAddress destinatario in msg.To.Mailboxes)
            {
                destinatariosExistentes.Add(destinatario.Address);
            }
            foreach (MailboxAddress copia in msg.Cc.Mailboxes)
            {
                destinatariosExistentes.Add(copia.Address);
            }

            if (!esDesarrollo && incluirCopiasOcultasAutomaticas)
            {
                foreach (string copiaOculta in new[]
                {
                    "desarrollo@grupopmm.com",
                    "desarrollo3@grupopmm.com",
                    "carmen.barragan@grupopmm.com"
                })
                {
                    if (destinatariosExistentes.Add(copiaOculta))
                    {
                        msg.Bcc.Add(MailboxAddress.Parse(copiaOculta));
                    }
                }
            }

            foreach (string copiaOculta in copiasOcultas ?? Enumerable.Empty<string>())
            {
                try
                {
                    if (destinatariosExistentes.Add(copiaOculta))
                    {
                        msg.Bcc.Add(MailboxAddress.Parse(copiaOculta));
                    }
                }
                catch
                {
                    _logger.LogWarning("SmtpService: correo de copia oculta inválido ignorado: {CopiaOculta}", copiaOculta);
                }
            }

            msg.Subject = asunto;
            var bodyBuilder = new BodyBuilder { HtmlBody = cuerpoHtml };
            foreach (EmailAttachment adjunto in adjuntos ?? Enumerable.Empty<EmailAttachment>())
            {
                try
                {
                    bodyBuilder.Attachments.Add(adjunto.FileName, adjunto.Content, MimeKit.ContentType.Parse(adjunto.ContentType));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SmtpService: no se pudo adjuntar '{FileName}', se omite.", adjunto.FileName);
                }
            }
            msg.Body = bodyBuilder.ToMessageBody();
            listoParaEnviar = true;

            // Quien no puede esperar al SMTP (la confirmacion de una salida con el guardia mirando el spinner) pide la
            // cola: el mensaje queda guardado en disco y sale por el trabajador en segundos. Si no se pudo guardar,
            // sigue el envio directo de siempre.
            if (preferirCola && _cola.Activa
                && await _cola.EncolarAsync(msg, "smtp", asunto, CancellationToken.None))
            {
                _logger.LogInformation("SmtpService: '{Asunto}' quedó en la cola de correo ({N} destinatarios).", asunto, msg.To.Count);
                return true;
            }

            // 30 s y no 10: un apretón de manos TLS lento contra Mailgun no debe contar como fallo.
            using SmtpClient client = new SmtpClient();
            client.Timeout = 30000;

            SecureSocketOptions secureOption = SecureSocketOptions.Auto;
            if (_config["Smtp:UseSsl"] == "true") secureOption = SecureSocketOptions.SslOnConnect;
            else if (useStartTls) secureOption = SecureSocketOptions.StartTls;

            try
            {
                await client.ConnectAsync(host, port, secureOption, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SmtpService: Error al conectar con el host {Host}:{Port}", host, port);
                return await DejarEnColaAsync(msg, asunto);
            }

            if (!string.IsNullOrWhiteSpace(user))
            {
                try
                {
                    await client.AuthenticateAsync(user, pass ?? "", ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SmtpService: Error de autenticación para el usuario {User}", user);
                    return await DejarEnColaAsync(msg, asunto);
                }
            }

            await client.SendAsync(msg, ct);
            await client.DisconnectAsync(quit: true, ct);
            _logger.LogInformation("SmtpService: enviado '{Asunto}' a {N} destinatarios.", asunto, msg.To.Count);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "SmtpService: error crítico enviando '{Asunto}'. Detalle: {Msg}", asunto, exception.Message);
            if (msg != null && listoParaEnviar)
            {
                return await DejarEnColaAsync(msg, asunto);
            }
            return false;
        }
    }

    /// <summary>
    /// El envío directo falló: el mensaje se guarda en la cola durable para que se reintente solo. Sigue devolviendo
    /// falso (no salió AHORA), pero la notificación ya no se pierde. Sin trabajador de cola (modos admin de línea de
    /// comandos) no se encola: el fallo se reporta en pantalla.
    /// </summary>
    private async Task<bool> DejarEnColaAsync(MimeMessage msg, string asunto)
    {
        if (!_cola.Activa) return false;

        bool encolado = await _cola.EncolarAsync(msg, "smtp", asunto, CancellationToken.None);
        if (encolado)
        {
            _logger.LogWarning("SmtpService: '{Asunto}' no salió ahora; quedó en la cola de correo para reintentarse.", asunto);
        }
        else
        {
            _logger.LogError("SmtpService: '{Asunto}' no salió y tampoco se pudo guardar en la cola de correo.", asunto);
        }
        return false;
    }
}
