using System;
using System.Collections.Generic;

namespace PMMovil.Api.Services;

/// <summary>
/// El número de factura que imprime el proveedor (<c>fact.cnofact</c>) NO identifica una factura: el mismo número
/// vuelve a llegar en otra entrada (en 2026 hay 1,528 números repetidos en <c>fact</c>). Una petición que solo trae
/// el número, escrita como <c>WHERE cnofact = @cnofact</c>, modifica TODAS las facturas que lo comparten, aunque
/// sean de otro cliente y de otra entrada. Caso 01C263616880000: el revisor guardó la factura 90568 y las dos
/// facturas de enero de otro cliente quedaron con su hora de modificación, su revisor, sus pesos y sus marcas.
///
/// La relación correcta con las partidas es <c>revdet.idfactura = fact.idfactura</c> (es la que usa el propio
/// sistema de escritorio: <c>SELECT * FROM revdet WHERE idfactura = ?lcidfac</c>). Para las escrituras sobre
/// <c>fact</c> la app manda además la entrada (<c>crefAgrupada</c>); esta clase decide a qué entrada se refiere la
/// petición cuando una versión anterior de la app no la manda.
/// </summary>
public static class RevisionFacturaAlcance
{
    /// <summary>
    /// Devuelve la entrada a la que pertenece la factura de la petición, o <c>null</c> si no hay ninguna entrada
    /// agrupada con ese número (no se debe escribir nada).
    /// </summary>
    /// <param name="crefIndicado">La entrada que mandó la app. Si viene, manda siempre.</param>
    /// <param name="candidatasDeMasNuevaAMasVieja">Entradas agrupadas donde existe una factura con ese número.</param>
    /// <param name="entradaActivaDelUsuario">La entrada de la última sesión de revisión del usuario.</param>
    public static string? ElegirEntrada(
        string? crefIndicado,
        IReadOnlyList<string> candidatasDeMasNuevaAMasVieja,
        string? entradaActivaDelUsuario)
    {
        string indicado = (crefIndicado ?? string.Empty).Trim();
        if (indicado.Length > 0)
        {
            return indicado;
        }

        if (candidatasDeMasNuevaAMasVieja.Count == 0)
        {
            return null;
        }

        if (candidatasDeMasNuevaAMasVieja.Count == 1)
        {
            return candidatasDeMasNuevaAMasVieja[0];
        }

        // Versión anterior de la app y número repetido: la entrada en la que el usuario viene trabajando es la de
        // su última sesión; si no está entre las candidatas, la más reciente (la que se acaba de recibir).
        string activa = (entradaActivaDelUsuario ?? string.Empty).Trim();
        if (activa.Length > 0)
        {
            foreach (string candidata in candidatasDeMasNuevaAMasVieja)
            {
                if (string.Equals(candidata, activa, StringComparison.OrdinalIgnoreCase))
                {
                    return candidata;
                }
            }
        }

        return candidatasDeMasNuevaAMasVieja[0];
    }
}
