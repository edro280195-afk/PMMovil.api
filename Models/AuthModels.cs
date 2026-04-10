using System.ComponentModel.DataAnnotations;

namespace PMMovil.Api.Models
{
    public class LoginRequest
    {
        [Required]
        public string Username { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public bool IsQR { get; set; } = false;
    }

    public class LoginResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public UserData? User { get; set; }
    }

    public class UserData
    {
        public string IdUsuario { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Nombre { get; set; } = string.Empty;
        public string Area { get; set; } = string.Empty;
        public string Lugar { get; set; } = string.Empty;
        public bool IsCliente { get; set; }
        public string ClienteId { get; set; } = string.Empty;
        public string Redireccionamiento { get; set; } = string.Empty;
    }
}
