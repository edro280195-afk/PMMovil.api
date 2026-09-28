using System.Data;
using System.IO;

using Dapper;

using Microsoft.Extensions.Configuration;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public interface IEntradaShareService
{
    Task<(string Token, DateTime Expira)?> GenerarLinkAsync(string cref, string usuario);
    Task<EntradaShareResumen?> ObtenerResumenAsync(string token);
    Task<(string FilePath, string Extension)?> ObtenerFotoAsync(string token, int linea);
}

/// <summary>
/// "Seguimiento de entrada": genera y sirve los links públicos que puede
/// abrir cualquiera (un ejecutivo, un cliente) sin sesión de PMMovil, para
/// ver los datos y las fotos de la ENTRADA sin pasar por VFP.
///
/// Entrada y Revisión son procesos distintos y consecutivos sobre el mismo
/// cref_agrupada: la entrada es cuando la caja/mercancía llega a bodega
/// (tabla traf); la revisión pasa después, por factura (tablas
/// fact/revdet). Esta página muestra SOLO datos de la entrada -- nunca
/// estatus de revisión, revisor asignado ni nada que viva en fact/revdet.
/// Si algún día hace falta "Seguimiento de revisión", es una página aparte,
/// no un campo más aquí.
///
/// El token vive en pmmutileria (tabla de soporte de la feature, no dato de
/// negocio) -- todo lo demás (traf/clientes/transpor/fotos) sigue en
/// pmmtraf, que es donde realmente vive. Ver Controllers/PublicoController.cs.
/// </summary>
public class EntradaShareService : IEntradaShareService
{
    private static readonly TimeSpan Vigencia = TimeSpan.FromHours(72);

    private readonly BaseRepository _db;
    private readonly ILogger<EntradaShareService> _logger;
    private readonly string _basePath;

    public EntradaShareService(BaseRepository db, IConfiguration config, ILogger<EntradaShareService> logger)
    {
        _db = db;
        _logger = logger;
        _basePath = config["PhotoSettings:BasePath"] ?? "F:\\Trafico\\fotos";
    }

    public async Task<(string Token, DateTime Expira)?> GenerarLinkAsync(string cref, string usuario)
    {
        using IDbConnection trafConn = _db.CreateConnection();

        // La entrada debe existir de verdad -- no generamos links para
        // referencias inventadas.
        int? existe = await trafConn.QueryFirstOrDefaultAsync<int?>(
            "SELECT 1 FROM traf WHERE creferencia = @cref LIMIT 1", new { cref });
        if (existe == null)
        {
            return null;
        }

        string token = Guid.NewGuid().ToString("N");
        // El token real ya NO se guarda en texto plano -- solo su hash
        // (para validar) y un "hint" de 8 caracteres en la columna legacy
        // `token` (mismo criterio que monitor_tv_links/token_hint), por si
        // algún día hace falta ubicar un enlace a simple vista en soporte.
        // Si pmmutileria alguna vez se filtra, un enlace vigente ya no queda
        // usable de inmediato solo con leer la tabla.
        string tokenHash = ShareTokenHasher.Hash(token);
        string tokenHint = token[..8];
        DateTime expira = DateTime.Now.Add(Vigencia);

        using IDbConnection utilConn = _db.CreatePmmutileriaConnection();
        await utilConn.ExecuteAsync(
            @"INSERT INTO entrada_links_compartidos (token, token_hash, cref_agrupada, usucrea, fexpira)
              VALUES (@tokenHint, @tokenHash, @cref, @usuario, @expira)",
            new { tokenHint, tokenHash, cref, usuario, expira });

        _logger.LogInformation("Link de seguimiento generado para {Cref} por {Usuario}, expira {Expira}", cref, usuario, expira);
        return (token, expira);
    }

    public async Task<EntradaShareResumen?> ObtenerResumenAsync(string token)
    {
        (string Cref, DateTime GeneradoEn, DateTime Expira)? link = await ResolverAsync(token);
        if (link == null)
        {
            return null;
        }
        string cref = link.Value.Cref;

        using IDbConnection trafConn = _db.CreateConnection();

        // Clase tipada (no dynamic) a propósito -- ver el comentario en
        // FotoController.VerFoto sobre por qué QueryFirstOrDefaultAsync<object>
        // ya causó fallos silenciosos en este proyecto por mayúsculas/
        // minúsculas de columnas sin alias.
        EntradaResumenRow? entrada = await trafConn.QueryFirstOrDefaultAsync<EntradaResumenRow>(
            @"SELECT t.creferencia AS Creferencia,
                     c.cnombre AS NombreCliente,
                     tr.cnombre AS NombreTransporte,
                     t.cnocaja AS Cnocaja,
                     t.bodega AS Bodega,
                     t.planta AS Planta,
                     t.fcreatrafico AS Fcreatrafico
              FROM traf t
              LEFT JOIN clientes c ON c.cidcte = t.cclienteidtraf
              LEFT JOIN transpor tr ON tr.cidtranspor = t.cidtransportraf
              WHERE t.creferencia = @cref
              LIMIT 1",
            new { cref });

        if (entrada == null)
        {
            return null;
        }

        List<EntradaShareFoto> fotos = (await trafConn.QueryAsync<int>(
            @"SELECT nlineno FROM fotos WHERE creferencia = @cref AND ntipodocto = 100 ORDER BY nlineno",
            new { cref }))
            .Select(n => new EntradaShareFoto { NLineNo = n, Caption = $"Foto {n}" })
            .ToList();

        return new EntradaShareResumen
        {
            CrefAgrupada = cref,
            NombreCliente = entrada.NombreCliente ?? "",
            NombreTransportista = entrada.NombreTransporte ?? "",
            Unidad = entrada.Cnocaja ?? "",
            Ubicacion = string.Join(" · ", new[] { entrada.Bodega, entrada.Planta }
                .Where(s => !string.IsNullOrWhiteSpace(s))),
            FechaLlegada = entrada.Fcreatrafico != null
                ? entrada.Fcreatrafico.Value.ToString("d MMM, HH:mm")
                : null,
            GeneradoEn = link.Value.GeneradoEn,
            Expira = link.Value.Expira,
            Fotos = fotos
        };
    }

    /// <summary>Fila cruda de traf/clientes/transpor para ObtenerResumenAsync -- solo uso interno.</summary>
    private class EntradaResumenRow
    {
        public string Creferencia { get; set; } = string.Empty;
        public string? NombreCliente { get; set; }
        public string? NombreTransporte { get; set; }
        public string? Cnocaja { get; set; }
        public string? Bodega { get; set; }
        public string? Planta { get; set; }
        public DateTime? Fcreatrafico { get; set; }
    }

    public async Task<(string FilePath, string Extension)?> ObtenerFotoAsync(string token, int linea)
    {
        (string Cref, DateTime GeneradoEn, DateTime Expira)? link = await ResolverAsync(token);
        if (link == null)
        {
            return null;
        }
        string cref = link.Value.Cref;

        using IDbConnection trafConn = _db.CreateConnection();

        // Clase tipada (no dynamic) a propósito -- ver el comentario en
        // FotoController.VerFoto sobre por qué QueryFirstOrDefaultAsync<object>
        // ya causó fallos silenciosos en este proyecto por mayúsculas/
        // minúsculas de columnas sin alias.
        FotoVerInfo? foto = await trafConn.QueryFirstOrDefaultAsync<FotoVerInfo>(
            @"SELECT TRIM(f.creferencia) AS Creferencia,
                     f.cextension AS CExtension,
                     f.nlineno AS NLineNo,
                     f.ntipodocto AS NTipoDocto,
                     t.cclienteidtraf AS Cidcte
              FROM fotos f
              JOIN traf t ON t.creferencia = f.creferencia
              WHERE f.creferencia = @cref AND f.nlineno = @linea AND f.ntipodocto = 100
              LIMIT 1",
            new { cref, linea });

        if (foto == null)
        {
            return null;
        }

        // Misma construcción de ruta que FotoController.VerFotoEntradaCore --
        // no se toca esa lógica, solo se replica para no acoplar el
        // controlador público al autenticado.
        string cidcte = (foto.Cidcte ?? "").Trim();
        string sqlCte = "SELECT COALESCE(NULLIF(carpeta,''), cidcte) FROM clientes WHERE cidcte = @id LIMIT 1";
        string carpeta = ((await trafConn.ExecuteScalarAsync<string>(sqlCte, new { id = cidcte })) ?? cidcte).Trim();

        string ext = PhotoFileNameResolver.NormalizeExtension(foto.CExtension);
        string? filePath = PhotoFileNameResolver.FindExistingPath(
            _basePath,
            carpeta,
            foto.Creferencia,
            foto.NTipoDocto,
            foto.NLineNo,
            ext);

        return filePath == null ? null : (filePath, ext);
    }

    /// <summary>Valida el token contra pmmutileria y regresa el cref_agrupada, o null si venció/no existe.</summary>
    private async Task<(string Cref, DateTime GeneradoEn, DateTime Expira)?> ResolverAsync(string token)
    {
        using IDbConnection utilConn = _db.CreatePmmutileriaConnection();
        string tokenHash = ShareTokenHasher.Hash(token);

        // Clase tipada (no dynamic) a propósito -- cref_agrupada tiene guion
        // bajo y sin alias explícito Dapper NO lo mapea a CrefAgrupada (ver
        // memoria "Dapper snake_case mapping": no está activado
        // MatchNamesWithUnderscores en este proyecto).
        EntradaLinkRow? link = await utilConn.QueryFirstOrDefaultAsync<EntradaLinkRow>(
            @"SELECT cref_agrupada AS CrefAgrupada, fcrea AS Fcrea, fexpira AS Fexpira, revocado AS Revocado
              FROM entrada_links_compartidos
              WHERE token_hash = @tokenHash
              LIMIT 1",
            new { tokenHash });

        if (link == null)
        {
            return null;
        }

        if (link.Revocado || DateTime.Now > link.Fexpira)
        {
            return null;
        }

        return (link.CrefAgrupada, link.Fcrea, link.Fexpira);
    }

    /// <summary>Fila cruda de entrada_links_compartidos para ResolverAsync -- solo uso interno.</summary>
    private class EntradaLinkRow
    {
        public string CrefAgrupada { get; set; } = string.Empty;
        public DateTime Fcrea { get; set; }
        public DateTime Fexpira { get; set; }
        public bool Revocado { get; set; }
    }
}
