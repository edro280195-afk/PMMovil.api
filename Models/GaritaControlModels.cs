using System.ComponentModel.DataAnnotations;

namespace PMMovil.Api.Models;

/// <summary>Consulta paginada. Hasta incluye todo el día seleccionado.</summary>
public sealed class GaritaAuditoriaFiltro : IValidatableObject
{
    [Range(1, int.MaxValue)] public int? IdCuenta { get; set; }
    [MaxLength(30)] public string? Evento { get; set; }
    [MaxLength(24)] public string? Folio { get; set; }
    public DateTime? Desde { get; set; }
    public DateTime? Hasta { get; set; }
    [Range(1, 1000000)] public int Pagina { get; set; } = 1;
    [Range(1, 200)] public int TamanoPagina { get; set; } = 50;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Desde?.Date > Hasta?.Date)
            yield return new ValidationResult("La fecha inicial debe ser anterior o igual a la final.", [nameof(Desde)]);
        if (Hasta?.Date == DateTime.MaxValue.Date)
            yield return new ValidationResult("La fecha final está fuera del rango permitido.", [nameof(Hasta)]);
    }
}

public sealed class GaritaAuditoriaPagina
{
    public IReadOnlyList<GaritaAuditoriaItem> Items { get; set; } = [];
    public int Total { get; set; }
    public int PlacasNoCoinciden { get; set; }
    public int Pagina { get; set; }
    public int TamanoPagina { get; set; }
}

public sealed class GaritaAuditoriaItem : PreregistroAuditoriaItem
{
    public long Id { get; set; }
    public int? IdCuenta { get; set; }
}

public sealed class PreregistroFicha
{
    public string Folio { get; set; } = string.Empty;
    public PreregistroAdminItem? Registro { get; set; }
    public string? CasetaId { get; set; }
    public IReadOnlyList<GaritaTimelineEvento> Eventos { get; set; } = [];
    public IReadOnlyList<PreregistroUbicacionItem> Recorrido { get; set; } = [];
    public int TotalSenales { get; set; }
}

public sealed class GaritaTimelineEvento
{
    public string Id { get; set; } = string.Empty;
    public DateTime Fecha { get; set; }
    public string Evento { get; set; } = string.Empty;
    public string? Usuario { get; set; }
    public string? Detalle { get; set; }
}

/// <summary>Ficha de consulta, independiente de si la relación puede liberarse.</summary>
public sealed class CasetaFicha
{
    public CasetaEntradaDetalle? Entrada { get; set; }
    public CasetaDetalle? Relacion { get; set; }
    public string? CasetaId { get; set; }
    public string? Relno { get; set; }
    public DateTime? Arribo { get; set; }
    public DateTime? Salida { get; set; }
    public string? UsuarioSalida { get; set; }
    public bool PuedeConfirmar { get; set; }
    public string Motivo { get; set; } = string.Empty;
    public IReadOnlyList<GaritaFoto> Fotos { get; set; } = [];
}

public sealed class GaritaFoto
{
    public string Referencia { get; set; } = string.Empty;
    public int Indice { get; set; }
    public string Etapa { get; set; } = string.Empty;
}
