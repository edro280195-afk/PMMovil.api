using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Dapper;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Resuelve un código leído (caja, relación, carpeta, bulto o pre-registro) a su ficha: recorre la cadena
/// caja → entrada → revisión → relación → salida con consultas filtradas por la llave del código.
///
/// Es de solo lectura, sin excepciones: no escribe auditoría, no vence pre-registros ni toca ningún estado. Ese es
/// el motivo por el que no reutiliza <c>PreregistroService.ValidarAsync</c>, que sí escribe.
/// </summary>
public sealed class TrazabilidadService
{
    /// <summary>Tope de entradas que se detallan por ficha (la relación con más entradas tiene 22).</summary>
    public const int MaxEntradas = 25;

    private readonly BaseRepository _db;
    private readonly RevisionNormalCompletionService _completion;

    public TrazabilidadService(BaseRepository db, RevisionNormalCompletionService completion)
    {
        _db = db;
        _completion = completion;
    }

    private sealed class CajaRow
    {
        public string CasetaId { get; set; } = string.Empty;
        public string NumCaja { get; set; } = string.Empty;
        public string Cliente { get; set; } = string.Empty;
        public string Linea { get; set; } = string.Empty;
        public string Planta { get; set; } = string.Empty;
        public string TipoOp { get; set; } = string.Empty;
        public string Estatus { get; set; } = string.Empty;
        public string Relacion { get; set; } = string.Empty;
        public DateTime? ArrivalAt { get; set; }
        public DateTime? DepartureAt { get; set; }
    }

    private sealed class RelacionRow
    {
        public string Relno { get; set; } = string.Empty;
        public string EstatusCarga { get; set; } = string.Empty;
        public string Caja { get; set; } = string.Empty;
        public string Transfer { get; set; } = string.Empty;
        public DateTime? AuthorizedAt { get; set; }
        public DateTime? DepartureAt { get; set; }
        public string UsuEdit { get; set; } = string.Empty;
    }

    private sealed class EntradaRow
    {
        public string Reference { get; set; } = string.Empty;
        public string Cliente { get; set; } = string.Empty;
        public DateTime? ArrivalAt { get; set; }
        public bool HasHazmat { get; set; }
    }

    private sealed class DamageRow
    {
        public string Reference { get; set; } = string.Empty;
        public bool HasDamage { get; set; }
    }

    private sealed class LabelRow
    {
        public string LabelStatus { get; set; } = string.Empty;
        public string Relacion { get; set; } = string.Empty;
    }

    private sealed class PreregistroRow
    {
        public string Folio { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string Unidad { get; set; } = string.Empty;
        public string Linea { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime? ExpiresAt { get; set; }
        public DateTime? UsedAt { get; set; }
        public string CasetaId { get; set; } = string.Empty;
    }

    /// <summary>Lo que se va juntando mientras se recorre la cadena de un código.</summary>
    private sealed class Chain
    {
        public bool Found { get; set; }
        public CajaRow? Caja { get; set; }
        public RelacionRow? Relacion { get; set; }
        public List<TraceEntrada> Entradas { get; } = new();
        public List<DateTime> EntradaDates { get; } = new();
        public bool Truncated { get; set; }
        public bool HasDirectLink { get; set; }
        public bool HasLinkByRelacion { get; set; }
        public TraceBulto? Bulto { get; set; }
        public TracePreregistro? Preregistro { get; set; }
    }

    private const string CajaColumns = @"
            c.casetaid AS CasetaId,
            IFNULL(c.nequipo, '') AS NumCaja,
            IFNULL(cl.cnombre, '') AS Cliente,
            IFNULL(c.transpore, '') AS Linea,
            IFNULL(c.planta, '') AS Planta,
            CASE WHEN c.tipoop = 'IMPORTACION' THEN 'IMPO'
                 WHEN c.tipoop = 'EXPORTACION' THEN 'EXPO'
                 ELSE IFNULL(c.tipoop, '') END AS TipoOp,
            IFNULL(c.estatus, '') AS Estatus,
            TRIM(IFNULL(c.relacion, '')) AS Relacion,
            CASE WHEN YEAR(c.farribo) >= 2000 THEN c.farribo END AS ArrivalAt,
            CASE WHEN YEAR(c.fsalida) >= 2000 THEN c.fsalida END AS DepartureAt";

    /// <summary>Resuelve el código a su ficha. Los códigos que no se reconocen vuelven rechazados sin tocar la base.</summary>
    public async Task<TraceResult> ResolveAsync(string? rawCode, CancellationToken ct = default)
    {
        TraceParsedCode parsed = TrazabilidadRules.Parse(rawCode);
        if (!parsed.IsRecognized)
        {
            // El gafete y lo que no tiene forma de código PMM no salen de aquí: ni se consulta ni se devuelve su valor.
            return new TraceResult { Success = false, Reason = parsed.Rejection, Stations = EmptyStations() };
        }

        using IDbConnection conn = _db.CreateConnection();
        var chain = new Chain();

        switch (parsed.Kind)
        {
            case TraceKind.Caja:
                await ResolveCajaAsync(conn, parsed, chain, ct);
                break;
            case TraceKind.Relacion:
                await ResolveRelacionAsync(conn, parsed.Code, chain, ct);
                break;
            case TraceKind.Carpeta:
            case TraceKind.Bulto:
                await ResolveEntradaAsync(conn, parsed, chain, ct);
                break;
            case TraceKind.PreRegistro:
                await ResolvePreregistroAsync(conn, parsed, chain, ct);
                break;
        }

        return Compose(parsed, chain);
    }

    // ── Caja ────────────────────────────────────────────────────────────────

    private async Task ResolveCajaAsync(IDbConnection conn, TraceParsedCode parsed, Chain chain, CancellationToken ct)
    {
        // El folio puede estar guardado con o sin el cero de la plantilla vieja; se busca por las dos formas y,
        // si hubiera dos filas, gana la que coincide exacta con lo escaneado.
        CajaRow? caja = await conn.QueryFirstOrDefaultAsync<CajaRow>(new CommandDefinition(
            $@"SELECT {CajaColumns}
               FROM caseta c
               LEFT JOIN clientes cl ON c.cclienteid = cl.cidcte
               WHERE c.casetaid IN @ids
               ORDER BY (c.casetaid = @exact) DESC, c.idcaseta DESC
               LIMIT 1",
            new { ids = parsed.CasetaIdCandidates, exact = parsed.Code },
            cancellationToken: ct));

        if (caja is null) return;

        chain.Found = true;
        await LoadCajaChainAsync(conn, caja, chain, ct);
    }

    /// <summary>Dada una caja: su relación y las entradas que salen del enlace directo y de la relación.</summary>
    private async Task LoadCajaChainAsync(IDbConnection conn, CajaRow caja, Chain chain, CancellationToken ct)
    {
        chain.Caja = caja;

        // Hoy Bodega no llena traf3.ccasetaid (se llenará cuando salga esta versión de la app): el enlace
        // directo casi siempre estará vacío y la relación es el camino que sí funciona.
        List<string> direct = (await conn.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT t3.creferencia
              FROM traf3 t3
              WHERE t3.ccasetaid = @casetaId AND t3.creferencia <> ''
              LIMIT 26",
            new { casetaId = caja.CasetaId },
            cancellationToken: ct))).ToList();

        List<string> byRelacion = new();
        if (TrazabilidadRules.HasRelationValue(caja.Relacion))
        {
            chain.Relacion = await LoadRelacionRowAsync(conn, caja.Relacion, ct);
            byRelacion = await LoadReferencesByRelacionAsync(conn, caja.Relacion, ct);
        }

        await FillEntradasAsync(conn, direct, byRelacion, chain, ct);
    }

    // ── Relación ────────────────────────────────────────────────────────────

    private async Task ResolveRelacionAsync(IDbConnection conn, string relno, Chain chain, CancellationToken ct)
    {
        RelacionRow? relacion = await LoadRelacionRowAsync(conn, relno, ct);
        if (relacion is null) return;

        chain.Found = true;
        chain.Relacion = relacion;

        // Una relación puede llevar varias cajas; la ficha muestra la más reciente.
        chain.Caja = await conn.QueryFirstOrDefaultAsync<CajaRow>(new CommandDefinition(
            $@"SELECT {CajaColumns}
               FROM caseta c
               LEFT JOIN clientes cl ON c.cclienteid = cl.cidcte
               WHERE c.relacion = @relno
               ORDER BY c.idcaseta DESC
               LIMIT 1",
            new { relno },
            cancellationToken: ct));

        List<string> byRelacion = await LoadReferencesByRelacionAsync(conn, relno, ct);
        await FillEntradasAsync(conn, new List<string>(), byRelacion, chain, ct);
    }

    private static async Task<RelacionRow?> LoadRelacionRowAsync(IDbConnection conn, string relno, CancellationToken ct)
    {
        // El nombre de la tabla sale de dos valores fijos (ver RelationTable); el relno va siempre como parámetro.
        string table = TrazabilidadRules.RelationTable(relno);
        return await conn.QueryFirstOrDefaultAsync<RelacionRow>(new CommandDefinition(
            $@"SELECT r.relno AS Relno,
                      IFNULL(r.estatus_carga, '') AS EstatusCarga,
                      IFNULL(r.caja, '') AS Caja,
                      IFNULL(t.cnombre, '') AS Transfer,
                      CASE WHEN YEAR(r.autoriza_carga) >= 2000 THEN r.autoriza_carga END AS AuthorizedAt,
                      CASE WHEN YEAR(r.salida_caseta) >= 2000 THEN r.salida_caseta END AS DepartureAt,
                      IFNULL(r.usuedit, '') AS UsuEdit
               FROM {table} r
               LEFT JOIN tranfers t ON r.cidtransfer = t.cidtransfer
               WHERE r.relno = @relno
               LIMIT 1",
            new { relno },
            cancellationToken: ct));
    }

    private static async Task<List<string>> LoadReferencesByRelacionAsync(IDbConnection conn, string relno, CancellationToken ct)
        => (await conn.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT creferencia
              FROM traf2
              WHERE nrelacion = @relno AND creferencia <> ''
              LIMIT 26",
            new { relno },
            cancellationToken: ct))).ToList();

    // ── Carpeta y bulto ─────────────────────────────────────────────────────

    private async Task ResolveEntradaAsync(IDbConnection conn, TraceParsedCode parsed, Chain chain, CancellationToken ct)
    {
        string reference = parsed.Reference;

        // La entrada puede ser una referencia agrupada que solo existe en `fact`; el detalle sale de la misma ruta.
        await FillEntradasAsync(conn, new List<string> { reference }, new List<string>(), chain, ct, singleCodeReference: true);
        if (chain.Entradas.Count == 0) return;

        chain.Found = true;

        if (parsed.Kind == TraceKind.Bulto)
        {
            LabelRow? label = await conn.QueryFirstOrDefaultAsync<LabelRow>(new CommandDefinition(
                @"SELECT TRIM(IFNULL(estatus, '')) AS LabelStatus, TRIM(IFNULL(relacion, '')) AS Relacion
                  FROM etiqueta_mcia
                  WHERE creferencia = @reference AND textoetiqueta = @label
                  ORDER BY id_etiqueta_mcia DESC
                  LIMIT 1",
                new { reference, label = parsed.Code },
                cancellationToken: ct));

            chain.Bulto = new TraceBulto
            {
                Ordinal = parsed.Ordinal,
                Total = parsed.Total,
                Registered = label is not null,
                LabelStatus = label?.LabelStatus ?? string.Empty,
                Relacion = label?.Relacion ?? string.Empty
            };
        }

        // Relación de la entrada.
        string? relno = await conn.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            @"SELECT TRIM(nrelacion)
              FROM traf2
              WHERE creferencia = @reference AND nrelacion IS NOT NULL AND TRIM(nrelacion) <> ''
              LIMIT 1",
            new { reference },
            cancellationToken: ct));
        if (TrazabilidadRules.HasRelationValue(relno))
            chain.Relacion = await LoadRelacionRowAsync(conn, relno!, ct);

        // Caja de la entrada: por el enlace directo si Bodega ya lo llena y, si no, por la relación.
        string? casetaId = await conn.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            @"SELECT TRIM(ccasetaid)
              FROM traf3
              WHERE creferencia = @reference AND ccasetaid IS NOT NULL AND TRIM(ccasetaid) NOT IN ('', '0')
              LIMIT 1",
            new { reference },
            cancellationToken: ct));

        if (!string.IsNullOrEmpty(casetaId))
        {
            chain.HasDirectLink = true;
            chain.Caja = await conn.QueryFirstOrDefaultAsync<CajaRow>(new CommandDefinition(
                $@"SELECT {CajaColumns}
                   FROM caseta c
                   LEFT JOIN clientes cl ON c.cclienteid = cl.cidcte
                   WHERE c.casetaid = @casetaId
                   LIMIT 1",
                new { casetaId },
                cancellationToken: ct));
        }
        else if (chain.Relacion is not null)
        {
            chain.Caja = await conn.QueryFirstOrDefaultAsync<CajaRow>(new CommandDefinition(
                $@"SELECT {CajaColumns}
                   FROM caseta c
                   LEFT JOIN clientes cl ON c.cclienteid = cl.cidcte
                   WHERE c.relacion = @relno
                   ORDER BY c.idcaseta DESC
                   LIMIT 1",
                new { relno = chain.Relacion.Relno },
                cancellationToken: ct));
        }
    }

    // ── Pre-registro ────────────────────────────────────────────────────────

    private async Task ResolvePreregistroAsync(IDbConnection conn, TraceParsedCode parsed, Chain chain, CancellationToken ct)
    {
        using IDbConnection utileria = _db.CreatePmmutileriaConnection();
        PreregistroRow? row = await utileria.QueryFirstOrDefaultAsync<PreregistroRow>(new CommandDefinition(
            @"SELECT folio AS Folio, token AS Token,
                     IFNULL(nunidad, '') AS Unidad, IFNULL(transporu, '') AS Linea,
                     estatus AS Status,
                     CASE WHEN YEAR(fexpira) >= 2000 THEN fexpira END AS ExpiresAt,
                     CASE WHEN YEAR(fuso) >= 2000 THEN fuso END AS UsedAt,
                     IFNULL(casetaid, '') AS CasetaId
              FROM caseta_preregistro
              WHERE folio = @folio
              LIMIT 1",
            new { folio = parsed.PreFolio },
            cancellationToken: ct));

        // Mismo criterio que la validación de garita: sin token válido no existe. La comparación no revela en qué
        // posición falla.
        if (row is null || !TokenMatches(row.Token, parsed.PreToken)) return;

        // El vencimiento se calcula al vuelo; aquí no se actualiza el estatus como hace la validación de garita.
        string status = row.Status;
        if (status == PreregistroEstatus.Vigente && row.ExpiresAt.HasValue && row.ExpiresAt.Value < DateTime.Now)
            status = PreregistroEstatus.Expirado;

        chain.Found = true;
        chain.Preregistro = new TracePreregistro
        {
            Folio = row.Folio,
            Status = status,
            Linea = row.Linea,
            Unidad = row.Unidad,
            ExpiresAt = TrazabilidadRules.FormatDate(row.ExpiresAt),
            UsedAt = TrazabilidadRules.FormatDate(row.UsedAt),
            CasetaId = row.CasetaId
        };

        // Un pre-registro ya usado dio origen a una entrada de caseta: la cadena sigue desde ahí.
        if (status == PreregistroEstatus.Usado && !string.IsNullOrWhiteSpace(row.CasetaId))
        {
            CajaRow? caja = await conn.QueryFirstOrDefaultAsync<CajaRow>(new CommandDefinition(
                $@"SELECT {CajaColumns}
                   FROM caseta c
                   LEFT JOIN clientes cl ON c.cclienteid = cl.cidcte
                   WHERE c.casetaid = @casetaId
                   LIMIT 1",
                new { casetaId = row.CasetaId },
                cancellationToken: ct));
            if (caja is not null) await LoadCajaChainAsync(conn, caja, chain, ct);
        }
    }

    public static bool TokenMatches(string stored, string received)
    {
        byte[] a = Encoding.UTF8.GetBytes(stored ?? string.Empty);
        byte[] b = Encoding.UTF8.GetBytes(received ?? string.Empty);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ── Entradas ────────────────────────────────────────────────────────────

    /// <summary>
    /// Detalla las entradas a partir de sus referencias: datos básicos, daño y avance de revisión. Las del enlace
    /// directo van primero. Con <paramref name="singleCodeReference"/> la referencia viene del propio código y, si no
    /// está en `traf`, se acepta una referencia agrupada que solo exista en `fact`.
    /// </summary>
    private async Task FillEntradasAsync(
        IDbConnection conn,
        List<string> direct,
        List<string> byRelacion,
        Chain chain,
        CancellationToken ct,
        bool singleCodeReference = false)
    {
        var via = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (string reference in direct)
            if (via.TryAdd(reference, singleCodeReference ? "CODIGO" : "DIRECTA")) ordered.Add(reference);
        foreach (string reference in byRelacion)
            if (via.TryAdd(reference, "RELACION")) ordered.Add(reference);

        chain.HasDirectLink = chain.HasDirectLink || (!singleCodeReference && direct.Count > 0);
        chain.HasLinkByRelacion = via.Values.Any(value => value == "RELACION");

        if (ordered.Count > MaxEntradas)
        {
            chain.Truncated = true;
            ordered = ordered.Take(MaxEntradas).ToList();
        }

        if (ordered.Count == 0) return;

        var rows = (await conn.QueryAsync<EntradaRow>(new CommandDefinition(
            @"SELECT t.creferencia AS Reference,
                     IFNULL(MAX(cl.cnombre), '') AS Cliente,
                     CASE WHEN YEAR(MAX(t.fcreatrafico)) >= 2000 THEN MAX(t.fcreatrafico) END AS ArrivalAt,
                     MAX(CASE WHEN COALESCE(t.lhazmat, 0) = 1 THEN 1 ELSE 0 END) AS HasHazmat
              FROM traf t
              LEFT JOIN clientes cl ON cl.cidcte = t.cclienteidtraf
              WHERE t.creferencia IN @references
              GROUP BY t.creferencia",
            new { references = ordered },
            cancellationToken: ct))).ToDictionary(row => row.Reference, StringComparer.OrdinalIgnoreCase);

        if (singleCodeReference && rows.Count == 0)
        {
            // Referencia agrupada: existe como grupo de facturas aunque no tenga fila propia en `traf`.
            var grouped = (await conn.QueryAsync<EntradaRow>(new CommandDefinition(
                @"SELECT f.cref_agrupada AS Reference,
                         IFNULL(MAX(cl.cnombre), '') AS Cliente,
                         CASE WHEN YEAR(MAX(f.dfecha)) >= 2000 THEN MAX(f.dfecha) END AS ArrivalAt,
                         0 AS HasHazmat
                  FROM fact f
                  LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid
                  WHERE f.cref_agrupada IN @references
                  GROUP BY f.cref_agrupada",
                new { references = ordered },
                cancellationToken: ct))).ToList();
            foreach (EntradaRow row in grouped) rows[row.Reference] = row;
            if (rows.Count == 0) return;
        }

        // Una relación o un enlace puede apuntar a una referencia sin fila propia: se muestra solo con su referencia.
        var damage = (await conn.QueryAsync<DamageRow>(new CommandDefinition(
            @"SELECT creferencia AS Reference,
                     MAX(CASE WHEN COALESCE(danada, 0) = 1 THEN 1 ELSE 0 END) AS HasDamage
              FROM traf3
              WHERE creferencia IN @references
              GROUP BY creferencia",
            new { references = ordered },
            cancellationToken: ct))).ToDictionary(row => row.Reference, row => row.HasDamage, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, RevisionNormalCompletionSummary> summaries =
            await _completion.ObtenerResumenesAsync(conn, ordered);

        foreach (string reference in ordered)
        {
            if (singleCodeReference && !rows.ContainsKey(reference)) continue;

            rows.TryGetValue(reference, out EntradaRow? row);
            var entrada = new TraceEntrada
            {
                Reference = reference,
                Cliente = row?.Cliente ?? string.Empty,
                ArrivalAt = TrazabilidadRules.FormatDate(row?.ArrivalAt),
                Via = via[reference],
                HasHazmat = row?.HasHazmat ?? false,
                HasDamage = damage.TryGetValue(reference, out bool damaged) && damaged
            };

            if (summaries.TryGetValue(reference, out RevisionNormalCompletionSummary? summary))
            {
                entrada.ReviewStatus = summary.ReviewStatus;
                entrada.TotalInvoices = summary.TotalInvoices;
                entrada.CompletedInvoices = summary.CompletedInvoices;
                entrada.TotalLines = summary.TotalLines;
                entrada.ReviewedLines = summary.ReviewedLines;
            }

            chain.Entradas.Add(entrada);
            if (TrazabilidadRules.IsRealDate(row?.ArrivalAt)) chain.EntradaDates.Add(row!.ArrivalAt!.Value);
        }
    }

    // ── Armado de la respuesta ──────────────────────────────────────────────

    private static TraceResult Compose(TraceParsedCode parsed, Chain chain)
    {
        // Del pre-registro solo sale el folio: el token es la llave del QR y no se devuelve.
        string code = parsed.Kind == TraceKind.PreRegistro
            ? $"{TrazabilidadRules.PreRegistroPrefix}{parsed.PreFolio}"
            : parsed.Code;

        TraceFacts facts = BuildFacts(parsed.Kind, chain);
        bool found = chain.Found;

        var result = new TraceResult
        {
            Success = true,
            Kind = parsed.Kind,
            Code = code,
            Found = found,
            Reason = found ? null : TraceReason.NotFound,
            Signal = TrazabilidadRules.BuildSignal(facts, found),
            Stations = found ? TrazabilidadRules.BuildStations(facts) : EmptyStations(),
            Flags = found ? TrazabilidadRules.BuildFlags(facts) : new List<string>(),
            Actions = TrazabilidadRules.BuildActions(facts, found),
            Entradas = found ? chain.Entradas : new List<TraceEntrada>(),
            Bulto = chain.Bulto,
            Preregistro = chain.Preregistro
        };

        if (found && chain.Caja is not null)
        {
            result.Caja = new TraceCaja
            {
                CasetaId = chain.Caja.CasetaId,
                NumCaja = chain.Caja.NumCaja,
                Cliente = chain.Caja.Cliente,
                Linea = chain.Caja.Linea,
                Planta = chain.Caja.Planta,
                TipoOp = chain.Caja.TipoOp,
                Estatus = chain.Caja.Estatus,
                Relacion = chain.Caja.Relacion,
                ArrivalAt = TrazabilidadRules.FormatDate(chain.Caja.ArrivalAt),
                DepartureAt = TrazabilidadRules.FormatDate(chain.Caja.DepartureAt)
            };
        }

        if (found && chain.Relacion is not null)
        {
            result.Relacion = new TraceRelacion
            {
                Relno = chain.Relacion.Relno,
                EstatusCarga = chain.Relacion.EstatusCarga,
                Caja = chain.Relacion.Caja,
                Transfer = chain.Relacion.Transfer,
                AuthorizedAt = TrazabilidadRules.FormatDate(chain.Relacion.AuthorizedAt),
                DepartureAt = TrazabilidadRules.FormatDate(chain.Relacion.DepartureAt),
                TotalEntradas = chain.Entradas.Count
            };
        }

        return result;
    }

    private static TraceFacts BuildFacts(string kind, Chain chain)
    {
        CajaRow? caja = chain.Caja;
        RelacionRow? relacion = chain.Relacion;

        return new TraceFacts
        {
            Kind = kind,
            HasCaja = caja is not null,
            CajaArrival = caja?.ArrivalAt,
            CajaDeparture = caja?.DepartureAt,
            CajaCancelled = caja is not null
                && (string.Equals(caja.Estatus, "CANCELADO", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(caja.Estatus, "CANCELADA", StringComparison.OrdinalIgnoreCase)),
            HasRelacion = relacion is not null,
            RelacionStatus = relacion?.EstatusCarga ?? string.Empty,
            RelacionAuthorizedAt = relacion?.AuthorizedAt,
            RelacionDeparture = relacion?.DepartureAt,
            RelacionBusy = relacion is not null && !string.IsNullOrWhiteSpace(relacion.UsuEdit),
            EntradasCount = chain.Entradas.Count,
            EntradasTruncated = chain.Truncated,
            HasDirectLink = chain.HasDirectLink,
            HasLinkByRelacion = chain.HasLinkByRelacion,
            EntradaArrival = chain.EntradaDates.Count > 0 ? chain.EntradaDates.Min() : null,
            // Una entrada sin facturas no tiene nada que revisar: contarla como «sin revisar» pintaría pendiente
            // una revisión que no existe (pasa con entradas viejas o que todavía no se capturan).
            ReviewStatuses = chain.Entradas
                .Where(entrada => entrada.TotalInvoices > 0)
                .Select(entrada => entrada.ReviewStatus)
                .ToList(),
            HasDamage = chain.Entradas.Any(entrada => entrada.HasDamage),
            HasHazmat = chain.Entradas.Any(entrada => entrada.HasHazmat),
            PreStatus = chain.Preregistro?.Status ?? string.Empty
        };
    }

    private static List<TraceStation> EmptyStations()
        => TraceStationId.Order
            .Select(id => new TraceStation { Id = id, State = TraceState.SinDato })
            .ToList();
}
