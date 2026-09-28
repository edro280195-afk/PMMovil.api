using System.Security.Cryptography;
using System.Text;

namespace PMMovil.Api.Services;

/// <summary>
/// Hash SHA-256 (hex, minúsculas) para los tokens de enlaces públicos que se
/// guardan en BD -- nunca se guarda el token en texto plano, para que un
/// volcado de pmmutileria no deje enlaces vigentes usables de inmediato.
/// Mismo algoritmo que ya usaba MonitorTvLinkService; factorizado aquí para
/// que EntradaShareService/RevisionShareService/ReportShareService lo
/// compartan en vez de reimplementarlo cada uno.
/// </summary>
internal static class ShareTokenHasher
{
    public static string Hash(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
