using System.Collections.Generic;
using System.Threading.Tasks;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public interface IAuthService
{
    Task<LoginResponse> AuthenticateAsync(LoginRequest request);

    /// <summary>
    /// Emite el mismo token que un login normal pero SIN contraseña, para un
    /// usuario cuya identidad ya se verificó por otro medio. Hoy la usa nada
    /// más FaceService, después de ganar la búsqueda 1:N contra el banco de
    /// rostros.
    ///
    /// No existe ningún endpoint que la exponga, y no debe existir: cualquiera
    /// que la pueda llamar con un nombre de usuario entra como esa persona.
    /// </summary>
    Task<LoginResponse> AuthenticateByUsernameAsync(string claveusuario);

    /// <summary>
    /// Arma el mismo payload "usuario;pass_qr" que hoy va impreso en el QR del
    /// gafete, para que la app pueda escribirlo en una etiqueta NFC. Nunca
    /// expone la contraseña real de acceso -- pass_qr es una columna aparte.
    /// Devuelve null si el usuario no existe o no tiene gafete QR asignado.
    /// </summary>
    Task<string?> GetGafetePayloadAsync(string username);
}
