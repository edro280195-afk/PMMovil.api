using System.Collections.Generic;

namespace PMMovil.Api.Models;

/// <summary>Respuesta de <c>POST Asignaciones/tomar/{cref}</c>. <c>Codigo</c> es estable; <c>Mensaje</c> es para mostrar.</summary>
public sealed class TomarCarpetaResultado
{
    public bool Success { get; set; }

    /// <summary>
    /// TOMADA, YA_ERA_TUYA, TOMADA_POR_OTRO (hay que confirmar), EN_REVISION_POR_OTRO, EN_REVISION_PROPIA,
    /// YA_REVISADA, NO_EXISTE, OCUPADO o REFERENCIA_VACIA.
    /// </summary>
    public string Codigo { get; set; } = string.Empty;

    public string Mensaje { get; set; } = string.Empty;

    /// <summary>Quién la tiene (o la está revisando) cuando no se pudo tomar sin confirmar.</summary>
    public string? TomadaPor { get; set; }

    /// <summary>Cuándo se tomó (ISO, hora del servidor).</summary>
    public string? Desde { get; set; }

    public int? IdAsignacion { get; set; }
    public string? NombreCliente { get; set; }
}

/// <summary>Una carpeta que alguien tomó y todavía no empieza a revisar.</summary>
public sealed class CarpetaTomadaItem
{
    public int IdAsignacion { get; set; }
    public string CrefAgrupada { get; set; } = string.Empty;
    public string Usuario { get; set; } = string.Empty;
    public string NombreUsuario { get; set; } = string.Empty;
    public string NombreCliente { get; set; } = string.Empty;

    /// <summary>Cuándo la tomó (ISO, hora del servidor).</summary>
    public string TomadaEn { get; set; } = string.Empty;

    /// <summary>Segundos que lleva sin iniciar, medidos por el servidor (el reloj de la pantalla parte de aquí).</summary>
    public long SegundosSinIniciar { get; set; }

    /// <summary>OK, AVISO o CRITICO según los umbrales.</summary>
    public string Nivel { get; set; } = "OK";
}

/// <summary>Respuesta de <c>GET Monitor/carpetas-tomadas</c> (y de su versión para la TV).</summary>
public sealed class CarpetasTomadasResponse
{
    public bool Success { get; set; } = true;
    public int AvisoMin { get; set; }
    public int CriticoMin { get; set; }

    /// <summary>Hora del servidor al armar la respuesta (ISO); las pantallas calculan el reloj contra ella.</summary>
    public string ServerTime { get; set; } = string.Empty;

    public List<CarpetaTomadaItem> Items { get; set; } = new();
}
