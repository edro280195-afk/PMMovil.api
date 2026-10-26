using System;
using System.Collections.Generic;
using System.Linq;

namespace PMMovil.Api.Services;

/// <summary>
/// Lista oficial de revisadores: las únicas personas que revisan mercancía.
///
/// La indicó Eduardo el 2026-10-05 ("los revisadores reales y únicos"). No se
/// deduce del departamento de <c>usuarios</c> porque ese dato no es confiable
/// para esto: el departamento 12 (Revisión) incluye a gente que no revisa y
/// deja fuera a <c>alarumbe</c> y <c>eder.magana</c>, que están en el 7.
///
/// Agregar o quitar a alguien es editar esta lista. Quien deje de revisar
/// puede permanecer aquí para seguir auditando su historial.
/// </summary>
public static class ReviewerRoster
{
    private static readonly string[] Keys =
    {
        "alarumbe",
        "jose.mercado",
        "fmadrigal",
        "kevin.montiel",
        "shinojosa",
        "israel.magana",
        "eder.magana",
    };

    private static readonly HashSet<string> KeySet = new(Keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>Claves de usuario de los revisadores, tal como se guardan en <c>usuarios.claveusuario</c> (sin relleno).</summary>
    public static IReadOnlyList<string> All => Keys;

    public static bool Contains(string? usuario) => KeySet.Contains(RevisionAuditRules.TrimKey(usuario));
}
