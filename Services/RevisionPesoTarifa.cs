using System;
using System.Collections.Generic;

namespace PMMovil.Api.Services;

/// <summary>
/// Regla de Revisión normal: cada vez que se captura el peso de una partida (Peso lb o Peso kg en la pantalla), la
/// cantidad tarifa (<c>revdet.ncanttarifa</c>) se llena con el valor del peso que corresponde a la UMT elegida
/// (<c>revdet.cunidadtarifa</c>): si la UMT es KG va el peso en kilos, si es LB va el peso en libras. Con cualquier
/// otra UMT (litros, piezas, metros...) el peso no define la cantidad tarifa y no se toca.
///
/// <c>revdet.npeso</c> se guarda SIEMPRE en libras (la pantalla convierte lo que se captura en kilos antes de enviarlo),
/// así que los kilos se derivan con 0.4536, el mismo factor de la pantalla y de la regla legacy de 5 clientes.
/// Es una función pura para poder probarla con la consola de regresión, sin base de datos.
/// </summary>
public static class RevisionPesoTarifa
{
    /// <summary>Kilos por libra: el factor de la pantalla de Revisión y de la regla legacy (GGD 14-08-17).</summary>
    public const decimal KilosPorLibra = 0.4536m;

    /// <summary>Decimales de <c>ncanttarifa</c>: los mismos que usa la regla legacy de los 5 clientes.</summary>
    public const int Decimales = 3;

    // Se aceptan las variantes comunes del código de la unidad (revdet.cunidadtarifa viene de catálogos distintos).
    private static readonly HashSet<string> UmtKilos = new(StringComparer.OrdinalIgnoreCase)
    {
        "KG", "KGM", "KILO", "KILOS", "KILOGRAMO", "KILOGRAMOS"
    };

    private static readonly HashSet<string> UmtLibras = new(StringComparer.OrdinalIgnoreCase)
    {
        "LB", "LBS", "LBR", "LIBRA", "LIBRAS"
    };

    /// <summary>
    /// Cantidad tarifa que le toca a una partida cuyo peso (en libras) acaba de cambiar.
    /// </summary>
    /// <param name="umt">La UMT elegida (<c>revdet.cunidadtarifa</c>); se ignoran espacios y mayúsculas.</param>
    /// <param name="libras">El peso capturado, en libras (<c>revdet.npeso</c>).</param>
    /// <returns>El valor para <c>ncanttarifa</c>, o <c>null</c> si la UMT no es de peso y no se debe tocar.</returns>
    public static decimal? CantidadTarifaPorPeso(string? umt, decimal libras)
    {
        string unidad = (umt ?? string.Empty).Trim();

        if (UmtKilos.Contains(unidad))
        {
            return Math.Round(libras * KilosPorLibra, Decimales);
        }

        if (UmtLibras.Contains(unidad))
        {
            return Math.Round(libras, Decimales);
        }

        return null;
    }
}
