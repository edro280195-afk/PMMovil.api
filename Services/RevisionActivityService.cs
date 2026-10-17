using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

using Dapper;

using Microsoft.Extensions.Logging;

using PMMovil.Api.Data;

namespace PMMovil.Api.Services;

public static class RevisionActivityTypes
{
    public const string CambioActividad = "CAMBIO_ACTIVIDAD";
    public const string InicioRevision = "INICIO_REVISION";
    public const string TomaDocumentos = "TOMA_DOCUMENTOS";
    public const string CampoFactura = "CAMPO_FACTURA";
    public const string ObservacionGeneral = "OBSERVACION_GENERAL";
    public const string CampoPartida = "CAMPO_PARTIDA";
    public const string PartidaRevisada = "PARTIDA_REVISADA";
    public const string PartidaAgregada = "PARTIDA_AGREGADA";
    public const string PartidaEliminada = "PARTIDA_ELIMINADA";
    public const string DiscrepanciasAclaradas = "DISCREPANCIAS_ACLARADAS";
    public const string FacturaCompletada = "FACTURA_COMPLETADA";
    public const string FacturaDiscrepancia = "FACTURA_DISCREPANCIA";
    public const string LecturaCodigo = "LECTURA_CODIGO";
    public const string HazmatRegistrado = "HAZMAT_REGISTRADO";
    public const string Pausada = "PAUSADA";
    public const string Finalizada = "FINALIZADA";
}

public sealed class RevisionActivityEntry
{
    public long Id { get; init; }
    public string Creferencia { get; init; } = string.Empty;
    public string Usuario { get; init; } = string.Empty;
    public string Tipo { get; init; } = string.Empty;
    public string? Cnofact { get; init; }
    public int? IdRevdet { get; init; }
    public string? Detalle { get; init; }
    public DateTime Fecha { get; init; }
    public string Origen { get; init; } = string.Empty;
}

public sealed class RevisionActivityService
{
    private const string TableName = "revision_actividad";
    private const string PresenceActivityType = "CAMBIO_ACTIVIDAD";
    private const string PresenceActivityDetail = "Actividad en revisión";

    private readonly BaseRepository _db;
    private readonly ILogger<RevisionActivityService> _logger;

    public RevisionActivityService(BaseRepository db, ILogger<RevisionActivityService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<long?> RegistrarAsync(
        string creferencia,
        string usuario,
        string tipo,
        string? cnofact = null,
        int? idRevdet = null,
        string? detalle = null,
        string origen = "PMMOVIL")
    {
        if (string.IsNullOrWhiteSpace(creferencia) ||
            string.IsNullOrWhiteSpace(usuario) ||
            string.IsNullOrWhiteSpace(tipo))
        {
            return null;
        }

        try
        {
            using IDbConnection conn = _db.CreatePmmutileriaConnection();
            string sql = $@"
                INSERT INTO {TableName}
                    (creferencia, usuario, tipo, cnofact, idrevdet, detalle, origen, fecha)
                VALUES
                    (@creferencia, @usuario, @tipo, @cnofact, @idRevdet, @detalle, @origen, NOW());
                SELECT LAST_INSERT_ID();";

            return await conn.ExecuteScalarAsync<long>(sql, new
            {
                creferencia,
                usuario,
                tipo,
                cnofact = string.IsNullOrWhiteSpace(cnofact) ? null : cnofact,
                idRevdet,
                detalle = LimitDetail(detalle),
                origen = string.IsNullOrWhiteSpace(origen) ? "PMMOVIL" : origen
            });
        }
        catch (Exception exception)
        {
            // La bitácora nunca debe impedir guardar una revisión. Si la tabla
            // aún no fue migrada o la base está temporalmente indisponible,
            // registramos el problema y conservamos el flujo operativo.
            _logger.LogWarning(
                exception,
                "No se pudo guardar actividad de revisión. Referencia={Creferencia}, Usuario={Usuario}, Tipo={Tipo}",
                creferencia,
                usuario,
                tipo);
            return null;
        }
    }

    public async Task<IReadOnlyList<RevisionActivityEntry>> ObtenerAsync(
        string? creferencia,
        string? usuario,
        DateTime? desde,
        DateTime? hasta,
        long? antesDe,
        int limite)
    {
        int safeLimit = Math.Clamp(limite, 1, 100);

        try
        {
            using IDbConnection conn = _db.CreatePmmutileriaConnection();
            string sql = $@"
                SELECT
                    id AS Id,
                    creferencia AS Creferencia,
                    usuario AS Usuario,
                    tipo AS Tipo,
                    cnofact AS Cnofact,
                    idrevdet AS IdRevdet,
                    detalle AS Detalle,
                    fecha AS Fecha,
                    origen AS Origen
                FROM {TableName}
                WHERE (@creferencia IS NULL OR creferencia = @creferencia)
                  AND (@usuario IS NULL OR usuario = @usuario)
                  AND (@desde IS NULL OR fecha >= @desde)
                  AND (@hasta IS NULL OR fecha < @hasta)
                  AND (@antesDe IS NULL OR id < @antesDe)
                  AND NOT (tipo = @presenceActivityType AND COALESCE(detalle, '') = @presenceActivityDetail)
                ORDER BY id DESC
                LIMIT @limite";

            return (await conn.QueryAsync<RevisionActivityEntry>(sql, new
            {
                creferencia = NullIfEmpty(creferencia),
                usuario = NullIfEmpty(usuario),
                desde,
                hasta,
                antesDe,
                presenceActivityType = PresenceActivityType,
                presenceActivityDetail = PresenceActivityDetail,
                limite = safeLimit
            })).ToList();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No se pudo consultar la bitácora de actividad de revisiones.");
            return Array.Empty<RevisionActivityEntry>();
        }
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? LimitDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string detail = value.Trim();
        return detail.Length <= 255 ? detail : detail[..255];
    }
}
