using Dapper;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public class ImexService
{
    private readonly string _connectionString;
    private readonly ILogger<ImexService> _logger;

    public ImexService(IConfiguration configuration, ILogger<ImexService> logger)
    {
        _connectionString = configuration.GetConnectionString("PmmtrafDb")!;
        _logger = logger;
    }

    private MySqlConnector.MySqlConnection CreateConnection() =>
   new(_connectionString);

    public async Task<List<ImexCaseta>> ObtenerCajasDeCasetaAsync()
    {
        const string sql = @"
   SELECT casetaid, nequipo, placas, idtranspore, transpore
   FROM caseta
   WHERE tipomov = 'RECOLECCION/PICKUP'
  AND tipoop = 'EXPORTACION'
  AND estatus = 'DISPONIBLE'
  AND tiporegistro = 'CAJA'
  AND (relacion = '' OR relacion = 'NO_RELACION')";

        using var conn = CreateConnection();
        var rows = (await conn.QueryAsync(sql)).ToList();

        return rows.Select(r => new ImexCaseta
        {
            Casetaid = (string)r.casetaid,
            Caja = (string)r.nequipo,
            Placas = (string)r.placas,
            Idlinea = r.idtranspore?.ToString() ?? "",
            RazonLinea = r.transpore?.ToString() ?? ""
        }).ToList();
    }

    public async Task<List<LineaTransportista>> ObtenerLineasAsync()
    {
        const string sql = @"
   SELECT cidtranspor AS Codigo, cnombre AS Nombre
   FROM transpor
   WHERE Inactivo = 0
   ORDER BY cnombre";

        using var conn = CreateConnection();
        var rows = (await conn.QueryAsync<LineaTransportista>(sql)).ToList();
        return rows;
    }

    public async Task<string> ObtenerConsecutivoRelacionAsync()
    {
        using var conn = CreateConnection();
        await conn.OpenAsync();

        using var tx = await conn.BeginTransactionAsync();

        var row = await conn.QueryFirstOrDefaultAsync<int?>(
        "SELECT rel_imex FROM consecutivo WHERE formulario = '01C'",
        transaction: tx);

        int consecutivo;
        if (row.HasValue)
        {
            consecutivo = row.Value + 1;
            await conn.ExecuteAsync(
           "UPDATE consecutivo SET rel_imex = @v WHERE formulario = '01C'",
           new { v = consecutivo },
           transaction: tx);
        }
        else
        {
            consecutivo = 1;
            await conn.ExecuteAsync(
           "INSERT INTO consecutivo (formulario, rel_imex) VALUES ('01C', 1)",
           transaction: tx);
        }

        await tx.CommitAsync();
        return consecutivo.ToString("D6");
    }

    public async Task<List<ImexEntrada>?> ObtenerEntradasAsync(string referencia)
    {
        const string sql = @"
   SELECT td.creferencia, td.ncantidad, td.cclase, td.cdescripcion,
     (SELECT t.ntotpeso FROM traf t WHERE t.creferencia = td.creferencia) AS ntotpeso,
     td.ubicacion, td.cobservaciones
   FROM trafdet td
   WHERE td.creferencia = @ref
  AND NOT EXISTS (
  SELECT 1 FROM traf2 WHERE creferencia = @ref AND LENGTH(nrelacion) > 0
  )";

        using var conn = CreateConnection();
        var rows = (await conn.QueryAsync(sql, new { @ref = referencia })).ToList();

        if (rows.Count == 0) return null;

        return rows.Select(r => new ImexEntrada
        {
            Entrada = r.creferencia?.ToString() ?? "",
            Cantidad = Convert.ToDecimal(r.ncantidad ?? 0),
            Clase = r.cclase?.ToString() ?? "",
            Descripcion = r.cdescripcion?.ToString() ?? "",
            Peso = Convert.ToDecimal(r.ntotpeso ?? 0),
            Ubicacion = r.ubicacion?.ToString() ?? "",
            Observaciones = r.cobservaciones?.ToString() ?? ""
        }).ToList();
    }

    public async Task<ImexRelacion?> ObtenerRelacionAsync(string relacion)
    {
        const string sql = @"
   SELECT relno, caja, placas, aduana, fechaCruce,
     observaciones, STATUS, usucrea, fcrea, usumodi, fmodi,
     tipop, transporte,
     (SELECT cnombre FROM transpor WHERE cidtranspor = transporte) AS nombreTransporte,
     version_hoja, estatus_carga, casetaid
   FROM relacion_imex
   WHERE TRIM(relno) = TRIM(@rel)";

        using var conn = CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync(sql, new { rel = relacion });

        if (row == null) return null;

        var relacionObj = new ImexRelacion
        {
            Relacion = row.relno?.ToString() ?? "",
            Caja = row.caja?.ToString() ?? "",
            Placas = row.placas?.ToString() ?? "",
            Aduana = row.aduana?.ToString() ?? "",
            FechaCruce = row.fechaCruce is DateTime dt ? dt : DateTime.MinValue,
            Observaciones = row.observaciones?.ToString() ?? "",
            StatusRelacion = row.STATUS?.ToString() ?? "",
            UsuarioRegistro = row.usucrea?.ToString() ?? "",
            FechaRegistro = row.fcrea is DateTime fcrea ? fcrea : DateTime.MinValue,
            UsuarioModifica = row.usumodi?.ToString() ?? "",
            FechaModifica = row.fmodi is DateTime fmodi ? fmodi : DateTime.MinValue,
            Tipop = row.tipop?.ToString() ?? "",
            Transporte = row.transporte?.ToString() ?? "",
            NombreTransporte = row.nombreTransporte?.ToString() ?? "",
            VersionHoja = Convert.ToInt32(row.version_hoja ?? 1),
            StatusCarga = row.estatus_carga?.ToString() ?? "",
            Casetaid = row.casetaid?.ToString() ?? ""
        };

        relacionObj.Entradas = await EntradasPorRelacionAsync(relacion);
        return relacionObj;
    }

    private async Task<List<ImexEntrada>> EntradasPorRelacionAsync(string relacion)
    {
        const string sql = @"
   SELECT td.creferencia, td.ncantidad, td.cclase, td.cdescripcion,
     (SELECT t.ntotpeso FROM traf t WHERE t.creferencia = td.creferencia) AS ntotpeso,
     td.ubicacion, td.cobservaciones
   FROM traf2 t2
   JOIN trafdet td ON t2.creferencia = td.creferencia
   WHERE t2.nrelacion = @rel OR TRIM(t2.nrelacion) = TRIM(@rel2)";

        using var conn = CreateConnection();
        var rows = (await conn.QueryAsync(sql, new { rel = relacion, rel2 = relacion })).ToList();

        if (rows.Count == 0) return [];

        return rows.Select(r => new ImexEntrada
        {
            Entrada = r.creferencia?.ToString() ?? "",
            Cantidad = Convert.ToDecimal(r.ncantidad ?? 0),
            Clase = r.cclase?.ToString() ?? "",
            Descripcion = r.cdescripcion?.ToString() ?? "",
            Peso = Convert.ToDecimal(r.ntotpeso ?? 0),
            Ubicacion = r.ubicacion?.ToString() ?? "",
            Observaciones = r.cobservaciones?.ToString() ?? ""
        }).ToList();
    }

    public async Task<ImexResult> GuardarInformacionAsync(GuardarImexRelacionRequest request, string usuario)
    {
        if (request.Entradas.Count == 0)
            return new ImexResult { Success = false, Message = "No hay entradas para procesar." };

        string relno = request.Relacion;

        using var conn = CreateConnection();
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();

        try
        {
            int existe = await conn.QueryFirstOrDefaultAsync<int>(
           "SELECT COUNT(*) FROM relacion_imex WHERE relno = @relno",
           new { relno },
           transaction: tx);

            string anioActual = DateTime.Now.ToString("yy");

            if (existe > 0)
            {
                await conn.ExecuteAsync(@"
 UPDATE relacion_imex
 SET status = @status, usumodi = @usumodi, fmodi = @fmodi, tipop = 'C',
 placas = @placas, transporte = @transporte, caja = @caja,
 aduana = @aduana, fechaCruce = @fechaCruce, observaciones = @observaciones,
 estatus_carga = @estatusCarga, bodega = @bodega, version_hoja = @versionHoja
 WHERE relno = @relno", new
                {
                    status = request.StatusRelacion,
                    usumodi = usuario,
                    fmodi = DateTime.Now,
                    placas = string.IsNullOrEmpty(request.Placas) ? null : request.Placas,
                    transporte = string.IsNullOrEmpty(request.Transporte) ? null : request.Transporte,
                    caja = string.IsNullOrEmpty(request.Caja) ? null : request.Caja,
                    aduana = string.IsNullOrEmpty(request.Aduana) ? null : request.Aduana,
                    fechaCruce = request.FechaCruce == DateTime.MinValue ? null : (DateTime?)request.FechaCruce,
                    observaciones = string.IsNullOrEmpty(request.Observaciones) ? null : request.Observaciones,
                    estatusCarga = request.StatusCarga,
                    bodega = string.IsNullOrEmpty(request.Bodega) ? null : request.Bodega,
                    versionHoja = request.VersionHoja,
                    relno
                }, transaction: tx);
            }
            else
            {
                await conn.ExecuteAsync(@"
 INSERT INTO relacion_imex (relno, status, usucrea, fcrea, usumodi, fmodi, tipop,
 placas, transporte, caja, aduana, fechaCruce, observaciones, estatus_carga, bodega, version_hoja)
 VALUES (@relno, 'EN PROCESO', @usucrea, @fcrea, @usumodi, @fmodi, 'C',
 @placas, @transporte, @caja, @aduana, @fechaCruce, @observaciones, @estatusCarga, @bodega, @versionHoja)", new
                {
                    relno,
                    usucrea = usuario,
                    fcrea = DateTime.Now,
                    usumodi = usuario,
                    fmodi = DateTime.Now,
                    placas = string.IsNullOrEmpty(request.Placas) ? null : request.Placas,
                    transporte = string.IsNullOrEmpty(request.Transporte) ? null : request.Transporte,
                    caja = string.IsNullOrEmpty(request.Caja) ? null : request.Caja,
                    aduana = string.IsNullOrEmpty(request.Aduana) ? null : request.Aduana,
                    fechaCruce = request.FechaCruce == DateTime.MinValue ? null : (DateTime?)request.FechaCruce,
                    observaciones = string.IsNullOrEmpty(request.Observaciones) ? null : request.Observaciones,
                    estatusCarga = request.StatusCarga,
                    bodega = string.IsNullOrEmpty(request.Bodega) ? null : request.Bodega,
                    versionHoja = request.VersionHoja
                }, transaction: tx);
            }

            foreach (var entrada in request.Entradas)
            {
                await conn.ExecuteAsync(
               "UPDATE traf2 SET nrelacion = @rel WHERE creferencia = @cref",
               new { rel = relno, cref = entrada.Entrada },
               transaction: tx);
            }

            foreach (var entrada in request.Entradas)
            {
                await conn.ExecuteAsync(
               "UPDATE traf SET ccajasal = @caja WHERE creferencia = @cref",
               new { caja = string.IsNullOrEmpty(request.Caja) ? null : request.Caja, cref = entrada.Entrada },
               transaction: tx);
            }

            if (!string.IsNullOrEmpty(request.Casetaid))
            {
                await conn.ExecuteAsync(
               "UPDATE caseta SET relacion = @rel WHERE casetaid = @id",
               new { rel = relno, id = request.Casetaid },
               transaction: tx);
            }

            await tx.CommitAsync();
            return new ImexResult { Message = "Insercion y actualizaciones realizadas correctamente." };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "Error al guardar relacion IMEX {Relno}", relno);
            return new ImexResult { Success = false, Message = $"Error al guardar la informacion: {ex.Message}" };
        }
    }

    public async Task<string> EliminarEntradaAsync(string entrada)
    {
        using var conn = CreateConnection();
        int result = await conn.ExecuteAsync(
        "UPDATE traf2 SET nrelacion = '' WHERE creferencia = @cref",
        new { cref = entrada });

        return result > 0 ? "Registro eliminado." : "No se elimino el registro.";
    }

    public async Task<string> EliminarRelacionAsync(string relacion, List<ImexEntrada> entradas)
    {
        using var conn = CreateConnection();
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();

        try
        {
            int result = await conn.ExecuteAsync(
           "DELETE FROM relacion_imex WHERE relno = @rel",
           new { rel = relacion },
           transaction: tx);

            if (result > 0)
            {
                foreach (var entrada in entradas)
                {
                    await conn.ExecuteAsync(
                    "UPDATE traf2 SET nrelacion = '' WHERE creferencia = @cref",
                    new { cref = entrada.Entrada },
                    transaction: tx);
                }

                await tx.CommitAsync();
                return "Registro eliminado.";
            }

            await tx.RollbackAsync();
            return "No se elimino el registro.";
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "Error al eliminar relacion IMEX {Rel}", relacion);
            return $"Error al eliminar: {ex.Message}";
        }
    }
}
