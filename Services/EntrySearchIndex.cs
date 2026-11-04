using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dapper;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Data;

namespace PMMovil.Api.Services;

/// <summary>
/// El índice en memoria de las entradas de los últimos 365 días: referencia, cliente y fecha. Se arma con dos consultas
/// agrupadas (≈0.7 s en la base local) y se renueva solo en segundo plano; quien busca nunca espera a esa consulta salvo
/// la primera vez después de arrancar el servidor. Es un singleton: pide su propia conexión por ámbito.
/// </summary>
public sealed class EntrySearchIndex
{
    /// <summary>Hasta dónde llega hacia atrás lo que se puede buscar; una entrada más vieja se abre con su código exacto.</summary>
    public const int WindowDays = 365;

    /// <summary>Cada cuánto se considera viejo el índice y se renueva en segundo plano (sigue sirviéndose mientras tanto).</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(90);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EntrySearchIndex> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot? _snapshot;
    private int _refreshing;

    public EntrySearchIndex(IServiceScopeFactory scopes, ILogger<EntrySearchIndex> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public sealed record Snapshot(IReadOnlyList<EntrySearchEntry> Entries, DateTime BuiltAtUtc);

    private sealed class IndexRow
    {
        public string Reference { get; set; } = string.Empty;
        public string? ClientId { get; set; }
        public DateTime? Date { get; set; }
    }

    private sealed class ClientRow
    {
        public string? ClientId { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>El índice listo para buscar. Si ya existe se devuelve al instante, aunque haya que renovarlo.</summary>
    public async Task<Snapshot> GetAsync(CancellationToken ct = default)
    {
        Snapshot? current = _snapshot;
        if (current is not null)
        {
            if (DateTime.UtcNow - current.BuiltAtUtc > RefreshAfter) StartBackgroundRefresh();
            return current;
        }

        // Primera vez: todos los que lleguen a la vez esperan la misma construcción.
        await _gate.WaitAsync(ct);
        try
        {
            return _snapshot ??= await BuildAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StartBackgroundRefresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                _snapshot = await BuildAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Se sigue sirviendo el índice anterior: una consulta fallida no debe dejar a nadie sin buscador.
                _logger.LogWarning(ex, "No se pudo renovar el índice de búsqueda de entradas; se conserva el anterior");
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        });
    }

    private async Task<Snapshot> BuildAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopes.CreateScope();
        BaseRepository db = scope.ServiceProvider.GetRequiredService<BaseRepository>();
        using IDbConnection conn = db.CreateConnection();

        // Las fechas basura (año 2049, 9202) existen en la base: abajo se tratan como fecha desconocida.
        var factRows = (await conn.QueryAsync<IndexRow>(new CommandDefinition(@"
            SELECT f.cref_agrupada AS Reference,
                   MAX(f.cclienteid) AS ClientId,
                   MAX(f.dfecha) AS `Date`
            FROM fact f
            WHERE f.cref_agrupada IS NOT NULL AND f.cref_agrupada <> ''
              AND f.dfecha >= DATE_SUB(NOW(), INTERVAL @days DAY)
              AND f.dfecha < '2100-01-01'
            GROUP BY f.cref_agrupada", new { days = WindowDays }, cancellationToken: ct))).ToList();

        // Las entradas que solo existen como código de barras (sin facturas), igual que en la lista de pendientes.
        var barcodeRows = (await conn.QueryAsync<IndexRow>(new CommandDefinition(@"
            SELECT t.creferencia AS Reference,
                   MAX(t.cclienteidtraf) AS ClientId,
                   MAX(t.fcreatrafico) AS `Date`
            FROM traf t
            INNER JOIN cteprov cp
                    ON cp.cidcte = t.cclienteidtraf
                   AND cp.cidprov = t.cidprovtraf
                   AND cp.inactivo = 0
                   AND cp.barcode_long > 0
            WHERE t.creferencia IS NOT NULL AND t.creferencia <> ''
              AND t.fcreatrafico >= DATE_SUB(NOW(), INTERVAL @days DAY)
              AND t.fcreatrafico < '2100-01-01'
              AND NOT EXISTS (SELECT 1 FROM fact f0 WHERE f0.cref_agrupada = t.creferencia)
            GROUP BY t.creferencia", new { days = WindowDays }, cancellationToken: ct))).ToList();

        var clients = (await conn.QueryAsync<ClientRow>(new CommandDefinition(@"
            SELECT cidcte AS ClientId, MAX(cnombre) AS Name
            FROM clientes
            WHERE inactivo = 0
            GROUP BY cidcte", cancellationToken: ct)))
            .Where(c => !string.IsNullOrWhiteSpace(c.ClientId))
            .GroupBy(c => c.ClientId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        DateTime now = DateTime.Now;
        var entries = new List<EntrySearchEntry>(factRows.Count + barcodeRows.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IndexRow row in factRows.Concat(barcodeRows))
        {
            string reference = row.Reference.Trim();
            if (reference.Length == 0 || !seen.Add(reference)) continue;

            string clientId = (row.ClientId ?? string.Empty).Trim();
            clients.TryGetValue(clientId, out string? name);
            // Una fecha en el futuro es un dato mal capturado (hay entradas de 2019 con año 2049): se trata como fecha
            // desconocida para que no ganen los empates por parecer «las más recientes».
            DateTime date = row.Date ?? DateTime.MinValue;
            if (date > now.AddDays(1)) date = DateTime.MinValue;
            entries.Add(new EntrySearchEntry(reference, clientId, name ?? string.Empty, date));
        }

        _logger.LogInformation("Índice de búsqueda de entradas armado: {Count} entradas de los últimos {Days} días", entries.Count, WindowDays);
        return new Snapshot(entries, DateTime.UtcNow);
    }
}
