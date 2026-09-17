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
        public string Planta { get; set; } = string.Empty;
        public bool IsCliente { get; set; }
        public string ClienteId { get; set; } = string.Empty;
        public string Redireccionamiento { get; set; } = string.Empty;

        /// <summary>
        /// True si el usuario tiene acceso total (bypassa permisos por
        /// módulo). Calculado UNA sola vez en el servidor -- ver
        /// <see cref="Services.ModulePermissionService"/>, que usa el mismo
        /// criterio -- para que Android no tenga que adivinarlo parseando
        /// <see cref="Area"/> con su propia copia de la regla. Antes de esto,
        /// PMMovil decidía "Sistemas" solo por Area (HARDWARE/SOFTWARE)
        /// mientras que PMMAdmin lo decidía solo por Departamento (=1,
        /// SISTEMAS): un usuario del departamento de Sistemas sin el campo
        /// Area bien llenado (p. ej. cparra, isabel.campos) quedaba marcado
        /// como acceso total en la página pero como usuario normal en el
        /// celular.
        /// </summary>
        public bool EsSistemas { get; set; }
    }

    public class ModuloPermisoDto
    {
        public int ModuloId { get; set; }
        public string Descripcion { get; set; } = string.Empty;
        public string FormularioWeb { get; set; } = string.Empty;
        public string IconW { get; set; } = string.Empty;
        public bool Lectura { get; set; }
        public bool Agregar { get; set; }
        public bool Modificar { get; set; }
        public bool Borrar { get; set; }
        public bool AccesoTotal { get; set; }
    }
}
