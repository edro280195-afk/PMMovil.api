using System.Data;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Dapper;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public interface ICasetaShareService
{
    Task<(string Token, DateTime Expira)?> GenerarLinkEntradaAsync(string casetaId, string usuario);
    Task<(string Token, DateTime Expira)?> GenerarLinkSalidaAsync(string relno, string usuario);
    Task<CasetaShareResumen?> ObtenerResumenAsync(string token);
    Task<(string FilePath, string Extension)?> ObtenerFotoAsync(string token, int idFotos);
    Task<List<(string FilePath, string FileName)>?> ObtenerArchivosParaZipAsync(string token);
}

/// <summary>
/// Genera y resuelve enlaces públicos temporales para la evidencia de entrada
/// y salida de caseta. Cada token queda limitado a una referencia y tipo de foto.
/// </summary>
public sealed class CasetaShareService : ICasetaShareService
{
    public const string TipoEntrada = "entrada";
    public const string TipoSalida = "salida";

    private const int VigenciaMeses = 2;
    private const int TipoDocumentoEntrada = 106;
    private const int TipoDocumentoSalida = 101;

    private readonly BaseRepository _db;
    private readonly CasetaPhotoStorage _photoStorage;
    private readonly ILogger<CasetaShareService> _logger;

    public CasetaShareService(
        BaseRepository db,
        CasetaPhotoStorage photoStorage,
        ILogger<CasetaShareService> logger)
    {
        _db = db;
        _photoStorage = photoStorage;
        _logger = logger;
    }

    public Task<(string Token, DateTime Expira)?> GenerarLinkEntradaAsync(string casetaId, string usuario) =>
        GenerarLinkAsync(TipoEntrada, casetaId, usuario);

    public Task<(string Token, DateTime Expira)?> GenerarLinkSalidaAsync(string relno, string usuario) =>
        GenerarLinkAsync(TipoSalida, relno, usuario);

    private async Task<(string Token, DateTime Expira)?> GenerarLinkAsync(string tipo, string referencia, string usuario)
    {
        string referenciaNormalizada = referencia.Trim();
        if (referenciaNormalizada.Length == 0)
        {
            return null;
        }

        int tipoDocumento = ObtenerTipoDocumento(tipo);
        using IDbConnection conn = _db.CreateConnection();
        int fotos = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM fotos WHERE creferencia = @referencia AND ntipodocto = @tipoDocumento",
            new { referencia = referenciaNormalizada, tipoDocumento });
        if (fotos == 0)
        {
            return null;
        }

        string token = Guid.NewGuid().ToString("N");
        string tokenHash = ShareTokenHasher.Hash(token);
        string tokenHint = token[..8];
        DateTime expira = DateTime.Now.AddMonths(VigenciaMeses);

        using IDbConnection utilConn = _db.CreatePmmutileriaConnection();
        await utilConn.ExecuteAsync(
            @"INSERT INTO caseta_fotos_links_compartidos
                (token, token_hash, tipo, referencia, usucrea, fexpira)
              VALUES (@tokenHint, @tokenHash, @tipo, @referencia, @usuario, @expira)",
            new
            {
                tokenHint,
                tokenHash,
                tipo,
                referencia = referenciaNormalizada,
                usuario = string.IsNullOrWhiteSpace(usuario) ? "sistema-correo" : usuario.Trim(),
                expira
            });

        _logger.LogInformation(
            "Enlace público de fotos de caseta generado. Tipo={Tipo}, Referencia={Referencia}, Expira={Expira}",
            tipo,
            referenciaNormalizada,
            expira);
        return (token, expira);
    }

    public async Task<CasetaShareResumen?> ObtenerResumenAsync(string token)
    {
        CasetaShareTarget? target = await ResolverAsync(token);
        if (target == null)
        {
            return null;
        }

        List<CasetaShareFoto> fotos = await ObtenerFotosAsync(target);
        if (fotos.Count == 0)
        {
            return null;
        }

        // El detalle es un extra: si no se puede leer, la página abre igual con sus fotos.
        CasetaShareDetalle? detalle = null;
        try
        {
            detalle = await ObtenerDetalleAsync(target.Tipo, target.Referencia);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer el detalle del enlace de caseta. Tipo={Tipo}, Referencia={Referencia}", target.Tipo, target.Referencia);
        }

        return new CasetaShareResumen
        {
            Referencia = target.Referencia,
            Tipo = target.Tipo,
            GeneradoEn = target.GeneradoEn,
            Expira = target.Expira,
            Fotos = fotos,
            Detalle = detalle
        };
    }

    private async Task<List<CasetaShareFoto>> ObtenerFotosAsync(CasetaShareTarget target)
    {
        using IDbConnection conn = _db.CreateConnection();
        return (await conn.QueryAsync<CasetaShareFoto>(
            @"SELECT idfotos AS IdFotos,
                     nlineno AS Indice,
                     COALESCE(DATE_FORMAT(fcrea, '%d/%m/%Y %H:%i:%s'), '') AS Fecha
              FROM fotos
              WHERE creferencia = @referencia AND ntipodocto = @tipoDocumento
              ORDER BY nlineno",
            new
            {
                referencia = target.Referencia,
                tipoDocumento = ObtenerTipoDocumento(target.Tipo)
            })).ToList();
    }

    /// <summary>
    /// Lo que acompaña a las fotos en la página pública: los datos de la relación en una salida, o los del registro de
    /// caseta en una entrada. Solo lecturas. Sin clientes ni pedimentos a propósito (ver <see cref="CasetaShareDetalle"/>).
    /// </summary>
    public async Task<CasetaShareDetalle?> ObtenerDetalleAsync(string tipo, string referencia)
    {
        string clave = (referencia ?? string.Empty).Trim();
        if (clave.Length == 0)
        {
            return null;
        }

        using IDbConnection conn = _db.CreateConnection();
        return tipo switch
        {
            TipoSalida => await ObtenerDetalleSalidaAsync(conn, clave),
            TipoEntrada => await ObtenerDetalleEntradaAsync(conn, clave),
            _ => null
        };
    }

    private static async Task<CasetaShareDetalle?> ObtenerDetalleSalidaAsync(IDbConnection conn, string relno)
    {
        // Misma regla que CasetaService.ResolverTabla: una `C` en la tercera posición es relacion_imex.
        string tabla = relno.Length > 2 && relno[2] == 'C' ? "relacion_imex" : "relacion";
        SalidaDetalleRow? rel = await conn.QueryFirstOrDefaultAsync<SalidaDetalleRow>(
            $@"SELECT IFNULL(r.caja, '') AS Caja,
                      IFNULL(t.cnombre, '') AS Transportista,
                      IFNULL(r.op_transfer, '') AS Operador,
                      IFNULL(r.num_tractor, '') AS Tractor,
                      TRIM(CONCAT_WS(' ', NULLIF(TRIM(r.sellos), ''), NULLIF(TRIM(r.sellos2), ''),
                                          NULLIF(TRIM(r.sellos3), ''), NULLIF(TRIM(r.sellos4), ''))) AS Sellos,
                      CASE WHEN YEAR(r.autoriza_carga) >= 2000 THEN r.autoriza_carga END AS Autorizada,
                      CASE WHEN YEAR(r.salida_caseta) >= 2000 THEN r.salida_caseta END AS Salida
               FROM {tabla} r
               LEFT JOIN tranfers t ON r.cidtransfer = t.cidtransfer
               WHERE r.relno = @relno
               LIMIT 1",
            new { relno });

        if (rel == null)
        {
            return null;
        }

        // La caja pudo no registrarse nunca en caseta: entonces no hay llegada y el recorrido empieza en la autorización.
        DateTime? llegada = await conn.ExecuteScalarAsync<DateTime?>(
            @"SELECT MIN(CASE WHEN YEAR(farribo) >= 2000 THEN farribo END)
              FROM caseta
              WHERE TRIM(relacion) = @relno",
            new { relno });

        return new CasetaShareDetalle
        {
            Caja = rel.Caja.Trim(),
            Transportista = rel.Transportista.Trim(),
            Operador = rel.Operador.Trim(),
            Tractor = rel.Tractor.Trim(),
            Sellos = rel.Sellos.Trim(),
            Autorizada = rel.Autorizada,
            Salida = rel.Salida,
            Llegada = llegada
        };
    }

    private static async Task<CasetaShareDetalle?> ObtenerDetalleEntradaAsync(IDbConnection conn, string casetaId)
    {
        EntradaDetalleRow? reg = await conn.QueryFirstOrDefaultAsync<EntradaDetalleRow>(
            @"SELECT IFNULL(nequipo, '') AS Caja,
                     IFNULL(transpore, '') AS Transportista,
                     IFNULL(placas, '') AS Placas,
                     CONCAT_WS(' · ', NULLIF(TRIM(tipoop), ''), NULLIF(TRIM(tipomov), '')) AS Movimiento,
                     CASE WHEN YEAR(farribo) >= 2000 THEN farribo END AS Llegada,
                     CASE WHEN YEAR(fcrea) >= 2000 THEN fcrea END AS Registrada
              FROM caseta
              WHERE casetaid = @casetaId
              LIMIT 1",
            new { casetaId });

        if (reg == null)
        {
            return null;
        }

        return new CasetaShareDetalle
        {
            Caja = reg.Caja.Trim(),
            Transportista = reg.Transportista.Trim(),
            Placas = reg.Placas.Trim(),
            Movimiento = (reg.Movimiento ?? string.Empty).Trim(),
            Llegada = reg.Llegada,
            Registrada = reg.Registrada
        };
    }

    public async Task<(string FilePath, string Extension)?> ObtenerFotoAsync(string token, int idFotos)
    {
        CasetaShareTarget? target = await ResolverAsync(token);
        if (target == null || idFotos <= 0)
        {
            return null;
        }

        using IDbConnection conn = _db.CreateConnection();
        CasetaSharePhotoFile? foto = await conn.QueryFirstOrDefaultAsync<CasetaSharePhotoFile>(
            @"SELECT nlineno AS Indice,
                     COALESCE(NULLIF(cextension, ''), 'jpg') AS Extension
              FROM fotos
              WHERE idfotos = @idFotos
                AND creferencia = @referencia
                AND ntipodocto = @tipoDocumento
              LIMIT 1",
            new
            {
                idFotos,
                referencia = target.Referencia,
                tipoDocumento = ObtenerTipoDocumento(target.Tipo)
            });

        if (foto == null)
        {
            return null;
        }

        string extension = NormalizarExtension(foto.Extension);
        string? filePath = _photoStorage.FindReadablePhotoPath(target.Referencia, foto.Indice, extension);
        return filePath != null && File.Exists(filePath) ? (filePath, extension) : null;
    }

    public async Task<List<(string FilePath, string FileName)>?> ObtenerArchivosParaZipAsync(string token)
    {
        CasetaShareTarget? target = await ResolverAsync(token);
        if (target == null)
        {
            return null;
        }

        List<CasetaShareFoto> fotos = await ObtenerFotosAsync(target);
        if (fotos.Count == 0)
        {
            return null;
        }

        string referenciaArchivo = Regex.Replace(target.Referencia, @"[^\w-]", "_");
        var archivos = new List<(string FilePath, string FileName)>();
        foreach (CasetaShareFoto foto in fotos)
        {
            (string FilePath, string Extension)? archivo = await ObtenerFotoAsync(token, foto.IdFotos);
            if (archivo == null)
            {
                continue;
            }

            string nombre = $"{target.Tipo}-{referenciaArchivo}-{foto.Indice:D4}.{archivo.Value.Extension}";
            archivos.Add((archivo.Value.FilePath, nombre));
        }

        return archivos;
    }

    private async Task<CasetaShareTarget?> ResolverAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        using IDbConnection utilConn = _db.CreatePmmutileriaConnection();
        string tokenHash = ShareTokenHasher.Hash(token);
        CasetaShareTarget? target = await utilConn.QueryFirstOrDefaultAsync<CasetaShareTarget>(
            @"SELECT tipo AS Tipo,
                     referencia AS Referencia,
                     fcrea AS GeneradoEn,
                     fexpira AS Expira,
                     revocado AS Revocado
              FROM caseta_fotos_links_compartidos
              WHERE token_hash = @tokenHash
              LIMIT 1",
            new { tokenHash });

        if (target == null || target.Revocado || DateTime.Now > target.Expira)
        {
            return null;
        }

        if (target.Tipo != TipoEntrada && target.Tipo != TipoSalida)
        {
            return null;
        }

        return target;
    }

    private static int ObtenerTipoDocumento(string tipo) => tipo switch
    {
        TipoEntrada => TipoDocumentoEntrada,
        TipoSalida => TipoDocumentoSalida,
        _ => throw new ArgumentOutOfRangeException(nameof(tipo), "El tipo de evidencia de caseta no es válido.")
    };

    private static string NormalizarExtension(string? extension)
    {
        string normalizada = (extension ?? "jpg").Trim().TrimStart('.').ToLowerInvariant();
        return normalizada is "png" or "webp" or "jpeg" ? normalizada : "jpg";
    }

    private sealed class CasetaShareTarget
    {
        public string Tipo { get; set; } = string.Empty;
        public string Referencia { get; set; } = string.Empty;
        public DateTime GeneradoEn { get; set; }
        public DateTime Expira { get; set; }
        public bool Revocado { get; set; }
    }

    private sealed class SalidaDetalleRow
    {
        public string Caja { get; set; } = string.Empty;
        public string Transportista { get; set; } = string.Empty;
        public string Operador { get; set; } = string.Empty;
        public string Tractor { get; set; } = string.Empty;
        public string Sellos { get; set; } = string.Empty;
        public DateTime? Autorizada { get; set; }
        public DateTime? Salida { get; set; }
    }

    private sealed class EntradaDetalleRow
    {
        public string Caja { get; set; } = string.Empty;
        public string Transportista { get; set; } = string.Empty;
        public string Placas { get; set; } = string.Empty;
        public string? Movimiento { get; set; }
        public DateTime? Llegada { get; set; }
        public DateTime? Registrada { get; set; }
    }

    private sealed class CasetaSharePhotoFile
    {
        public int Indice { get; set; }
        public string Extension { get; set; } = string.Empty;
    }
}
