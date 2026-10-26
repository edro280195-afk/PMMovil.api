using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PMMovil.Api.Services;

/// <summary>Fila de <c>traftiemposrevision</c> con las fechas ya saneadas (null cuando vienen vacías o en cero).</summary>
public sealed record AuditSessionRow(
    DateTime? Inicio,
    DateTime? Fin,
    DateTime? TomaDocumento,
    int EstatusFinal,
    string Cerro);

/// <summary>Estado de una entrada visto desde sus sesiones de revisión.</summary>
public sealed record AuditSessionSummary(
    string Estado,
    int Sesiones,
    long Segundos,
    string CerradaPor,
    DateTime? CerradaEn);

/// <summary>
/// Reglas puras de la auditoría de revisiones (Historial y auditoría del
/// panel). Viven aparte del servicio para poder probarse sin base de datos.
/// </summary>
public static class RevisionAuditRules
{
    /// <summary>Días máximos que una consulta puede abarcar. Protege a la bitácora de rangos enormes.</summary>
    public const int MaxRangeDays = 93;

    /// <summary>
    /// Latido técnico de presencia que el hub y el escáner guardan además del
    /// movimiento real; repite lo que ya registran los demás tipos, así que
    /// nunca se cuenta ni se muestra en la auditoría.
    /// </summary>
    public static readonly IReadOnlyList<string> TechnicalTypes = new[]
    {
        RevisionActivityTypes.CambioActividad,
    };

    /// <summary>
    /// Movimientos de muy alto volumen (cada campo capturado, cada código
    /// leído). Solo se listan cuando se pide el detalle completo; en el
    /// resumen se cuentan aparte.
    /// </summary>
    public static readonly IReadOnlyList<string> DetailOnlyTypes = new[]
    {
        RevisionActivityTypes.CampoFactura,
        RevisionActivityTypes.CampoPartida,
        RevisionActivityTypes.LecturaCodigo,
    };

    /// <summary>Origen con el que el sistema registra lo que hace por su cuenta (cierres y pausas automáticas).</summary>
    public const string SystemOrigin = "SISTEMA";

    private const int OpenSessionFreshHours = 18;
    private const int DocumentsFreshHours = 1;

    private static readonly HashSet<string> LowercaseParticles = new(StringComparer.OrdinalIgnoreCase)
    {
        "de", "del", "la", "las", "los", "y", "e", "da", "di", "van", "von",
    };

    /// <summary>
    /// Convierte un rango de días completos (ambos extremos incluidos) en el
    /// intervalo [inicio, fin) que usan las consultas. Si falta un extremo se
    /// usa el otro; si faltan los dos, el día de hoy.
    /// </summary>
    /// <exception cref="ArgumentException">El rango está invertido o pasa de <see cref="MaxRangeDays"/> días.</exception>
    public static (DateTime Start, DateTime EndExclusive) NormalizeRange(DateOnly? from, DateOnly? to, DateOnly today)
    {
        DateOnly start = from ?? to ?? today;
        DateOnly end = to ?? from ?? today;

        if (end < start)
        {
            throw new ArgumentException("La fecha final no puede ser anterior a la inicial.");
        }

        if (end.DayNumber - start.DayNumber + 1 > MaxRangeDays)
        {
            throw new ArgumentException($"El rango no puede pasar de {MaxRangeDays} días.");
        }

        return (start.ToDateTime(TimeOnly.MinValue), end.AddDays(1).ToDateTime(TimeOnly.MinValue));
    }

    /// <summary>
    /// La clave de usuario viene de columnas heredadas de FoxPro, varias con
    /// espacios al final ("rrojas          "). Toda comparación y todo valor
    /// que sale hacia el cliente pasa por aquí.
    /// </summary>
    public static string TrimKey(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>
    /// Cuentas que no son personas: los cierres automáticos ("AUTO-17H",
    /// "AUTO-INSTANT"), el vencimiento de permisos y el propio sistema.
    /// </summary>
    public static bool IsSystemUser(string? usuario)
    {
        string key = TrimKey(usuario);
        return key.Equals("AUTO", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("AUTO-", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("SYSTEM", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SISTEMA", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "Nombre Apellido Apellido" en mayúsculas y minúsculas normales. Los
    /// datos llegan mezclados ("MADRIGAL", "Sanchez", "RENE") y con relleno.
    /// </summary>
    public static string FormatDisplayName(string? nombre, string? apaterno, string? amaterno)
    {
        string[] words = new[] { nombre, apaterno, amaterno }
            .SelectMany(part => (part ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        StringBuilder result = new();
        for (int index = 0; index < words.Length; index++)
        {
            if (index > 0)
            {
                result.Append(' ');
            }

            string word = words[index];
            result.Append(index > 0 && LowercaseParticles.Contains(word)
                ? word.ToLower(CultureInfo.GetCultureInfo("es-MX"))
                : Capitalize(word));
        }

        return result.ToString();
    }

    /// <summary>
    /// Resume las filas de <c>traftiemposrevision</c> de una entrada con las
    /// mismas reglas del Monitor: una sesión abierta solo cuenta como "en
    /// revisión" si arrancó hace menos de 18 horas (después es una sesión
    /// olvidada), y el tiempo suma únicamente sesiones cerradas válidas.
    /// </summary>
    public static AuditSessionSummary SummarizeSessions(IReadOnlyCollection<AuditSessionRow> rows, DateTime now)
    {
        if (rows.Count == 0)
        {
            return new AuditSessionSummary("SIN_REVISION", 0, 0, string.Empty, null);
        }

        int sessions = 0;
        long seconds = 0;
        foreach (AuditSessionRow row in rows)
        {
            if (RevisionTimeRules.IsValidClosedSession(row.Inicio, row.Fin, now))
            {
                sessions++;
                seconds += (long)(row.Fin!.Value - row.Inicio!.Value).TotalSeconds;
            }
        }

        AuditSessionRow? closing = rows
            .Where(row => row.Fin.HasValue && row.EstatusFinal == 1)
            .OrderByDescending(row => row.Fin)
            .FirstOrDefault();

        bool inReview = rows.Any(row => row.Inicio.HasValue && !row.Fin.HasValue && row.Inicio.Value >= now.AddHours(-OpenSessionFreshHours));
        bool documentsTaken = rows.Any(row => row.TomaDocumento.HasValue && !row.Fin.HasValue && row.TomaDocumento.Value >= now.AddHours(-DocumentsFreshHours));

        string state = inReview ? "EN_REVISION"
            : documentsTaken ? "DOCUMENTOS_TOMADOS"
            : closing is not null ? "FINALIZADA"
            : "PAUSADA";

        return new AuditSessionSummary(state, sessions, seconds, closing?.Cerro ?? string.Empty, closing?.Fin);
    }

    private static string Capitalize(string word)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo("es-MX");
        StringBuilder result = new(word.Length);
        bool startOfWord = true;

        foreach (char letter in word)
        {
            result.Append(startOfWord ? char.ToUpper(letter, culture) : char.ToLower(letter, culture));
            startOfWord = letter is '-' or '\'' or '.';
        }

        return result.ToString();
    }
}
