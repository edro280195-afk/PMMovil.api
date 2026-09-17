using Dapper;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public class InventarioService
{
    private readonly string _connectionString;
    private readonly ILogger<InventarioService> _logger;

    public InventarioService(IConfiguration configuration, ILogger<InventarioService> logger)
    {
        _connectionString = configuration.GetConnectionString("PmmtrafDb")!;
        _logger = logger;
    }

    private MySqlConnector.MySqlConnection CreateConnection() =>
   new(_connectionString);

    public async Task<int> CountInvAudAsync()
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<int>("SELECT COUNT(*) FROM inventario");
    }

    public async Task ReiniciarInvAsync()
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync("TRUNCATE TABLE inventario");
    }

    public async Task<List<string>> BuscarEntradaAsync(string busqueda)
    {
        const string sql = @"
   SELECT creferencia
   FROM traf
   WHERE creferencia LIKE @patron
  AND tipop = 'C'";

        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<string>(sql, new { patron = $"%{busqueda}%" });
        return rows.ToList();
    }

    public async Task<InventarioEntradaResult?> BuscarEntradaTotAsync(string cref)
    {
        const string sql = @"
   SELECT traf.creferencia, total
   FROM traf
   LEFT JOIN etiqueta_mcia et ON traf.creferencia = et.creferencia
   WHERE traf.creferencia = @cref
   GROUP BY traf.creferencia";

        using var conn = CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync(sql, new { cref });

        if (row == null) return null;

        return new InventarioEntradaResult
        {
            Creferencia = row.creferencia?.ToString() ?? "",
            Total = row.total != null ? Convert.ToDecimal(row.total) : null
        };
    }

    public async Task<InventarioResult> InventarioOKAsync(string cref, string usuario)
    {
        using var conn = CreateConnection();
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();

        try
        {
            var etiquetas = (await conn.QueryAsync<string>(
           "SELECT textoetiqueta FROM etiqueta_mcia WHERE creferencia = @cref",
           new { cref },
           transaction: tx)).ToList();

            int insertados = 0;
            foreach (var et in etiquetas)
            {
                int existe = await conn.QueryFirstOrDefaultAsync<int>(
               "SELECT COUNT(*) FROM inventario WHERE textoetiqueta = @et",
               new { et },
               transaction: tx);

                if (existe == 0)
                {
                    await conn.ExecuteAsync(
                    "INSERT INTO inventario(textoetiqueta, usuario, fecha, ubicacion) VALUES(@et, @usr, @fecha, '')",
                    new { et, usr = "ADMIN", fecha = DateTime.Now },
                    transaction: tx);
                    insertados++;
                }
            }

            await tx.CommitAsync();
            return new InventarioResult
            {
                Success = true,
                Message = $"OK. {insertados} bultos insertados de {etiquetas.Count} totales."
            };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "Error en InventarioOK para {Cref}", cref);
            return new InventarioResult { Success = false, Message = $"Error: {ex.Message}" };
        }
    }

    public async Task<InventarioResult> GuardarScanAsync(string codigoBarras, string usuario, string ubicacion)
    {
        using var conn = CreateConnection();

        int existe = await conn.QueryFirstOrDefaultAsync<int>(
        "SELECT COUNT(*) FROM inventario WHERE textoetiqueta = @et",
        new { et = codigoBarras });

        if (existe > 0)
            return new InventarioResult { Success = true, Message = "Bulto ya registrado." };

        await conn.ExecuteAsync(
        "INSERT INTO inventario(textoetiqueta, usuario, fecha, ubicacion) VALUES(@et, @usr, @fecha, @ubi)",
        new { et = codigoBarras, usr = usuario, fecha = DateTime.Now, ubi = ubicacion });

        return new InventarioResult { Success = true, Message = "Bulto registrado." };
    }

    public async Task<List<InventarioBulto>> ObtenerBultosAuditadosAsync()
    {
        const string sql = "SELECT textoetiqueta, usuario, fecha, ubicacion FROM inventario ORDER BY fecha";

        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<InventarioBulto>(sql);
        return rows.ToList();
    }
}
