using System;

namespace PMMovil.Api.Services;

/// <summary>
/// Regla única para decidir qué sesiones cerradas pueden alimentar el tiempo
/// de una revisión. La tabla histórica contiene filas de toma de documento,
/// cierres sin inicio y sesiones que quedaron abiertas durante días; ninguna
/// de esas filas debe inflar el acumulado operativo.
/// </summary>
internal static class RevisionTimeRules
{
    public const int MinutosMinimos = 1;
    public const int MinutosMaximos = 240;

    /// <summary>
    /// Fragmento SQL para una fila de traftiemposrevision ya cerrada.
    /// El alias se recibe desde código controlado, nunca desde la petición.
    /// </summary>
    public static string ClosedSessionPredicate(string alias = "")
    {
        string prefix = string.IsNullOrWhiteSpace(alias) ? string.Empty : $"{alias}.";
        return $@"
            {prefix}finiciorevision IS NOT NULL
            AND YEAR({prefix}finiciorevision) >= 2000
            AND {prefix}ffinrevision IS NOT NULL
            AND YEAR({prefix}ffinrevision) >= 2000
            AND {prefix}ffinrevision >= {prefix}finiciorevision
            AND {prefix}ffinrevision <= NOW()
            AND TIMESTAMPDIFF(MINUTE, {prefix}finiciorevision, {prefix}ffinrevision)
                BETWEEN {MinutosMinimos} AND {MinutosMaximos}";
    }

    // Se mantiene separada del SQL para que la regla pueda probarse sin una
    // conexión a MySQL y para documentar el contrato temporal en C#.
    public static bool IsValidClosedSession(DateTime? start, DateTime? finish, DateTime now)
    {
        if (!start.HasValue || !finish.HasValue || finish.Value < start.Value || finish.Value > now)
        {
            return false;
        }

        double minutes = (finish.Value - start.Value).TotalMinutes;
        return minutes >= MinutosMinimos && minutes <= MinutosMaximos;
    }
}
