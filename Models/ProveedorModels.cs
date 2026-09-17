using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PMMovil.Api.Models;

public class ConsignatarioProv
{
    public int FkIdProv { get; set; }
    public string? IdProveedorLegacy { get; set; }
    public int NLineno { get; set; }
    public string? CConsignaId { get; set; }
    public string? Nombre { get; set; }
}

public class Proveedor
{
    public int IdProv { get; set; }
    public string? IdProveedorLegacy { get; set; }
    public string? Nombre { get; set; }
    public string? Domicilio { get; set; }
    public string? NumExterior { get; set; }
    public string? NumInterior { get; set; }
    public string? Colonia { get; set; }
    public string? CodigoPostal { get; set; }
    public string? Ciudad { get; set; }
    public string? StateId { get; set; }
    public string? IdPais { get; set; }
    public string? Telefono { get; set; }
    public string? ExtContacto { get; set; }
    public string? Fax { get; set; }
    public string? Contacto { get; set; }
    public string? Email { get; set; }
    public string? TaxId { get; set; }
    public string? ProgramaImex { get; set; }
    public DateTime? FechaVigenciaImex { get; set; }
    public string? Notas { get; set; }
    public bool Inactivo { get; set; }
    public string? UsuarioCreacion { get; set; }
    public DateTime? FechaCreacion { get; set; }
    public string? UsuarioModificacion { get; set; }
    public DateTime? FechaModificacion { get; set; }
    public List<ConsignatarioProv>? Consignatarios { get; set; }
}

public class ProveedorListDto
{
    public int IdProv { get; set; }
    public string? IdProveedorLegacy { get; set; }
    public string? Nombre { get; set; }
    public string? Ciudad { get; set; }
    public string? StateId { get; set; }
    public string? IdPais { get; set; }
    public string? Telefono { get; set; }
    public string? Contacto { get; set; }
    public string? Email { get; set; }
    public string? TaxId { get; set; }
    public bool Inactivo { get; set; }
}

public class ProveedorUpsertRequest
{
    [Required(ErrorMessage = "La clave del proveedor es requerida.")]
    public string? IdProveedorLegacy { get; set; }

    [Required(ErrorMessage = "El nombre del proveedor es requerido.")]
    [MaxLength(100)]
    public string Nombre { get; set; } = string.Empty;

    public string? Domicilio { get; set; }
    public string? NumExterior { get; set; }
    public string? NumInterior { get; set; }
    public string? Colonia { get; set; }
    public string? CodigoPostal { get; set; }
    public string? Ciudad { get; set; }
    public string? StateId { get; set; }
    public string? IdPais { get; set; }
    public string? Telefono { get; set; }
    public string? ExtContacto { get; set; }
    public string? Fax { get; set; }
    public string? Contacto { get; set; }
    public string? Email { get; set; }
    public string? TaxId { get; set; }
    public string? ProgramaImex { get; set; }
    public DateTime? FechaVigenciaImex { get; set; }
    public string? Notas { get; set; }
    public bool Inactivo { get; set; }
}

public class ProveedoresResponse
{
    public int TotalRegistros { get; set; }
    public int PaginaActual { get; set; }
    public int TotalPaginas { get; set; }
    public List<ProveedorListDto> Proveedores { get; set; } = new List<ProveedorListDto>();
}
