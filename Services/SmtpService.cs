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

    public SmtpService(IConfiguration config, ILogger<SmtpService> logger, IHostEnvironment env)
    {
        _config = config;
        _logger = logger;
        _env = env;
    }

    public async Task<bool> EnviarAsync(
        IEnumerable<string> destinatarios,
        string asunto,
        string cuerpoHtml,
        IEnumerable<EmailAttachment>? adjuntos = null,
        IEnumerable<string>? copias = null,
        IEnumerable<string>? copiasOcultas = null,
        bool incluirCopiasOcultasAutomaticas = true,
        CancellationToken ct = default(CancellationToken))
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
        try
        {
            MimeMessage msg = new MimeMessage
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
            using SmtpClient client = new SmtpClient();
            client.Timeout = 10000;

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
                return false;
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
                    return false;
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
            return false;
        }
    }
}
