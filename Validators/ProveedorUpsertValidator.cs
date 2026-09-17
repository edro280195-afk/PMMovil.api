using FluentValidation;

using PMMovil.Api.Models;

namespace PMMovil.Api.Validators;

public class ProveedorUpsertValidator : AbstractValidator<ProveedorUpsertRequest>
{
    public ProveedorUpsertValidator()
    {
        RuleFor(x => x.IdProveedorLegacy)
        .NotEmpty().WithMessage("La clave del proveedor es requerida.")
        .MaximumLength(10);

        RuleFor(x => x.Nombre)
        .NotEmpty().WithMessage("El nombre del proveedor es requerido.")
        .MaximumLength(100);

        RuleFor(x => x.Domicilio).MaximumLength(80);
        RuleFor(x => x.NumExterior).MaximumLength(10);
        RuleFor(x => x.NumInterior).MaximumLength(10);
        RuleFor(x => x.Colonia).MaximumLength(80);
        RuleFor(x => x.Ciudad).MaximumLength(80);
        RuleFor(x => x.StateId).MaximumLength(3);
        RuleFor(x => x.IdPais).MaximumLength(3);
        RuleFor(x => x.ExtContacto).MaximumLength(5);
        RuleFor(x => x.Fax).MaximumLength(20);
        RuleFor(x => x.Contacto).MaximumLength(30);
        RuleFor(x => x.Email).MaximumLength(50);
        RuleFor(x => x.ProgramaImex).MaximumLength(10);
        RuleFor(x => x.Notas).MaximumLength(650);

        RuleFor(x => x.Email)
        .EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email))
        .WithMessage("El correo electronico no es valido.");

        RuleFor(x => x.CodigoPostal)
        .MaximumLength(10);

        RuleFor(x => x.Telefono)
        .MaximumLength(20);

        RuleFor(x => x.TaxId)
        .MaximumLength(30);
    }
}
