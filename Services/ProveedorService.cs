using Dapper;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public class ProveedorService
{
    private readonly string _connectionString;
    private readonly ILogger<ProveedorService> _logger;

    private const string ProveedorSelect = @"
   SELECT p.idprov AS IdProv,
          p.cidprov AS IdProveedorLegacy,
          p.cnombre AS Nombre,
          p.cdomicilio AS Domicilio,
          p.cnumexterior AS NumExterior,
          p.cnuminterior AS NumInterior,
          p.ccolonia AS Colonia,
          p.ccodigopostal AS CodigoPostal,
          p.cciudad AS Ciudad,
          p.cstateid AS StateId,
          p.cidpais AS IdPais,
          p.ctelefono AS Telefono,
          p.cextcont AS ExtContacto,
          p.cfax AS Fax,
          p.ccontacto AS Contacto,
          p.cemail AS Email,
          p.taxidno AS TaxId,
          p.cprogimex AS ProgramaImex,
          p.dvigimex AS FechaVigenciaImex,
          p.mnotas AS Notas,
          COALESCE(p.inactivo, 0) AS Inactivo,
          p.usucrea AS UsuarioCreacion,
          p.fcrea AS FechaCreacion,
          p.usumodi AS UsuarioModificacion,
          p.fmodi AS FechaModificacion
     FROM provs p";

    private const string ConsignatarioSelect = @"
   SELECT c.fk_idprov AS FkIdProv,
          c.cidprov AS IdProveedorLegacy,
          c.nlineno AS NLineno,
          c.cconsignaid AS CConsignaId,
          c.cnombre AS Nombre
     FROM consignaprov c";

    public ProveedorService(IConfiguration configuration, ILogger<ProveedorService> logger)
    {
        _connectionString = configuration.GetConnectionString("PmmtrafDb")!;
        _logger = logger;
    }

    private MySqlConnector.MySqlConnection CreateConnection() =>
   new(_connectionString);

    public async Task<ProveedoresResponse> ListarAsync(string? busqueda, int pagina = 1, int pageSize = 20)
    {
        using var conn = CreateConnection();

        var skip = (pagina - 1) * pageSize;

        var sqlCount = "SELECT COUNT(*) FROM provs p WHERE 1=1";
        var sql = ProveedorSelect + " WHERE 1=1";

        if (!string.IsNullOrWhiteSpace(busqueda))
        {
            var filtro = "%" + busqueda + "%";
            var where = " AND (p.cnombre LIKE @f OR p.cidprov LIKE @f OR p.taxidno LIKE @f OR p.cciudad LIKE @f)";
            sqlCount += where;
            sql += where + " ORDER BY p.cnombre LIMIT @take OFFSET @skip";

            var total = await conn.QueryFirstAsync<int>(sqlCount, new { f = filtro });
            var items = (await conn.QueryAsync<ProveedorListDto>(sql, new { f = filtro, take = pageSize, skip })).ToList();

            return new ProveedoresResponse
            {
                TotalRegistros = total,
                PaginaActual = pagina,
                TotalPaginas = (int)Math.Ceiling((double)total / pageSize),
                Proveedores = items
            };
        }
        else
        {
            sql += " ORDER BY p.cnombre LIMIT @take OFFSET @skip";

            var total = await conn.QueryFirstAsync<int>(sqlCount);
            var items = (await conn.QueryAsync<ProveedorListDto>(sql, new { take = pageSize, skip })).ToList();

            return new ProveedoresResponse
            {
                TotalRegistros = total,
                PaginaActual = pagina,
                TotalPaginas = (int)Math.Ceiling((double)total / pageSize),
                Proveedores = items
            };
        }
    }

    public async Task<Proveedor?> ObtenerPorIdAsync(int id)
    {
        using var conn = CreateConnection();
        var proveedor = await conn.QueryFirstOrDefaultAsync<Proveedor>(
        ProveedorSelect + " WHERE p.idprov = @id", new { id });

        if (proveedor != null)
        {
            proveedor.Consignatarios = (await conn.QueryAsync<ConsignatarioProv>(
           ConsignatarioSelect + " WHERE c.fk_idprov = @id ORDER BY c.nlineno",
           new { id })).ToList();
        }

        return proveedor;
    }

    public async Task<int> CrearAsync(ProveedorUpsertRequest request, string usuario)
    {
        using var conn = CreateConnection();
        var sql = @"
   INSERT INTO provs (cidprov, cnombre, cdomicilio, cnumexterior, cnuminterior,
  ccolonia, ccodigopostal, cciudad, cstateid, cidpais, ctelefono, cextcont, cfax,
  ccontacto, cemail, taxidno, cprogimex, dvigimex, mnotas, inactivo,
  usucrea, fcrea)
   VALUES (@idprovlegacy, @nombre, @domicilio, @numext, @numint,
  @colonia, @cp, @ciudad, @stateid, @idpais, @tel, @ext, @fax,
  @contacto, @email, @taxid, @progimex, @fecvig, @notas, @inactivo,
  @usr, NOW())";

        await conn.ExecuteAsync(sql, new
        {
            idprovlegacy = request.IdProveedorLegacy,
            nombre = request.Nombre,
            domicilio = request.Domicilio,
            numext = request.NumExterior,
            numint = request.NumInterior,
            colonia = request.Colonia,
            cp = request.CodigoPostal,
            ciudad = request.Ciudad,
            stateid = request.StateId,
            idpais = request.IdPais,
            tel = request.Telefono,
            ext = request.ExtContacto,
            fax = request.Fax,
            contacto = request.Contacto,
            email = request.Email,
            taxid = request.TaxId,
            progimex = request.ProgramaImex,
            fecvig = request.FechaVigenciaImex,
            notas = request.Notas,
            inactivo = request.Inactivo,
            usr = usuario
        });

        return (int)await conn.QueryFirstAsync<long>("SELECT LAST_INSERT_ID()");
    }

    public async Task<bool> ActualizarAsync(int id, ProveedorUpsertRequest request, string usuario)
    {
        using var conn = CreateConnection();
        var sql = @"
   UPDATE provs SET
  cidprov = @idprovlegacy, cnombre = @nombre,
  cdomicilio = @domicilio, cnumexterior = @numext, cnuminterior = @numint,
  ccolonia = @colonia, ccodigopostal = @cp, cciudad = @ciudad,
  cstateid = @stateid, cidpais = @idpais, ctelefono = @tel,
  cextcont = @ext, cfax = @fax, ccontacto = @contacto,
  cemail = @email, taxidno = @taxid, cprogimex = @progimex,
  dvigimex = @fecvig, mnotas = @notas, inactivo = @inactivo,
  usumodi = @usr, fmodi = NOW()
   WHERE idprov = @id";

        int rows = await conn.ExecuteAsync(sql, new
        {
            id,
            idprovlegacy = request.IdProveedorLegacy,
            nombre = request.Nombre,
            domicilio = request.Domicilio,
            numext = request.NumExterior,
            numint = request.NumInterior,
            colonia = request.Colonia,
            cp = request.CodigoPostal,
            ciudad = request.Ciudad,
            stateid = request.StateId,
            idpais = request.IdPais,
            tel = request.Telefono,
            ext = request.ExtContacto,
            fax = request.Fax,
            contacto = request.Contacto,
            email = request.Email,
            taxid = request.TaxId,
            progimex = request.ProgramaImex,
            fecvig = request.FechaVigenciaImex,
            notas = request.Notas,
            inactivo = request.Inactivo,
            usr = usuario
        });

        return rows > 0;
    }

    public async Task<bool> EliminarAsync(int id)
    {
        using var conn = CreateConnection();
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();

        try
        {
            await conn.ExecuteAsync(
               "DELETE FROM consignaprov WHERE fk_idprov = @id",
           new { id },
           transaction: tx);

            int rows = await conn.ExecuteAsync(
               "DELETE FROM provs WHERE idprov = @id",
           new { id },
           transaction: tx);

            await tx.CommitAsync();
            return rows > 0;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}
