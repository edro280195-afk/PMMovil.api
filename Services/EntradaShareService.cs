using System.Data;
using System.IO;
using System.Text.RegularExpressions;

using Dapper;

using Microsoft.Extensions.Configuration;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public interface IEntradaShareService
{
    Task<(string Token, DateTime Expira)?> GenerarLinkAsync(string cref, string usuario);
    Task<EntradaShareResumen?> ObtenerResumenAsync(string token);

    /// <summary>Foto de EVIDENCIA de la entrada (ntipodocto 100).</summary>
    Task<(string FilePath, string Extension)?> ObtenerFotoAsync(string token, int linea);

    /// <summary>Foto de DAÑO de la entrada (ntipodocto 103): solo si está marcada como daño de entrada.</summary>
    Task<(string FilePath, string Extension)?> ObtenerFotoDanioAsync(string token, int linea);

    /// <summary>Archivos para el ZIP, con carpetas «Daño» y «Evidencia». Null si el enlace no existe o no hay fotos.</summary>
    Task<(string Cref, List<(string FilePath, string FileName)> Archivos)?> ObtenerArchivosParaZipAsync(string token);
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
/// Fotos de daño: viven en <c>fotos</c> con <c>ntipodocto = 103</c> («DAÑADA» en <c>tipodocumentos</c>, formulario
/// PREOP04), el apartado de daño de siempre; no hay tabla aparte. Ojo: Revisión también guarda ahí sus discrepancias sobre
/// la misma referencia (comparten apartado en el sistema legado). Por eso la página solo muestra y sirve esas fotos cuando
/// la entrada misma se declaró dañada (<c>traf3.danada = 1</c>); sin esa declaración el 103 no sale en este enlace.
///
/// El token vive en pmmutileria (tabla de soporte de la feature, no dato de
/// negocio) -- todo lo demás (traf/clientes/transpor/fotos) sigue en
/// pmmtraf, que es donde realmente vive. Ver Controllers/PublicoController.cs.
/// </summary>
public class EntradaShareService : IEntradaShareService
{
    private static readonly TimeSpan Vigencia = TimeSpan.FromHours(72);

    private const int TipoEvidencia = 100;
    private const int TipoDanio = 103;

    private readonly BaseRepository _db;
    private readonly ILogger<EntradaShareService> _logger;

    // Las fotos se leen por el almacén: una recién subida aún no está en el 23 y el enlace debe mostrarla igual.
    private readonly ClientePhotoStorage _fotos;

    public EntradaShareService(BaseRepository db, IConfiguration config, ILogger<EntradaShareService> logger, ClientePhotoStorage fotos)
    {
        _db = db;
        _logger = logger;
        _fotos = fotos;
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
                     t.fcreatrafico AS Fcreatrafico,
                     CAST(COALESCE(t3.danada, 0) AS SIGNED) AS Danada,
                     t3.danada_descripcion AS DanadaDescripcion
              FROM traf t
              LEFT JOIN clientes c ON c.cidcte = t.cclienteidtraf
              LEFT JOIN transpor tr ON tr.cidtranspor = t.cidtransportraf
              LEFT JOIN traf3 t3 ON t3.fk_idtraf = t.idtraf
              WHERE t.creferencia = @cref
              LIMIT 1",
            new { cref });

        if (entrada == null)
        {
            return null;
        }

        List<EntradaShareFoto> fotos = (await trafConn.QueryAsync<FotoListaRow>(
            @"SELECT nlineno AS NLineNo, DATE_FORMAT(fcrea, '%H:%i') AS Hora
              FROM fotos WHERE creferencia = @cref AND ntipodocto = 100 ORDER BY nlineno",
            new { cref }))
            .Select(f => new EntradaShareFoto { NLineNo = f.NLineNo, Caption = $"Foto {f.NLineNo}", Hora = f.Hora ?? string.Empty })
            .ToList();

        // Daño: solo si la entrada se declaró dañada, y solo las fotos marcadas como daño DE ENTRADA.
        bool danada = entrada.Danada == 1;
        List<EntradaShareFoto> fotosDanio = new();
        int pendientes = 0;
        if (danada)
        {
            fotosDanio = await ListarFotosDanioAsync(trafConn, cref);
            int? esperadas = await EntradaDanioSeguimiento.LeerEsperadasAsync(_db, _logger, cref);
            pendientes = EntradaDanioSeguimiento.Faltantes(true, esperadas, fotosDanio.Count);
        }

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
            Fotos = fotos,
            Danada = danada,
            DanadaDescripcion = entrada.DanadaDescripcion?.Trim() ?? string.Empty,
            FotosDanio = fotosDanio,
            FotosDanioPendientes = pendientes
        };
    }

    /// <summary>
    /// Fotos de daño de la entrada: las de <c>fotos</c> con <c>ntipodocto = 103</c> («DAÑADA» en <c>tipodocumentos</c>), el
    /// mismo criterio que ya usa FotoController. Solo se llama si la entrada se declaró dañada.
    /// </summary>
    private static async Task<List<EntradaShareFoto>> ListarFotosDanioAsync(IDbConnection conn, string cref)
    {
        return (await conn.QueryAsync<FotoListaRow>(
            @"SELECT nlineno AS NLineNo, DATE_FORMAT(fcrea, '%H:%i') AS Hora
              FROM fotos WHERE creferencia = @cref AND ntipodocto = 103 ORDER BY nlineno",
            new { cref }))
            .Select(f => new EntradaShareFoto { NLineNo = f.NLineNo, Caption = $"Daño {f.NLineNo}", Hora = f.Hora ?? string.Empty })
            .ToList();
    }

    /// <summary>Fila cruda de traf/clientes/transpor/traf3 para ObtenerResumenAsync -- solo uso interno.</summary>
    private class EntradaResumenRow
    {
        public string Creferencia { get; set; } = string.Empty;
        public string? NombreCliente { get; set; }
        public string? NombreTransporte { get; set; }
        public string? Cnocaja { get; set; }
        public string? Bodega { get; set; }
        public string? Planta { get; set; }
        public DateTime? Fcreatrafico { get; set; }
        public int Danada { get; set; }
        public string? DanadaDescripcion { get; set; }
    }

    /// <summary>Número de línea y hora de subida de una foto -- solo uso interno.</summary>
    private class FotoListaRow
    {
        public int NLineNo { get; set; }
        public string? Hora { get; set; }
    }

    public Task<(string FilePath, string Extension)?> ObtenerFotoAsync(string token, int linea)
        => ObtenerFotoPorTipoAsync(token, linea, TipoEvidencia);

    public Task<(string FilePath, string Extension)?> ObtenerFotoDanioAsync(string token, int linea)
        => ObtenerFotoPorTipoAsync(token, linea, TipoDanio);

    private async Task<(string FilePath, string Extension)?> ObtenerFotoPorTipoAsync(string token, int linea, int tipoDocto)
    {
        (string Cref, DateTime GeneradoEn, DateTime Expira)? link = await ResolverAsync(token);
        if (link == null)
        {
            return null;
        }
        string cref = link.Value.Cref;

        using IDbConnection trafConn = _db.CreateConnection();

        // El tipo 103 («DAÑADA») solo se sirve si la entrada misma se declaró dañada (traf3.danada = 1), igual que la página
        // solo lo lista en ese caso: así este enlace no entrega fotos 103 de una entrada que no declaró daño.
        // Clase tipada (no dynamic) a propósito -- ver el comentario en FotoController.VerFoto sobre por qué
        // QueryFirstOrDefaultAsync<object> ya causó fallos silenciosos en este proyecto por mayúsculas/minúsculas.
        string sql = tipoDocto == TipoDanio
            ? @"SELECT f.idfotos AS IdFotos,
                       TRIM(f.creferencia) AS Creferencia,
                       f.cextension AS CExtension,
                       f.nlineno AS NLineNo,
                       f.ntipodocto AS NTipoDocto,
                       t.cclienteidtraf AS Cidcte
                FROM fotos f
                JOIN traf t ON t.creferencia = f.creferencia
                JOIN traf3 t3 ON t3.fk_idtraf = t.idtraf AND t3.danada = 1
                WHERE f.creferencia = @cref AND f.nlineno = @linea AND f.ntipodocto = 103
                ORDER BY f.idfotos DESC
                LIMIT 1"
            : @"SELECT f.idfotos AS IdFotos,
                       TRIM(f.creferencia) AS Creferencia,
                       f.cextension AS CExtension,
                       f.nlineno AS NLineNo,
                       f.ntipodocto AS NTipoDocto,
                       t.cclienteidtraf AS Cidcte
                FROM fotos f
                JOIN traf t ON t.creferencia = f.creferencia
                WHERE f.creferencia = @cref AND f.nlineno = @linea AND f.ntipodocto = 100
                ORDER BY f.idfotos DESC
                LIMIT 1";

        FotoVerInfo? foto = await trafConn.QueryFirstOrDefaultAsync<FotoVerInfo>(sql, new { cref, linea });

        if (foto == null)
        {
            return null;
        }

        return await ResolverArchivoAsync(trafConn, foto);
    }

    /// <summary>
    /// Misma carpeta que FotoController.VerFotoEntradaCore (se replica para no acoplar el controlador público al
    /// autenticado); el archivo lo da el almacén: lo local si la foto es reciente, si no el 23.
    /// </summary>
    private async Task<(string FilePath, string Extension)?> ResolverArchivoAsync(IDbConnection conn, FotoVerInfo foto)
    {
        string cidcte = (foto.Cidcte ?? "").Trim();
        string sqlCte = "SELECT COALESCE(NULLIF(carpeta,''), cidcte) FROM clientes WHERE cidcte = @id LIMIT 1";
        string carpeta = ((await conn.ExecuteScalarAsync<string>(sqlCte, new { id = cidcte })) ?? cidcte).Trim();

        string ext = PhotoFileNameResolver.NormalizeExtension(foto.CExtension);
        string? filePath = _fotos.BuscarOriginal(
            new FotoCliente(foto.IdFotos, carpeta, foto.Creferencia, foto.NTipoDocto, foto.NLineNo, ext));

        return filePath == null ? null : (filePath, ext);
    }

    public async Task<(string Cref, List<(string FilePath, string FileName)> Archivos)?> ObtenerArchivosParaZipAsync(string token)
    {
        EntradaShareResumen? resumen = await ObtenerResumenAsync(token);
        if (resumen == null)
        {
            return null;
        }

        string referenciaArchivo = Regex.Replace(resumen.CrefAgrupada, @"[^\w-]", "_");
        var archivos = new List<(string FilePath, string FileName)>();

        foreach (EntradaShareFoto foto in resumen.FotosDanio)
        {
            (string FilePath, string Extension)? archivo = await ObtenerFotoDanioAsync(token, foto.NLineNo);
            if (archivo != null)
            {
                archivos.Add((archivo.Value.FilePath, $"{CarpetaZipDanio}/danio-{referenciaArchivo}-{foto.NLineNo:D4}.{archivo.Value.Extension}"));
            }
        }

        foreach (EntradaShareFoto foto in resumen.Fotos)
        {
            (string FilePath, string Extension)? archivo = await ObtenerFotoAsync(token, foto.NLineNo);
            if (archivo != null)
            {
                archivos.Add((archivo.Value.FilePath, $"{CarpetaZipEvidencia}/evidencia-{referenciaArchivo}-{foto.NLineNo:D4}.{archivo.Value.Extension}"));
            }
        }

        return archivos.Count == 0 ? null : (resumen.CrefAgrupada, archivos);
    }

    /// <summary>Nombres de las carpetas dentro del ZIP (visibles en la descarga: «Daño» y «Evidencia»).</summary>
    public const string CarpetaZipDanio = "Daño";
    public const string CarpetaZipEvidencia = "Evidencia";

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
