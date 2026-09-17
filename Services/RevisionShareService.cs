using System.Data;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Dapper;

using Microsoft.Extensions.Configuration;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public interface IRevisionShareService
{
    Task<(string Token, DateTime Expira)?> GenerarLinkAsync(string crefAgrupada, string usuario);
    Task<RevisionShareResumen?> ObtenerResumenAsync(string token);
    Task<(string FilePath, string Extension)?> ObtenerFotoAsync(string token, int idFotos);

    /// <summary>Resuelve la lista de archivos (ruta real en disco + nombre ya
    /// renombrado para descarga) de toda la evidencia de esta revisión, para
    /// armar el ZIP de "descargar todo". Null si el token venció/no existe.</summary>
    Task<(string CrefAgrupada, List<(string FilePath, string FileName)> Archivos)?> ObtenerArchivosParaZipAsync(string token);
}

/// <summary>
/// "Seguimiento de revisión": genera y sirve los links públicos -- página
/// SEPARADA de "Seguimiento de entrada" (EntradaShareService).
///
/// Entrada y Revisión son procesos distintos y consecutivos sobre el mismo
/// cref_agrupada (ver comentario completo en EntradaShareService): la
/// entrada es cuando la caja/mercancía llega a bodega (tabla traf); la
/// revisión pasa después, por factura (tablas fact/revdet). Esta página
/// muestra SOLO datos de la revisión -- estatus por factura, discrepancias,
/// evidencia fotográfica (ntipodocto=103) -- nunca cliente/transportista/
/// fotos de recepción, que viven en la página de Entrada.
///
/// El token vive en pmmutileria (igual que entrada_links_compartidos);
/// los datos de negocio siguen en pmmtraf.
/// </summary>
public class RevisionShareService : IRevisionShareService
{
    private static readonly TimeSpan Vigencia = TimeSpan.FromHours(72);

    private readonly BaseRepository _db;
    private readonly ILogger<RevisionShareService> _logger;
    private readonly string _basePath;

    public RevisionShareService(BaseRepository db, IConfiguration config, ILogger<RevisionShareService> logger)
    {
        _db = db;
        _logger = logger;
        _basePath = config["PhotoSettings:BasePath"] ?? "F:\\Trafico\\fotos";
    }

    public async Task<(string Token, DateTime Expira)?> GenerarLinkAsync(string crefAgrupada, string usuario)
    {
        using IDbConnection trafConn = _db.CreateConnection();

        // La revisión debe existir de verdad -- al menos una factura bajo
        // este cref_agrupada -- no generamos links para referencias sin
        // revisión iniciada.
        int? existe = await trafConn.QueryFirstOrDefaultAsync<int?>(
            "SELECT 1 FROM fact WHERE cref_agrupada = @cref LIMIT 1", new { cref = crefAgrupada });
        if (existe == null)
        {
            return null;
        }

        string token = Guid.NewGuid().ToString("N");
        DateTime expira = DateTime.Now.Add(Vigencia);

        using IDbConnection utilConn = _db.CreatePmmutileriaConnection();
        await utilConn.ExecuteAsync(
            @"INSERT INTO revision_links_compartidos (token, cref_agrupada, usucrea, fexpira)
              VALUES (@token, @cref, @usuario, @expira)",
            new { token, cref = crefAgrupada, usuario, expira });

        _logger.LogInformation("Link de seguimiento de revisión generado para {Cref} por {Usuario}, expira {Expira}", crefAgrupada, usuario, expira);
        return (token, expira);
    }

    public async Task<RevisionShareResumen?> ObtenerResumenAsync(string token)
    {
        (string Cref, DateTime GeneradoEn, DateTime Expira)? link = await ResolverAsync(token);
        if (link == null)
        {
            return null;
        }
        string cref = link.Value.Cref;

        using IDbConnection conn = _db.CreateConnection();

        dynamic? resumen = await conn.QueryFirstOrDefaultAsync<object>(
            @"SELECT
                  MAX(cl.cnombre) AS nom_cliente,
                  COUNT(DISTINCT f.cnofact) AS total_facturas,
                  COUNT(DISTINCT CASE WHEN f.status_rev = 1 THEN f.cnofact END) AS facturas_completas,
                  COUNT(DISTINCT rd.idrevdet) AS total_partidas,
                  COUNT(DISTINCT CASE WHEN rd.revisada = 1 THEN rd.idrevdet END) AS partidas_revisadas
              FROM fact f
              LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid
              LEFT JOIN revdet rd ON rd.cnofact = f.cnofact
              WHERE f.cref_agrupada = @cref
              GROUP BY f.cref_agrupada",
            new { cref });

        if (resumen == null)
        {
            // La revisión existía cuando se generó el link pero ya no hay
            // facturas bajo este cref (poco probable, pero mejor expirado
            // que una página vacía a medias).
            return null;
        }

        List<RevisionShareFactura> facturas = (await conn.QueryAsync<RevisionShareFactura>(
            @"SELECT
                  f.cnofact AS Cnofact,
                  COALESCE(MAX(f.status_rev), 0) AS StatusRev,
                  COALESCE(MAX(NULLIF(TRIM(f.usurev), '')), '') AS Revisor,
                  COALESCE(MAX(f.totbultos), 0) AS TotBultos,
                  COALESCE(MAX(f.totbultosdoc), 0) AS TotBultosDoc,
                  COALESCE(MAX(f.discrepancias), '') AS Discrepancias
              FROM fact f
              WHERE f.cref_agrupada = @cref
              GROUP BY f.cnofact
              ORDER BY f.cnofact",
            new { cref })).ToList();

        // Mismo criterio que FotoController: 107 = evidencia normal, 103 =
        // evidencia de discrepancia -- se muestran separadas, nunca
        // mezcladas, igual que en la app (PhotoGridScreen). Todas las fotos
        // de revisión se guardan con creferencia = cref_agrupada, no
        // cnofact -- ver FotoSyncWorker.
        List<FotoConTipo> todasLasFotos = (await conn.QueryAsync<FotoConTipo>(
            @"SELECT idfotos AS IdFotos,
                     nlineno AS NLineNo,
                     ntipodocto AS NTipoDocto,
                     DATE_FORMAT(fcrea, '%d/%m/%Y %H:%i:%s') AS FCrea
              FROM fotos
              WHERE creferencia = @cref AND ntipodocto IN (107, 103)
              ORDER BY nlineno",
            new { cref })).ToList();

        return new RevisionShareResumen
        {
            CrefAgrupada = cref,
            NombreCliente = (string?)resumen.nom_cliente ?? "",
            TotalFacturas = (int)resumen.total_facturas,
            FacturasCompletas = (int)resumen.facturas_completas,
            TotalPartidas = (int)resumen.total_partidas,
            PartidasRevisadas = (int)resumen.partidas_revisadas,
            GeneradoEn = link.Value.GeneradoEn,
            Expira = link.Value.Expira,
            Facturas = facturas,
            FotosNormales = todasLasFotos
                .Where(f => f.NTipoDocto == 107)
                .Select(f => new RevisionShareFoto
                {
                    IdFotos = f.IdFotos,
                    NLineNo = f.NLineNo,
                    FCrea = f.FCrea ?? string.Empty
                })
                .ToList(),
            FotosDiscrepancia = todasLasFotos
                .Where(f => f.NTipoDocto == 103)
                .Select(f => new RevisionShareFoto
                {
                    IdFotos = f.IdFotos,
                    NLineNo = f.NLineNo,
                    FCrea = f.FCrea ?? string.Empty
                })
                .ToList()
        };
    }

    private class FotoConTipo
    {
        public int IdFotos { get; set; }
        public int NLineNo { get; set; }
        public int NTipoDocto { get; set; }
        public string? CExtension { get; set; }
        public string? FCrea { get; set; }
    }

    public async Task<(string FilePath, string Extension)?> ObtenerFotoAsync(string token, int idFotos)
    {
        (string Cref, DateTime GeneradoEn, DateTime Expira)? link = await ResolverAsync(token);
        if (link == null)
        {
            return null;
        }
        string cref = link.Value.Cref;

        using IDbConnection conn = _db.CreateConnection();

        // Clase tipada (no dynamic) a propósito -- ver el comentario en
        // FotoController.VerFoto sobre por qué QueryFirstOrDefaultAsync<object>
        // ya causó fallos silenciosos en este proyecto por mayúsculas/
        // minúsculas de columnas sin alias.
        FotoVerInfo? foto = await conn.QueryFirstOrDefaultAsync<FotoVerInfo>(
            @"SELECT TRIM(f.creferencia) AS Creferencia,
                     f.cextension AS CExtension,
                     f.nlineno AS NLineNo,
                     f.ntipodocto AS NTipoDocto,
                     COALESCE(
                         NULLIF(fa.cclienteidtraf, ''),
                         NULLIF(ff.cclienteid, '')
                     ) AS Cidcte
              FROM fotos f
              LEFT JOIN traf fa ON fa.creferencia = f.creferencia
              LEFT JOIN fact ff
                ON ff.cnofact = f.creferencia
                OR ff.cref_agrupada = f.creferencia
                OR ff.creferencia = f.creferencia
              -- El token solo abre fotos de ESTA revisión (cref) -- nunca
              -- cualquier idfotos -- ya sea evidencia normal (107) o de
              -- discrepancia (103).
              WHERE f.idfotos = @idFotos AND f.creferencia = @cref AND f.ntipodocto IN (107, 103)
              LIMIT 1",
            new { idFotos, cref });

        if (foto == null)
        {
            return null;
        }

        string cidcte = (foto.Cidcte ?? "").Trim();
        string sqlCte = "SELECT COALESCE(NULLIF(carpeta,''), cidcte) FROM clientes WHERE cidcte = @id LIMIT 1";
        string folder = ((await conn.ExecuteScalarAsync<string>(sqlCte, new { id = cidcte })) ?? cidcte).Trim();

        string ext = PhotoFileNameResolver.NormalizeExtension(foto.CExtension);
        string? filePath = PhotoFileNameResolver.FindExistingPath(
            _basePath,
            folder,
            foto.Creferencia,
            foto.NTipoDocto,
            foto.NLineNo,
            ext);

        return filePath == null ? null : (filePath, ext);
    }

    public async Task<(string CrefAgrupada, List<(string FilePath, string FileName)> Archivos)?> ObtenerArchivosParaZipAsync(string token)
    {
        (string Cref, DateTime GeneradoEn, DateTime Expira)? link = await ResolverAsync(token);
        if (link == null)
        {
            return null;
        }
        string cref = link.Value.Cref;

        using IDbConnection conn = _db.CreateConnection();

        string cidcte = (await conn.ExecuteScalarAsync<string?>(
            "SELECT cclienteid FROM fact WHERE cref_agrupada = @cref LIMIT 1", new { cref })) ?? "";
        string folder = ((await conn.ExecuteScalarAsync<string>(
            "SELECT COALESCE(NULLIF(carpeta,''), cidcte) FROM clientes WHERE cidcte = @id LIMIT 1",
            new { id = cidcte })) ?? cidcte).Trim();

        List<FotoConTipo> filas = (await conn.QueryAsync<FotoConTipo>(
            @"SELECT idfotos AS IdFotos, nlineno AS NLineNo, ntipodocto AS NTipoDocto, cextension AS CExtension
              FROM fotos
              WHERE creferencia = @cref AND ntipodocto IN (107, 103)
              ORDER BY ntipodocto, nlineno",
            new { cref })).ToList();

        string safeRef = Regex.Replace(cref, @"[^\w-]", "");
        var archivos = new List<(string FilePath, string FileName)>();
        int seqNormal = 0;
        int seqDiscrepancia = 0;

        foreach (FotoConTipo foto in filas)
        {
            string ext = PhotoFileNameResolver.NormalizeExtension(foto.CExtension);
            string? filePath = PhotoFileNameResolver.FindExistingPath(
                _basePath,
                folder,
                cref,
                foto.NTipoDocto,
                foto.NLineNo,
                ext);
            if (filePath == null)
            {
                continue;
            }

            // Mismo criterio de renombrado que en el correo de finalización
            // (RevisionReportService) -- para que el revisor/cliente identifiquen
            // a simple vista cuáles fotos son evidencia normal y cuáles de
            // discrepancia, aunque las bajen sueltas del ZIP.
            bool esDiscrepancia = foto.NTipoDocto == 103;
            int seq = esDiscrepancia ? ++seqDiscrepancia : ++seqNormal;
            string prefijo = esDiscrepancia ? "DISCREPANCIA" : "Evidencia";
            string nombreDescarga = $"{prefijo}-{seq:D2}-{safeRef}.{ext}";

            archivos.Add((filePath, nombreDescarga));
        }

        return (cref, archivos);
    }

    /// <summary>Valida el token contra pmmutileria y regresa el cref_agrupada, o null si venció/no existe.</summary>
    private async Task<(string Cref, DateTime GeneradoEn, DateTime Expira)?> ResolverAsync(string token)
    {
        using IDbConnection utilConn = _db.CreatePmmutileriaConnection();

        dynamic? link = await utilConn.QueryFirstOrDefaultAsync<object>(
            @"SELECT cref_agrupada, fcrea, fexpira, revocado
              FROM revision_links_compartidos
              WHERE token = @token
              LIMIT 1",
            new { token });

        if (link == null)
        {
            return null;
        }

        bool revocado = Convert.ToInt32(link.revocado) == 1;
        DateTime expira = (DateTime)link.fexpira;
        if (revocado || DateTime.Now > expira)
        {
            return null;
        }

        return ((string)link.cref_agrupada, (DateTime)link.fcrea, expira);
    }
}
