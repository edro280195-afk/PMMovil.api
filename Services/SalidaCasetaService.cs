using System.Data;

using Dapper;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public class SalidaCasetaService
{
    private readonly BaseRepository _db;
    private readonly ILogger<SalidaCasetaService> _logger;

    public SalidaCasetaService(BaseRepository db, ILogger<SalidaCasetaService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<SalidaCajaItem>> GetPendientesAsync(string planta)
    {
        // LEFT JOIN (antes INNER JOIN): los camiones que vienen de un
        // pre-registro entran a `caseta` con cclienteid vacio (no aplica un
        // cliente para un camion sin caja). Con INNER JOIN esas filas
        // desaparecian por completo de "Dar salida" -- solo se podia cerrar
        // su salida entrando directo a FoxPro, que consulta `caseta` sin este
        // join. COALESCE evita que NombreCliente llegue null al DTO.
        const string sql = @"
            SELECT
                c.casetaid  AS CasetaId,
                c.nequipo   AS NumCaja,
                c.transpore AS LineaCaja,
                c.nunidad   AS NumCamion,
                c.placas    AS Placas,
                COALESCE(cl.cnombre, '') AS NombreCliente,
                c.tipouni   AS TipoUnidad,
                c.tipomov   AS TipoMovimiento,
                CASE
                    WHEN c.tipoop = 'IMPORTACION' THEN 'IMPO'
                    WHEN c.tipoop = 'EXPORTACION' THEN 'EXPO'
                    ELSE c.tipoop
                END         AS TipoOp,
                c.planta    AS Planta,
                c.farribo   AS FechaArribo,
                DATEDIFF(NOW(), c.farribo) AS DiasEnPatio
            FROM caseta c
            LEFT JOIN clientes cl ON c.cclienteid = cl.cidcte
            WHERE (c.fsalida IS NULL OR c.fsalida < '2000-01-01')
              AND c.estatus <> 'CANCELADO'
              AND (@planta = '' OR c.planta = @planta)
            ORDER BY c.farribo DESC";

        using IDbConnection conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<SalidaCajaItem>(sql, new { planta });
        return rows.ToList();
    }

    public async Task<string> RevisarRelacionAsync(string casetaId)
    {
        const string sql = @"
            SELECT relacion
            FROM caseta
            WHERE casetaid = @casetaId AND LEFT(relacion, 2) = '01'
            LIMIT 1";

        using IDbConnection conn = _db.CreateConnection();
        var relacion = await conn.ExecuteScalarAsync<string?>(sql, new { casetaId });
        return relacion ?? string.Empty;
    }

    public async Task<RegistrarSalidaCajaResult> RegistrarSalidaAsync(string casetaId, string usuario, string observaciones)
    {
        using IDbConnection conn = _db.CreateConnection();
        conn.Open();
        using IDbTransaction tx = conn.BeginTransaction();
        try
        {
            var fsalida = await conn.ExecuteScalarAsync<DateTime?>(
    "SELECT fsalida FROM caseta WHERE casetaid = @casetaId LIMIT 1",
    new { casetaId }, tx);

            if (fsalida.HasValue && fsalida.Value > new DateTime(2000, 1, 1))
            {
                tx.Rollback();
                return new RegistrarSalidaCajaResult
                {
                    Success = false,
                    Message = "Esta salida ya fue registrada anteriormente."
                };
            }

            var consecutivo = await conn.ExecuteScalarAsync<int>(
    "SELECT numero FROM consecutivo WHERE formulario = 'CASETAS' AND prefijo = 'S'",
    transaction: tx);
            consecutivo++;
            await conn.ExecuteAsync(
                "UPDATE consecutivo SET numero = @numero WHERE formulario = 'CASETAS' AND prefijo = 'S'",
                new { numero = consecutivo }, tx);

            var salida = DateTime.Now;
            var rows = await conn.ExecuteAsync(@"
                UPDATE caseta
                SET fsalida    = @salida,
                    estatus    = 'SALIDA',
                    ususalida  = @usuario,
                    usumodi    = @usuario,
                    fmodi      = @salida,
                    obs_salida = @observaciones
                WHERE casetaid = @casetaId",
                new { salida, usuario, observaciones, casetaId }, tx);

            if (rows > 0)
            {
                tx.Commit();
                return new RegistrarSalidaCajaResult
                {
                    Success = true,
                    Message = "Salida registrada correctamente."
                };
            }

            tx.Rollback();
            return new RegistrarSalidaCajaResult
            {
                Success = false,
                Message = "No se encontró el registro de la caja."
            };
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
