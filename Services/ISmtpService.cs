using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PMMovil.Api.Services;

/// <summary>Un archivo adjunto ya listo para mandar (nombre de envío, bytes y content-type).</summary>
public record EmailAttachment(string FileName, byte[] Content, string ContentType);

public interface ISmtpService
{
    Task<bool> EnviarAsync(
        IEnumerable<string> destinatarios,
        string asunto,
        string cuerpoHtml,
        IEnumerable<EmailAttachment>? adjuntos = null,
        IEnumerable<string>? copias = null,
        IEnumerable<string>? copiasOcultas = null,
        bool incluirCopiasOcultasAutomaticas = true,
        CancellationToken ct = default(CancellationToken),
        bool preferirCola = false);
}
