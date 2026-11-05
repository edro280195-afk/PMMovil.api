using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>Resultado de reconocer un código por su forma, sin consultar la base.</summary>
public sealed record TraceParsedCode
{
    /// <summary>Qué clase de código es (<see cref="TraceKind"/>); vacío si no se reconoció.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Código normalizado. Vacío en el gafete: su valor jamás sale de aquí.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Motivo por el que no se reconoció (<see cref="TraceReason"/>); <c>null</c> si se reconoció.</summary>
    public string? Rejection { get; init; }

    /// <summary>Referencia de entrada de 15 caracteres (carpeta y bulto).</summary>
    public string Reference { get; init; } = string.Empty;

    public int Ordinal { get; init; }
    public int Total { get; init; }

    /// <summary>Formas con las que el folio de la caja puede estar guardado (ver <see cref="TrazabilidadRules.CasetaIdCandidates"/>).</summary>
    public IReadOnlyList<string> CasetaIdCandidates { get; init; } = Array.Empty<string>();

    public string PreFolio { get; init; } = string.Empty;
    public string PreToken { get; init; } = string.Empty;

    public bool IsRecognized => Rejection is null;
}

/// <summary>
/// Lo que se sabe de la cadena de un código una vez leída la base. Es la entrada de las reglas de estado:
/// ninguna regla consulta nada por su cuenta.
/// </summary>
public sealed class TraceFacts
{
    public string Kind { get; set; } = string.Empty;

    public bool HasCaja { get; set; }
    public DateTime? CajaArrival { get; set; }
    public DateTime? CajaDeparture { get; set; }
    public bool CajaCancelled { get; set; }

    public bool HasRelacion { get; set; }
    public string RelacionStatus { get; set; } = string.Empty;
    public DateTime? RelacionAuthorizedAt { get; set; }
    public DateTime? RelacionDeparture { get; set; }
    public bool RelacionBusy { get; set; }

    /// <summary>Cuándo se empezó a cargar la relación (<c>inicio_carga</c>).</summary>
    public DateTime? LoadStartedAt { get; set; }

    /// <summary>Cuándo se terminó de cargar (<c>fin_carga</c>).</summary>
    public DateTime? LoadFinishedAt { get; set; }

    public int EntradasCount { get; set; }
    public bool EntradasTruncated { get; set; }

    /// <summary>Hay al menos una entrada ligada a la caja por <c>traf3.ccasetaid</c>.</summary>
    public bool HasDirectLink { get; set; }

    /// <summary>Hay al menos una entrada que se encontró por la relación y no por el enlace directo.</summary>
    public bool HasLinkByRelacion { get; set; }

    public DateTime? EntradaArrival { get; set; }
    public IReadOnlyList<string> ReviewStatuses { get; set; } = Array.Empty<string>();
    public bool HasDamage { get; set; }
    public bool HasHazmat { get; set; }

    public string PreStatus { get; set; } = string.Empty;
}

/// <summary>
/// Reglas puras de la ficha de un código: reconocerlo por su forma y convertir los hechos de la base en el estado
/// de las cinco estaciones, la señal, las banderas y las acciones. No toca la base; se prueba sin ella.
/// </summary>
public static class TrazabilidadRules
{
    public const string PreRegistroPrefix = "PMMPR|";
    private const int MaxCodeLength = 120;

    /// <summary>Caja de caseta: <c>E-17846/26</c> (sin ceros) o <c>E-017846/26</c> (con el cero de la plantilla vieja).</summary>
    private static readonly Regex CajaShape = new(@"^E-(\d{3,6})/(\d{2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RelacionShape = new(@"^\d{2}[IEC]\d{8}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EntradaShape = new(@"^\d{2}[A-Z0-9]{13}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BultoShape = new(@"^(\S+)\s+(\d{1,6})-(\d{1,6})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Deja el código como se compara contra la base: sin caracteres de control, con espacios simples y en mayúsculas.
    /// También deshace una diagonal que llegó escapada (<c>E-017846%2F26</c>). El pre-registro conserva sus
    /// mayúsculas y minúsculas porque su token se compara tal cual.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var builder = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (char.IsControl(c)) continue;
            builder.Append(c == ' ' ? ' ' : c);
        }

        string text = Regex.Replace(builder.ToString().Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
        text = text.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

        // El prefijo se acepta en cualquier capitalización, pero lo que sigue (folio y token) queda como llegó.
        return text.StartsWith(PreRegistroPrefix, StringComparison.OrdinalIgnoreCase)
            ? PreRegistroPrefix + text[PreRegistroPrefix.Length..]
            : text.ToUpperInvariant();
    }

    /// <summary>
    /// Reconoce qué es un código leído. El gafete (<c>usuario;clave</c>) lleva una credencial: se rechaza antes de
    /// cualquier otra cosa y su valor no vuelve en el resultado. Lo que no tenga la forma de nada que PMM imprima
    /// se rechaza también; la ficha solo cubre caja, relación, carpeta, bulto y pre-registro.
    /// </summary>
    public static TraceParsedCode Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new TraceParsedCode { Rejection = TraceReason.Empty };
        if (raw.Contains(';')) return new TraceParsedCode { Rejection = TraceReason.BadgeNotLookedUp };

        string code = Normalize(raw);
        if (code.Length == 0) return new TraceParsedCode { Rejection = TraceReason.Empty };
        if (code.Length > MaxCodeLength) return new TraceParsedCode { Rejection = TraceReason.Unsupported };

        if (code.StartsWith(PreRegistroPrefix, StringComparison.Ordinal))
        {
            // Aquí el prefijo ya viene normalizado; el folio y el token se comparan tal cual.
            string[] parts = code.Split('|');
            if (parts.Length != 3 || parts[1].Trim().Length == 0 || parts[2].Trim().Length == 0)
                return new TraceParsedCode { Rejection = TraceReason.Unsupported };

            return new TraceParsedCode
            {
                Kind = TraceKind.PreRegistro,
                Code = $"{PreRegistroPrefix}{parts[1].Trim()}|{parts[2].Trim()}",
                PreFolio = parts[1].Trim(),
                PreToken = parts[2].Trim()
            };
        }

        Match caja = CajaShape.Match(code);
        if (caja.Success)
        {
            return new TraceParsedCode
            {
                Kind = TraceKind.Caja,
                Code = code,
                CasetaIdCandidates = CasetaIdCandidates(code)
            };
        }

        if (RelacionShape.IsMatch(code)) return new TraceParsedCode { Kind = TraceKind.Relacion, Code = code };

        Match position = BultoShape.Match(code);
        string reference = position.Success ? position.Groups[1].Value : code;
        if (!EntradaShape.IsMatch(reference)) return new TraceParsedCode { Rejection = TraceReason.Unsupported };

        if (!position.Success) return new TraceParsedCode { Kind = TraceKind.Carpeta, Code = reference, Reference = reference };

        bool okOrdinal = int.TryParse(position.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal);
        bool okTotal = int.TryParse(position.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int total);

        // Una posición mayor al total o un total en cero es una lectura mutilada: no se acepta un bulto que no se puede ubicar.
        if (!okOrdinal || !okTotal || total < 1 || ordinal < 1 || ordinal > total)
            return new TraceParsedCode { Rejection = TraceReason.Unsupported };

        return new TraceParsedCode
        {
            Kind = TraceKind.Bulto,
            Code = $"{reference} {ordinal}-{total}",
            Reference = reference,
            Ordinal = ordinal,
            Total = total
        };
    }

    /// <summary>
    /// Formas en que puede estar guardado el folio de una caja. La plantilla vieja armaba <c>E-0</c> + número
    /// (<c>E-017846/26</c>) y la app nueva lo guarda sin ese cero (<c>E-17846/26</c>); en producción hay de las dos y
    /// ninguna pareja distinta comparte número, así que se busca por las dos. La diagonal se conserva tal cual:
    /// forma parte del folio y no separa nada.
    /// </summary>
    public static IReadOnlyList<string> CasetaIdCandidates(string normalizedCode)
    {
        Match match = CajaShape.Match(normalizedCode);
        if (!match.Success) return Array.Empty<string>();

        string digits = match.Groups[1].Value;
        string year = match.Groups[2].Value;
        string trimmed = digits.TrimStart('0');
        if (trimmed.Length == 0) trimmed = "0";

        // El código tal como vino va primero: es el que se prefiere si hubiera dos filas.
        return new[]
        {
            normalizedCode,
            $"E-{trimmed}/{year}",
            $"E-0{trimmed}/{year}"
        }.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>La relación con tercer carácter <c>C</c> vive en <c>relacion_imex</c>; cualquier otra en <c>relacion</c>.</summary>
    public static string RelationTable(string relno)
        => relno.Length > 2 && relno[2] == 'C' ? "relacion_imex" : "relacion";

    /// <summary>Una caja tiene relación si el campo no está vacío ni trae el marcador de «sin relación».</summary>
    public static bool HasRelationValue(string? relacion)
        => !string.IsNullOrWhiteSpace(relacion)
           && !string.Equals(relacion.Trim(), "NO_RELACION", StringComparison.OrdinalIgnoreCase);

    /// <summary>Las fechas en cero que guarda el sistema viejo se tratan como «sin fecha».</summary>
    public static bool IsRealDate(DateTime? value) => value.HasValue && value.Value.Year >= 2000;

    public static string? FormatDate(DateTime? value)
        => IsRealDate(value) ? value!.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) : null;

    /// <summary>La relación ya cargó y salió (o la caja ya salió): el recorrido físico terminó.</summary>
    public static bool IsDeparted(TraceFacts facts)
        => IsRealDate(facts.CajaDeparture)
           || string.Equals(facts.RelacionStatus, "SALIDA", StringComparison.OrdinalIgnoreCase)
           || IsRealDate(facts.RelacionDeparture);

    public static bool IsAuthorized(TraceFacts facts)
        => string.Equals(facts.RelacionStatus, "AUTORIZADA", StringComparison.OrdinalIgnoreCase);

    // ── Estaciones ──────────────────────────────────────────────────────────

    /// <summary>
    /// Las seis estaciones con su estado. <c>SIN_DATO</c> se reserva para lo que no se puede saber: hoy Bodega no
    /// liga la entrada a la caja, así que una caja sin relación no se marca «sin entrada» (podría tenerla), se marca
    /// «sin dato».
    /// </summary>
    public static List<TraceStation> BuildStations(TraceFacts facts)
    {
        bool departed = IsDeparted(facts);

        return new List<TraceStation>
        {
            new() { Id = TraceStationId.Caseta, State = CasetaState(facts), At = FormatDate(facts.CajaArrival) },
            new() { Id = TraceStationId.Entrada, State = EntradaState(facts), At = FormatDate(facts.EntradaArrival) },
            new() { Id = TraceStationId.Revision, State = RevisionState(facts) },
            new() { Id = TraceStationId.Carga, State = CargaState(facts, departed), At = FormatDate(CargaDate(facts)) },
            new() { Id = TraceStationId.Relacion, State = RelacionState(facts, departed), At = FormatDate(facts.RelacionAuthorizedAt) },
            new() { Id = TraceStationId.Salida, State = SalidaState(facts, departed), At = FormatDate(DepartureDate(facts)) }
        };
    }

    // `estatus_carga` de la relación (tablas relacion / relacion_imex). Los valores salen de SalidaReglas.
    private static string LoadStatus(TraceFacts facts)
        => (facts.RelacionStatus ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>La carga terminó: el cargador cerró la relación (aunque falte que el supervisor la autorice).</summary>
    public static bool IsLoadFinished(TraceFacts facts)
        => LoadStatus(facts) is "CARGADA" or "DEMASIADO RAPIDO" or "AUTORIZADA" or "SALIDA";

    /// <summary>
    /// Falta cargar y el cargador puede hacerlo: la relación existe, no ha salido y está sin empezar, cargándose o en
    /// espera. Bloqueada o incompleta la resuelve el supervisor, no el cargador, y por eso no se ofrece cargarla.
    /// </summary>
    public static bool IsLoadPending(TraceFacts facts)
        => facts.HasRelacion && !IsDeparted(facts) && (LoadStatus(facts) is "" or "CARGANDO" or "EN ESPERA");

    private static DateTime? CargaDate(TraceFacts facts)
        => IsRealDate(facts.LoadFinishedAt) ? facts.LoadFinishedAt
            : IsRealDate(facts.LoadStartedAt) ? facts.LoadStartedAt
            : null;

    /// <summary>
    /// Cargar mercancía. Hecha cuando el cargador cerró la relación (<c>CARGADA</c>) o ya pasó de ahí; en curso mientras
    /// se carga o está en espera; atención si quedó bloqueada o incompleta (pide al supervisor); pendiente si la
    /// relación existe y nadie la ha empezado. Sin relación no hay nada que cargar con relación: la salida física no la
    /// usa (no aplica) y, si la caja sigue en el patio, todavía no se sabe.
    /// </summary>
    private static string CargaState(TraceFacts facts, bool departed)
    {
        if (!facts.HasRelacion)
        {
            if (departed) return TraceState.NoAplica;
            return facts.HasCaja || facts.EntradasCount > 0 ? TraceState.Pendiente : TraceState.SinDato;
        }

        if (departed) return TraceState.Hecha;

        return LoadStatus(facts) switch
        {
            "CARGADA" or "DEMASIADO RAPIDO" or "AUTORIZADA" or "SALIDA" => TraceState.Hecha,
            "CARGANDO" or "EN ESPERA" => TraceState.EnCurso,
            "BLOQUEO" or "BLOQUEO REIMPRESION" or "INCOMPLETA" => TraceState.Atencion,
            _ => TraceState.Pendiente
        };
    }

    private static DateTime? DepartureDate(TraceFacts facts)
        => IsRealDate(facts.CajaDeparture) ? facts.CajaDeparture : facts.RelacionDeparture;

    private static string CasetaState(TraceFacts facts)
    {
        if (facts.Kind == TraceKind.PreRegistro && !facts.HasCaja)
        {
            return facts.PreStatus switch
            {
                PreregistroEstatus.Vigente => TraceState.Pendiente,
                PreregistroEstatus.Expirado or PreregistroEstatus.Cancelado => TraceState.Atencion,
                _ => TraceState.SinDato
            };
        }

        if (!facts.HasCaja) return TraceState.SinDato;
        return facts.CajaCancelled ? TraceState.Atencion : TraceState.Hecha;
    }

    private static string EntradaState(TraceFacts facts)
        => facts.EntradasCount > 0 ? TraceState.Hecha : TraceState.SinDato;

    private static string RevisionState(TraceFacts facts)
    {
        if (facts.EntradasCount == 0 || facts.ReviewStatuses.Count == 0) return TraceState.SinDato;

        int complete = facts.ReviewStatuses.Count(IsCompleteStatus);
        bool anyDiscrepancy = facts.ReviewStatuses.Contains("COMPLETA_CON_DISCREPANCIAS");
        bool anyInProgress = facts.ReviewStatuses.Contains("EN_PROGRESO");

        if (complete == facts.ReviewStatuses.Count) return anyDiscrepancy ? TraceState.Atencion : TraceState.Hecha;
        if (anyInProgress || complete > 0) return TraceState.EnCurso;
        return TraceState.Pendiente;
    }

    private static bool IsCompleteStatus(string status)
        => status is "COMPLETA" or "COMPLETA_CON_DISCREPANCIAS";

    private static string RelacionState(TraceFacts facts, bool departed)
    {
        if (facts.HasRelacion)
        {
            if (departed) return TraceState.Hecha;

            // La relación es la autorización del supervisor y llega DESPUÉS de cargar: mientras se carga (o no se ha
            // empezado) todavía no le toca; cuando el cargador cierra (cargada, bloqueada, incompleta) espera al
            // supervisor, que es lo que significa «en curso» aquí.
            return LoadStatus(facts) switch
            {
                "AUTORIZADA" or "SALIDA" => TraceState.Hecha,
                "" or "CARGANDO" or "EN ESPERA" => TraceState.Pendiente,
                _ => TraceState.EnCurso
            };
        }

        // Más de la mitad de las cajas sale por «salida física», sin relación: no es una falta.
        if (departed) return TraceState.NoAplica;
        return facts.HasCaja || facts.EntradasCount > 0 ? TraceState.Pendiente : TraceState.SinDato;
    }

    private static string SalidaState(TraceFacts facts, bool departed)
    {
        if (departed) return TraceState.Hecha;
        if (facts.HasRelacion && IsAuthorized(facts)) return TraceState.EnCurso;
        return facts.HasCaja || facts.HasRelacion ? TraceState.Pendiente : TraceState.SinDato;
    }

    // ── Ficha compacta (AR) ─────────────────────────────────────────────────

    /// <summary>Cuántos códigos se piden de una vez: los mismos seis QR que el AR puede tener a la vista.</summary>
    public const int MaxCompactCodes = 6;

    /// <summary>Solo las cajas y las relaciones tienen ficha compacta; una entrada ya tiene su tarjeta del AR.</summary>
    public static bool HasCompact(string kind) => kind is TraceKind.Caja or TraceKind.Relacion;

    /// <summary>
    /// Reduce una ficha a lo que cabe en la marca de un QR: señal, estado de las estaciones, banderas y tres datos para
    /// reconocer la pieza (cliente, caja, relación). El orden de <see cref="TraceCompactItem.States"/> es siempre el de
    /// <see cref="TraceStationId.CompactOrder"/>, aunque la ficha traiga menos estaciones.
    /// </summary>
    public static TraceCompactItem ToCompact(TraceResult result)
    {
        var item = new TraceCompactItem
        {
            Code = result.Code,
            Kind = result.Kind,
            Found = result.Found,
            Reason = result.Reason,
            Signal = result.Signal,
            Flags = result.Flags.ToList()
        };

        foreach (string id in TraceStationId.CompactOrder)
            item.States.Add(result.Stations.FirstOrDefault(station => station.Id == id)?.State ?? TraceState.SinDato);
        item.Carga = result.Stations.FirstOrDefault(station => station.Id == TraceStationId.Carga)?.State ?? TraceState.SinDato;

        if (!result.Found) return item;

        item.Customer = !string.IsNullOrWhiteSpace(result.Caja?.Cliente)
            ? result.Caja!.Cliente
            : result.Entradas.Select(entrada => entrada.Cliente).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty;
        item.Box = !string.IsNullOrWhiteSpace(result.Caja?.NumCaja) ? result.Caja!.NumCaja : result.Relacion?.Caja ?? string.Empty;
        item.Relno = !string.IsNullOrWhiteSpace(result.Relacion?.Relno) ? result.Relacion!.Relno : result.Caja?.Relacion ?? string.Empty;
        return item;
    }

    // ── Banderas, señal y acciones ──────────────────────────────────────────

    public static List<string> BuildFlags(TraceFacts facts)
    {
        var flags = new List<string>();

        if (facts.CajaCancelled) flags.Add(TraceFlag.Cancelada);

        bool boxCode = facts.Kind is TraceKind.Caja or TraceKind.Relacion;
        if (boxCode && facts.EntradasCount == 0) flags.Add(TraceFlag.SinEntradaVinculada);
        if (facts.HasLinkByRelacion && !facts.HasDirectLink) flags.Add(TraceFlag.EntradaPorRelacion);
        if (facts.Kind == TraceKind.Caja && facts.HasCaja && !facts.HasRelacion) flags.Add(TraceFlag.SinRelacion);

        if (facts.HasDamage) flags.Add(TraceFlag.Danada);
        if (facts.HasHazmat) flags.Add(TraceFlag.Hazmat);
        if (facts.ReviewStatuses.Contains("COMPLETA_CON_DISCREPANCIAS")) flags.Add(TraceFlag.Discrepancia);
        if (facts.EntradasTruncated) flags.Add(TraceFlag.ListaRecortada);
        if (facts.RelacionBusy) flags.Add(TraceFlag.RelacionOcupada);

        if (HasOpenRecord(facts)) flags.Add(TraceFlag.RegistroAbierto);

        return flags;
    }

    /// <summary>
    /// Señal global. Se deriva solo de hechos: <c>ALTO</c> cuando algo impide seguir (caja cancelada, pre-registro
    /// vencido o cancelado), <c>ATENCION</c> con daño o discrepancias, <c>LISTO</c> cuando la relación está autorizada y
    /// falta salir, <c>CERRADO</c> cuando ya salió y <c>AVISO</c> en el resto.
    /// </summary>
    public static string BuildSignal(TraceFacts facts, bool found)
    {
        if (!found) return TraceSignal.SinDato;

        if (facts.Kind == TraceKind.PreRegistro && !facts.HasCaja)
        {
            return facts.PreStatus switch
            {
                PreregistroEstatus.Expirado or PreregistroEstatus.Cancelado => TraceSignal.Alto,
                PreregistroEstatus.Vigente => TraceSignal.Aviso,
                _ => TraceSignal.SinDato
            };
        }

        if (facts.CajaCancelled) return TraceSignal.Alto;

        // Ya salió: queda cerrado aunque tenga daño o discrepancias (las banderas siguen a la vista). Solo se pide
        // atención si la relación salió y el registro de la caja en el patio sigue abierto.
        if (IsDeparted(facts)) return HasOpenRecord(facts) ? TraceSignal.Atencion : TraceSignal.Cerrado;

        if (facts.HasDamage || facts.ReviewStatuses.Contains("COMPLETA_CON_DISCREPANCIAS") || facts.RelacionBusy)
            return TraceSignal.Atencion;
        if (IsAuthorized(facts)) return TraceSignal.Listo;
        return TraceSignal.Aviso;
    }

    /// <summary>La relación ya salió pero la caja sigue en el patio: falta cerrar su registro.</summary>
    private static bool HasOpenRecord(TraceFacts facts)
        => facts.HasCaja && facts.HasRelacion
           && string.Equals(facts.RelacionStatus, "SALIDA", StringComparison.OrdinalIgnoreCase)
           && !IsRealDate(facts.CajaDeparture);

    /// <summary>
    /// Acciones que el estado permite, en orden de sugerencia (la primera es la principal). Si hay más de una entrada
    /// no se ofrece abrir «la» entrada: se elige desde la lista de relacionadas.
    /// </summary>
    public static List<string> BuildActions(TraceFacts facts, bool found)
    {
        var actions = new List<string>();
        void Add(string action)
        {
            if (!actions.Contains(action)) actions.Add(action);
        }

        if (!found) return actions;

        bool departed = IsDeparted(facts);
        bool singleEntrada = facts.EntradasCount == 1;

        switch (facts.Kind)
        {
            case TraceKind.Caja:
                if (facts.CajaCancelled) break;
                if (!departed && IsAuthorized(facts)) Add(TraceAction.ExitGate);
                if (!departed && facts.EntradasCount == 0 && !facts.HasRelacion) Add(TraceAction.CreateEntry);
                if (IsLoadPending(facts)) Add(TraceAction.OpenLoad);
                if (facts.HasRelacion) Add(TraceAction.OpenRelation);
                if (singleEntrada)
                {
                    Add(TraceAction.OpenEntry);
                    Add(TraceAction.ReviewEntry);
                }
                if (!departed)
                {
                    Add(TraceAction.FindInYard);
                    Add(TraceAction.ExitGate);
                }
                break;

            case TraceKind.Relacion:
                if (!departed && IsAuthorized(facts)) Add(TraceAction.ExitGate);
                // Sin cargar es lo que sigue: «Cargar mercancía» va primero. Una relación ya cargada no la ofrece.
                if (IsLoadPending(facts)) Add(TraceAction.OpenLoad);
                Add(TraceAction.OpenRelation);
                if (singleEntrada)
                {
                    Add(TraceAction.OpenEntry);
                    Add(TraceAction.ReviewEntry);
                }
                if (!departed) Add(TraceAction.ExitGate);
                break;

            case TraceKind.Carpeta:
            case TraceKind.Bulto:
                bool reviewPending = facts.ReviewStatuses.Any(status => !IsCompleteStatus(status));
                if (reviewPending) Add(TraceAction.ReviewEntry);
                Add(TraceAction.OpenEntry);
                Add(TraceAction.ReviewEntry);
                Add(TraceAction.ShowOnMap);
                if (facts.HasRelacion) Add(TraceAction.OpenRelation);
                break;

            case TraceKind.PreRegistro:
                if (facts.PreStatus == PreregistroEstatus.Vigente) Add(TraceAction.RegisterPreRegistro);
                else if (facts.HasCaja && !departed) Add(TraceAction.FindInYard);
                break;
        }

        return actions;
    }
}
