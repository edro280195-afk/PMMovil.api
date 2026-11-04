using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PMMovil.Api.Services;

/// <summary>
/// Una entrada tal como la ve el buscador: lo mínimo para encontrarla. El estado real (facturas, partidas, quién la
/// revisa) NO vive aquí; se consulta solo para los pocos resultados que se devuelven.
/// </summary>
public sealed class EntrySearchEntry
{
    public EntrySearchEntry(string reference, string clientId, string clientName, DateTime date)
    {
        Reference = reference;
        ClientId = clientId;
        ClientName = clientName;
        Date = date;
        Key = EntrySearchRules.NormalizeKey(reference);
        ClientIdKey = EntrySearchRules.NormalizeKey(clientId);
        NameNormalized = EntrySearchRules.Normalize(clientName);
        Words = NameNormalized.Length == 0 ? Array.Empty<string>() : NameNormalized.Split(' ');
    }

    public string Reference { get; }
    public string ClientId { get; }
    public string ClientName { get; }
    public DateTime Date { get; }

    /// <summary>Referencia en mayúsculas y solo letras y dígitos.</summary>
    public string Key { get; }
    public string ClientIdKey { get; }
    public string NameNormalized { get; }
    public string[] Words { get; }
}

public sealed record EntrySearchHit(EntrySearchEntry Entry, int Score, bool Approximate);

public sealed record EntrySearchResult(IReadOnlyList<EntrySearchHit> Hits, int Total, bool Tolerant);

/// <summary>
/// Búsqueda difusa de entradas. Es pura y sin base de datos para poder probarla: acentos y mayúsculas no cuentan, una
/// parte de la referencia o del cliente basta, y con 6 caracteres o más tolera un error de tecleo (un dígito de más,
/// de menos, distinto o dos intercambiados). Es la misma regla que se validó en el mockup de la pantalla de Revisión.
/// </summary>
public static class EntrySearchRules
{
    /// <summary>Con menos de esto no se busca en todas las entradas: coincidiría con demasiadas.</summary>
    public const int MinChars = 3;

    public const int DefaultLimit = 8;
    public const int MaxLimit = 20;

    /// <summary>Con menos dígitos que esto, un error de tecleo ya coincide con muchas entradas al azar.</summary>
    public const int MinCharsForNumericTypo = 6;

    /// <summary>Con menos letras que esto no se tolera error en el nombre del cliente.</summary>
    public const int MinCharsForNameTypo = 5;

    private const int ScoreExact = 1000;
    private const int ScorePrefixOfReference = 900;
    private const int ScoreInReference = 700;
    private const int ScoreClientWordPrefix = 600;
    private const int ScoreClientId = 500;
    private const int ScoreInClientName = 420;
    private const int ScoreTypoInReference = 350;
    private const int ScoreTypoInName = 300;

    /// <summary>Quita acentos, pasa a mayúsculas y deja solo letras y dígitos separados por un espacio.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        string decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        bool pendingSpace = false;
        foreach (char c in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToUpperInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>Lo mismo pero sin espacios: la forma en que se guarda una referencia.</summary>
    public static string NormalizeKey(string? text) => Normalize(text).Replace(" ", string.Empty);

    public static string[] Tokens(string? query)
    {
        string normalized = Normalize(query);
        return normalized.Length == 0 ? Array.Empty<string>() : normalized.Split(' ');
    }

    /// <summary>Cuántos caracteres útiles trae la consulta, sin contar espacios.</summary>
    public static int UsefulChars(string? query) => Tokens(query).Sum(t => t.Length);

    public static int ClampLimit(int limit) => Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);

    /// <summary>
    /// Las mejores coincidencias de <paramref name="index"/> para <paramref name="query"/>. Primero se buscan solo
    /// coincidencias directas; la pasada tolerante a errores corre únicamente si no alcanzan para llenar el límite.
    /// </summary>
    public static EntrySearchResult Search(IReadOnlyList<EntrySearchEntry> index, string? query, int limit)
    {
        string[] tokens = Tokens(query);
        int cap = ClampLimit(limit);
        if (tokens.Length == 0 || UsefulChars(query) < MinChars)
        {
            return new EntrySearchResult(Array.Empty<EntrySearchHit>(), 0, false);
        }

        List<EntrySearchHit> hits = Collect(index, tokens, tolerant: false);
        bool tolerant = false;

        // Quien escribió o escaneó una referencia completa que existe no quiere ver «parecidas»: ya la encontró.
        bool foundExact = tokens.Length == 1 && hits.Any(h => h.Entry.Key == tokens[0]);
        if (hits.Count < cap && !foundExact)
        {
            hits = Collect(index, tokens, tolerant: true);
            tolerant = hits.Any(h => h.Approximate);   // «tolerante» solo si de verdad entró alguna con un error de tecleo
        }

        hits.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            int byDate = b.Entry.Date.CompareTo(a.Entry.Date);
            return byDate != 0 ? byDate : string.CompareOrdinal(a.Entry.Reference, b.Entry.Reference);
        });

        return new EntrySearchResult(hits.Take(cap).ToList(), hits.Count, tolerant);
    }

    private static List<EntrySearchHit> Collect(IReadOnlyList<EntrySearchEntry> index, string[] tokens, bool tolerant)
    {
        // Una referencia impresa en grupos («01C26 3607 34 0000») se teclea con espacios: además de buscar cada parte, se
        // prueba la consulta pegada como una sola referencia.
        string joined = tokens.Length > 1 ? string.Concat(tokens) : string.Empty;

        var hits = new List<EntrySearchHit>();
        foreach (EntrySearchEntry entry in index)
        {
            int total = 0;
            bool approximate = false;
            bool matchesAll = true;
            foreach (string token in tokens)
            {
                (int score, bool typo)? match = ScoreToken(token, entry, tolerant);
                if (match is null)
                {
                    matchesAll = false;
                    break;
                }
                total += match.Value.score;
                approximate |= match.Value.typo;
            }

            if (joined.Length >= MinChars)
            {
                (int score, bool typo)? asReference = ScoreReference(joined, entry, tolerant);
                if (asReference is not null && (!matchesAll || asReference.Value.score > total))
                {
                    matchesAll = true;
                    total = asReference.Value.score;
                    approximate = asReference.Value.typo;
                }
            }

            if (matchesAll) hits.Add(new EntrySearchHit(entry, total, approximate));
        }
        return hits;
    }

    /// <summary>La parte del puntaje que mira solo la referencia: exacta, por el inicio, en medio o con un error de tecleo.</summary>
    private static (int score, bool typo)? ScoreReference(string token, EntrySearchEntry entry, bool tolerant)
    {
        if (entry.Key == token) return (ScoreExact, false);

        if (token.Length >= MinChars)
        {
            int at = entry.Key.IndexOf(token, StringComparison.Ordinal);
            if (at == 0) return (ScorePrefixOfReference, false);
            if (at > 0) return (ScoreInReference - at, false);
        }

        if (tolerant && token.Length >= MinCharsForNumericTypo && token.Any(char.IsDigit)
            && MinDistanceInWindows(entry.Key, token) <= 1)
        {
            return (ScoreTypoInReference, true);
        }

        return null;
    }

    private static (int score, bool typo)? ScoreToken(string token, EntrySearchEntry entry, bool tolerant)
    {
        bool isNumber = token.All(char.IsDigit);

        (int score, bool typo)? inReference = ScoreReference(token, entry, tolerant: false);
        if (inReference is not null) return inReference;

        if (token.Length >= MinChars && entry.ClientIdKey.Length > 0 && entry.ClientIdKey == token)
        {
            return (ScoreClientId, false);
        }

        if (!isNumber)
        {
            foreach (string word in entry.Words)
            {
                if (word.StartsWith(token, StringComparison.Ordinal)) return (ScoreClientWordPrefix, false);
            }
            if (token.Length >= MinChars && entry.NameNormalized.Contains(token, StringComparison.Ordinal))
            {
                return (ScoreInClientName, false);
            }
        }

        if (tolerant)
        {
            bool hasDigit = token.Any(char.IsDigit);
            if (hasDigit)
            {
                if (token.Length >= MinCharsForNumericTypo && MinDistanceInWindows(entry.Key, token) <= 1)
                {
                    return (ScoreTypoInReference, true);
                }
            }
            else if (token.Length >= MinCharsForNameTypo)
            {
                foreach (string word in entry.Words)
                {
                    if (word.Length >= 4 && MinDistanceToPrefix(word, token) <= 1) return (ScoreTypoInName, true);
                }
            }
        }

        return null;
    }

    /// <summary>La menor distancia entre <paramref name="token"/> y cualquier tramo de <paramref name="text"/> de casi su largo.</summary>
    internal static int MinDistanceInWindows(string text, string token)
    {
        int best = int.MaxValue;
        for (int length = token.Length - 1; length <= token.Length + 1; length++)
        {
            if (length < 1 || length > text.Length) continue;
            for (int start = 0; start + length <= text.Length; start++)
            {
                int distance = Distance(text.AsSpan(start, length), token);
                if (distance < best) best = distance;
                if (best == 0) return 0;
            }
        }
        return best;
    }

    /// <summary>La menor distancia entre <paramref name="token"/> y el inicio de <paramref name="word"/> (con tolerancia de un carácter de largo).</summary>
    internal static int MinDistanceToPrefix(string word, string token)
    {
        int best = int.MaxValue;
        for (int length = token.Length - 1; length <= token.Length + 1; length++)
        {
            if (length < 1 || length > word.Length) continue;
            int distance = Distance(word.AsSpan(0, length), token);
            if (distance < best) best = distance;
        }
        return best;
    }

    /// <summary>Distancia de edición con intercambio de dos caracteres contiguos (cuenta como un solo error).</summary>
    public static int Distance(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int m = a.Length, n = b.Length;
        if (n > MaxTokenForTypo || m > MaxTokenForTypo + 2) return int.MaxValue;

        // Tres filas rodantes (anterior de la anterior, anterior y actual) en la pila: sin asignar memoria por llamada,
        // que se invoca decenas de miles de veces en una pasada tolerante.
        int width = n + 1;
        Span<int> rows = stackalloc int[3 * width];
        Span<int> beforePrevious = rows.Slice(0, width);
        Span<int> previous = rows.Slice(width, width);
        Span<int> current = rows.Slice(2 * width, width);

        for (int j = 0; j <= n; j++) previous[j] = j;
        for (int i = 1; i <= m; i++)
        {
            current[0] = i;
            for (int j = 1; j <= n; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, beforePrevious[j - 2] + 1);
                }
                current[j] = value;
            }
            Span<int> recycled = beforePrevious;
            beforePrevious = previous;
            previous = current;
            current = recycled;
        }
        return previous[n];
    }

    /// <summary>Un tramo más largo que esto no es un tecleo con error: no se compara.</summary>
    private const int MaxTokenForTypo = 40;
}
