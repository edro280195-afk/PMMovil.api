using System;
using System.Collections.Generic;
using System.Linq;

namespace PMMovil.Api.Services;

/// <summary>Una persona de la tabla <c>usuarios</c> ya limpia (sin relleno, con nombre legible).</summary>
public sealed record AuditUserInfo(
    string Usuario,
    string Nombre,
    string Area,
    bool Activo);

/// <summary>
/// Directorio en memoria de usuarios para resolver nombres y para saber
/// quién es revisador. Revisador es quien está en <see cref="ReviewerRoster"/>.
/// </summary>
public sealed class AuditUserDirectory
{
    private readonly Dictionary<string, AuditUserInfo> _byKey;
    private readonly HashSet<string> _reviewerKeys;

    private AuditUserDirectory(Dictionary<string, AuditUserInfo> byKey, HashSet<string> reviewerKeys)
    {
        _byKey = byKey;
        _reviewerKeys = reviewerKeys;

        Reviewers = reviewerKeys
            .Select(key => byKey.TryGetValue(key, out AuditUserInfo? user)
                ? user
                // Está en la lista pero no en usuarios (cuenta nueva, error de captura):
                // se conserva para poder auditarlo, mostrando su clave como nombre.
                : new AuditUserInfo(key, key, string.Empty, true))
            .OrderByDescending(user => user.Activo)
            .ThenBy(user => user.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(user => user.Usuario, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Revisadores en activo primero y, después, los dados de baja; cada grupo por nombre.</summary>
    public IReadOnlyList<AuditUserInfo> Reviewers { get; }

    /// <summary>
    /// Si la misma clave aparece más de una vez (por ejemplo una cuenta dada
    /// de baja y otra nueva), gana la cuenta activa.
    /// </summary>
    public static AuditUserDirectory Build(IEnumerable<AuditUserInfo> users, IEnumerable<string> reviewerKeys)
    {
        Dictionary<string, AuditUserInfo> byKey = new(StringComparer.OrdinalIgnoreCase);

        foreach (AuditUserInfo user in users)
        {
            string key = RevisionAuditRules.TrimKey(user.Usuario);
            if (key.Length == 0)
            {
                continue;
            }

            AuditUserInfo clean = user with { Usuario = key };
            if (!byKey.TryGetValue(key, out AuditUserInfo? current) || (clean.Activo && !current.Activo))
            {
                byKey[key] = clean;
            }
        }

        HashSet<string> reviewers = new(
            reviewerKeys.Select(RevisionAuditRules.TrimKey).Where(key => key.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        return new AuditUserDirectory(byKey, reviewers);
    }

    public AuditUserInfo? Find(string? usuario)
    {
        string key = RevisionAuditRules.TrimKey(usuario);
        return key.Length > 0 && _byKey.TryGetValue(key, out AuditUserInfo? user) ? user : null;
    }

    /// <summary>Nombre de la persona o cadena vacía si la clave no está en <c>usuarios</c> (sistema, cuentas viejas).</summary>
    public string NameOf(string? usuario) => Find(usuario)?.Nombre ?? string.Empty;

    public bool IsReviewer(string? usuario) => _reviewerKeys.Contains(RevisionAuditRules.TrimKey(usuario));
}
