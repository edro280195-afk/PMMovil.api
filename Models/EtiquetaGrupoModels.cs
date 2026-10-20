using System.ComponentModel.DataAnnotations;

namespace PMMovil.Api.Models;

/// <summary>
/// Un grupo de clientes que comparten el mismo juego de etiquetas, y por tanto
/// el mismo perfil de lectura. La <see cref="Clave"/> es la que amarra con el
/// perfil que vive en el código de la app.
/// </summary>
public class EtiquetaGrupoCliente
{
    public int IdGrupo { get; set; }

    public string Clave { get; set; } = string.Empty;

    public string Nombre { get; set; } = string.Empty;

    public bool Activo { get; set; }

    public string? Notas { get; set; }

    public DateTime? FechaModificacion { get; set; }

    public string? UsuarioModificacion { get; set; }

    /// <summary>Los cclienteid que pertenecen al grupo, con el nombre del catálogo.</summary>
    public List<EtiquetaGrupoMiembro> Miembros { get; set; } = [];
}

public class EtiquetaGrupoMiembro
{
    public string Cidcte { get; set; } = string.Empty;

    /// <summary>Nombre del catálogo de clientes. Null si el id ya no existe ahí.</summary>
    public string? Nombre { get; set; }
}

/// <summary>
/// Alta o cambio de un grupo. Los miembros llegan completos: lo que no venga
/// en la lista se da de baja. Es deliberado — el formulario siempre manda el
/// conjunto que el usuario está viendo, así que una baja es quitar un renglón.
/// </summary>
public class EtiquetaGrupoGuardarRequest
{
    /// <summary>0 o ausente da de alta; con valor, modifica ese grupo.</summary>
    public int IdGrupo { get; set; }

    [Required, MaxLength(30)]
    public string Clave { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string Nombre { get; set; } = string.Empty;

    public bool Activo { get; set; } = true;

    [MaxLength(400)]
    public string? Notas { get; set; }

    public List<string> Clientes { get; set; } = [];
}

/// <summary>
/// Lo que la app necesita saber al abrir una entrada: si este cliente tiene
/// perfil de etiquetas y cuál es.
/// </summary>
public class EtiquetaPerfilClienteResponse
{
    public bool Aplica { get; set; }

    public string? Clave { get; set; }

    public string? Nombre { get; set; }
}

/// <summary>Un cliente del catálogo, para el buscador del formulario.</summary>
public class EtiquetaClienteCatalogoItem
{
    public string Cidcte { get; set; } = string.Empty;

    public string Nombre { get; set; } = string.Empty;

    /// <summary>Clave del grupo al que ya pertenece, si pertenece a alguno.</summary>
    public string? GrupoActual { get; set; }
}
