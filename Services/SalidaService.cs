using System.Data;

using Dapper;

using Microsoft.AspNetCore.SignalR;

using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public class SalidaService
{
    private const int VentanaCasetaQrSegundos = 10;
    private const string TablaAutorizacionCasetaQr = "salida_carga_qr";

    private readonly BaseRepository _db;
    private readonly ISmtpService _smtp;
    private readonly IConfiguration _config;
    private readonly ILogger<SalidaService> _logger;
    private readonly IHubContext<WarehouseHub> _hub;
    private readonly IRealtimeNotificationService _notifications;
    private const string RelacionSelectFields = @"
   relno AS Relno,
   STATUS AS Estatus,
   estatus_carga AS EstatusCarga,
   usucargamcia AS Usucargamcia,
   usuedit AS Usuedit,
   version_hoja AS VersionHoja,
   cidtransfer AS Cidtransfer,
   op_transfer AS OpTransfer,
   caja AS Caja,
   usuautoriza AS Usuautoriza,
   sellos AS Sellos,
   num_tractor AS NumTractor,
   gafete_op AS GafeteOp,
   planta AS Planta";

    public SalidaService(
   BaseRepository db,
   ISmtpService smtp,
   IConfiguration config,
   ILogger<SalidaService> logger,
   IHubContext<WarehouseHub> hub,
   IRealtimeNotificationService notifications)
    {
        _db = db;
        _smtp = smtp;
        _config = config;
        _logger = logger;
        _hub = hub;
        _notifications = notifications;
    }

    private sealed record ScanSpeedReview(bool Bloqueado, int EtiquetasBloqueadas, string Motivo);

    private sealed class CasetaQrDbRow
    {
        public string CasetaId { get; set; } = string.Empty;
        public string Relacion { get; set; } = string.Empty;
    }

    private sealed class CasetaQrAuthorizationDbRow
    {
        public long Id { get; set; }
        public string Estado { get; set; } = string.Empty;
        public int Expirada { get; set; }
    }

    private sealed record CasetaQrAuthorizationValidation(
        bool Valida,
        string Tipo,
        string Mensaje,
        long Id = 0);


    private static (string tabla, string sufijo) ResolverTabla(string relno)
    {
        char sufijo = relno.Length > 2 ? relno[2] : 'I';
        string tabla = sufijo == 'C' ? "relacion_imex" : "relacion";
        return (tabla, sufijo == 'C' ? "C" : "I");
    }

    private static string ConvShort(string etiqueta)
    {
        if (string.IsNullOrEmpty(etiqueta)) return etiqueta ?? "";
        try
        {
            if (etiqueta.Length >= 3 && etiqueta[..3] == "OK-")
            {
                if (etiqueta.Length >= 18)
                    return "OK-" + etiqueta[5..8] + " " + etiqueta[8..16] + etiqueta[18..];
                return etiqueta[3..];
            }
            else
            {
                if (etiqueta.Length >= 15)
                    return etiqueta[2..5] + " " + etiqueta[5..13] + etiqueta[15..];
                return etiqueta;
            }
        }
        catch { return etiqueta; }
    }

    private async Task EnsureAutorizacionCasetaQrAsync(IDbConnection conn)
    {
        await conn.ExecuteAsync($@"
CREATE TABLE IF NOT EXISTS {TablaAutorizacionCasetaQr} (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    relno VARCHAR(32) NOT NULL,
    casetaid VARCHAR(64) NOT NULL,
    usuario VARCHAR(100) NOT NULL,
    token CHAR(32) NOT NULL,
    estado VARCHAR(20) NOT NULL DEFAULT 'PENDIENTE',
    leido_at DATETIME NOT NULL,
    expira_at DATETIME NOT NULL,
    consumido_at DATETIME NULL,
    texto_bulto VARCHAR(255) NULL,
    motivo VARCHAR(100) NULL,
    ip_address VARCHAR(45) NOT NULL DEFAULT '',
    PRIMARY KEY (id),
    UNIQUE KEY uq_salida_carga_qr_token (token),
    KEY idx_salida_carga_qr_pendiente (relno, usuario, estado, expira_at),
    KEY idx_salida_carga_qr_caseta (casetaid, leido_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
    }

    private async Task<CasetaQrDbRow?> GetCasetaQrAsync(
        IDbConnection conn,
        string casetaId,
        IDbTransaction? transaction = null)
    {
        var caseta = await conn.QueryFirstOrDefaultAsync<CasetaQrDbRow>(
            @"SELECT casetaid AS CasetaId,
                     COALESCE(NULLIF(TRIM(relacion), ''), '') AS Relacion
              FROM caseta
              WHERE casetaid = @casetaId
              LIMIT 1",
            new { casetaId },
            transaction);

        if (caseta == null || !string.IsNullOrWhiteSpace(caseta.Relacion))
            return caseta;

        var relacionPorEntrada = await conn.QueryFirstOrDefaultAsync<string>(
            @"SELECT NULLIF(TRIM(t2.nrelacion), '')
              FROM traf3 t3
              INNER JOIN traf t ON t.idtraf = t3.fk_idtraf
              INNER JOIN traf2 t2 ON t2.creferencia = t.creferencia
              WHERE t3.ccasetaid = @casetaId
              ORDER BY t2.nrelacion DESC
              LIMIT 1",
            new { casetaId },
            transaction);

        caseta.Relacion = relacionPorEntrada ?? string.Empty;
        return caseta;
    }

    private async Task<bool> CasetaPerteneceAEntradaAsync(
        IDbConnection conn,
        string casetaId,
        string entrada,
        IDbTransaction? transaction = null)
    {
        var count = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*)
              FROM traf3
              WHERE ccasetaid = @casetaId
                AND creferencia = @entrada",
            new { casetaId, entrada },
            transaction);

        return count > 0;
    }

    private async Task InvalidatePendingCasetaQrAsync(
        IDbConnection conn,
        string relno,
        string usuario,
        string motivo)
    {
        if (string.IsNullOrWhiteSpace(relno)) return;

        await conn.ExecuteAsync(
            $@"UPDATE {TablaAutorizacionCasetaQr}
               SET estado = CASE WHEN expira_at <= NOW() THEN 'EXPIRADO' ELSE 'REEMPLAZADO' END,
                   consumido_at = NOW(),
                   motivo = CASE WHEN expira_at <= NOW() THEN 'TIEMPO_EXCEDIDO' ELSE @motivo END
               WHERE relno = @relno AND usuario = @usuario AND estado = 'PENDIENTE'",
            new { relno, usuario, motivo });
    }

    public async Task<ValidarCasetaQrResult> ValidarCasetaQrAsync(
        ValidarCasetaQrRequest request,
        string usuario)
    {
        var result = new ValidarCasetaQrResult();
        string casetaId = request.CasetaId?.Trim() ?? string.Empty;
        string requestedRelno = request.Relno?.Trim() ?? string.Empty;
        string requestedEntrada = request.Entrada?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(casetaId) || casetaId.Length > 64)
        {
            result.Tipo = "QR_INVALIDO";
            result.Message = "QR DE CASETA INVALIDO";
            return result;
        }

        using IDbConnection conn = _db.CreateConnection();
        await EnsureAutorizacionCasetaQrAsync(conn);

        string pendingRelno = string.IsNullOrWhiteSpace(requestedRelno)
            ? requestedEntrada
            : requestedRelno;
        await InvalidatePendingCasetaQrAsync(conn, pendingRelno, usuario, "NUEVO_INTENTO_QR");

        var caseta = await GetCasetaQrAsync(conn, casetaId);
        if (caseta == null)
        {
            result.Tipo = "QR_NO_ENCONTRADO";
            result.Message = "NO SE ENCONTRO LA ETIQUETA DE CASETA";
            return result;
        }

        bool entradaDirectaValida = false;
        string entradaParaValidar = string.IsNullOrWhiteSpace(requestedEntrada)
            ? requestedRelno
            : requestedEntrada;

        if (!string.IsNullOrWhiteSpace(entradaParaValidar) &&
            entradaParaValidar.Length > 2 &&
            char.ToUpperInvariant(entradaParaValidar[2]) == 'C')
        {
            entradaDirectaValida = await CasetaPerteneceAEntradaAsync(conn, casetaId, entradaParaValidar);
        }

        string relno = requestedRelno;
        if (string.IsNullOrWhiteSpace(relno))
        {
            relno = entradaDirectaValida && !string.IsNullOrWhiteSpace(requestedEntrada)
                ? requestedEntrada
                : caseta.Relacion.Trim();
            if (string.IsNullOrWhiteSpace(relno))
            {
                result.CasetaId = casetaId;
                result.Tipo = "QR_SIN_RELACION";
                result.Message = "LA ETIQUETA DE CASETA AUN NO TIENE RELACION ASIGNADA";
                return result;
            }
        }
        else if (!string.IsNullOrWhiteSpace(caseta.Relacion) &&
                 !string.Equals(caseta.Relacion.Trim(), relno, StringComparison.OrdinalIgnoreCase))
        {
            result.CasetaId = casetaId;
            result.Relno = relno;
            result.Tipo = "QR_OTRA_RELACION";
            result.Message = "EL QR DE CASETA PERTENECE A OTRA RELACION";
            return result;
        }
        else if (string.IsNullOrWhiteSpace(caseta.Relacion) && !entradaDirectaValida)
        {
            result.CasetaId = casetaId;
            result.Relno = relno;
            result.Tipo = "QR_SIN_RELACION";
            result.Message = "EL QR DE CASETA NO ESTA RELACIONADO CON ESTA ENTRADA";
            return result;
        }

        CargarRelacionResult? cargaInicial = null;
        if (string.IsNullOrWhiteSpace(requestedRelno) && !entradaDirectaValida)
        {
            cargaInicial = await CargarRelacionAsync(relno, "", usuario);
            if (!cargaInicial.Success)
            {
                result.CasetaId = casetaId;
                result.Relno = relno;
                result.Tipo = "RELACION_NO_DISPONIBLE";
                result.Message = cargaInicial.Message;
                return result;
            }
        }

        string token = Guid.NewGuid().ToString("N");
        string ipAddress = request.IpAddress?.Trim() ?? string.Empty;

        conn.Open();
        using var transaction = conn.BeginTransaction();
        try
        {
            await conn.ExecuteAsync(
                $@"UPDATE {TablaAutorizacionCasetaQr}
                   SET estado = CASE WHEN expira_at <= NOW() THEN 'EXPIRADO' ELSE 'REEMPLAZADO' END,
                       consumido_at = NOW(),
                       motivo = CASE WHEN expira_at <= NOW() THEN 'TIEMPO_EXCEDIDO' ELSE 'NUEVO_QR' END
                   WHERE relno = @relno AND usuario = @usuario AND estado = 'PENDIENTE'",
                new { relno, usuario },
                transaction);

            await conn.ExecuteAsync(
                $@"INSERT INTO {TablaAutorizacionCasetaQr}
                   (relno, casetaid, usuario, token, estado, leido_at, expira_at, ip_address)
                   VALUES (@relno, @casetaId, @usuario, @token, 'PENDIENTE',
                           NOW(), DATE_ADD(NOW(), INTERVAL {VentanaCasetaQrSegundos} SECOND), @ipAddress)",
                new { relno, casetaId, usuario, token, ipAddress },
                transaction);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        result.Success = true;
        result.Tipo = "CASETA_QR_OK";
        result.Message = $"QR DE CASETA VALIDO. ESCANEA EL BULTO EN {VentanaCasetaQrSegundos} SEGUNDOS";
        result.Relno = relno;
        result.CasetaId = casetaId;
        result.AutorizacionToken = token;
        result.ExpiraEnSegundos = VentanaCasetaQrSegundos;

        if (cargaInicial != null)
        {
            result.Relacion = cargaInicial.Relacion;
            result.Etiquetas = cargaInicial.Etiquetas;
            result.EstatusCarga = cargaInicial.EstatusCarga;
            result.TotBultos = cargaInicial.TotBultos;
            result.VersionHoja = cargaInicial.VersionHoja;
        }

        return result;
    }


    public async Task<CargarRelacionResult> CargarRelacionAsync(string rawRelno, string version, string usuario)
    {
        var result = new CargarRelacionResult();
        string relno = rawRelno?.Trim() ?? "";

        using IDbConnection conn = _db.CreateConnection();

        // Inteligencia de escaneo: una etiqueta de caseta puede ser corta
        // (por ejemplo E-12185/26), por lo que no se debe identificar por
        // longitud. Se intenta resolver cualquier código exacto en caseta.
        if (!string.IsNullOrWhiteSpace(relno))
        {
            var caseta = await GetCasetaQrAsync(conn, relno);
            var resolvedRelno = caseta?.Relacion?.Trim();

            if (!string.IsNullOrEmpty(resolvedRelno))
            {
                relno = resolvedRelno;
            }
            else if (caseta != null)
            {
                result.Message = "ETIQUETA DE CASETA SIN RELACION ASIGNADA";
                return result;
            }
        }

        if (relno.Length < 5 || relno.Length > 15)
        {
            result.Message = "NO ES RELACION";
            return result;
        }

        char idx2 = relno.Length > 2 ? relno[2] : ' ';
        if (idx2 != 'I' && idx2 != 'C')
        {
            result.Message = "NO ES RELACION";
            return result;
        }

        var sufijo = idx2 == 'C' ? "C" : "I";
        var tabla = idx2 == 'C' ? "relacion_imex" : "relacion";

        if (relno.EndsWith("P"))
        {
            result.Message = "EN PROCESO";
            return result;
        }

        string relnoBase = relno;
        string versionHoja = version;
        if (relno.Contains('-'))
        {
            var parts = relno.Split('-', 2);
            relnoBase = parts[0];
            if (string.IsNullOrEmpty(versionHoja)) versionHoja = parts[1];
        }

        var dbRel = await conn.QueryFirstOrDefaultAsync<SalidaDbRelation>(
        $"SELECT {RelacionSelectFields} FROM {tabla} WHERE TIPOP=@sufijo AND relno=@relno",
        new { relno = relnoBase, sufijo });

        if (dbRel == null)
        {
            result.Message = "NO ES RELACION";
            return result;
        }

        if (!string.IsNullOrEmpty(dbRel.Usuedit) ||
     (dbRel.EstatusCarga == "CARGANDO" && dbRel.Usucargamcia != usuario))
        {
            result.Message = "RELACION OCUPADA";
            return result;
        }

        if (!string.IsNullOrEmpty(dbRel.VersionHoja) && !string.IsNullOrEmpty(versionHoja) &&
     dbRel.VersionHoja != versionHoja)
        {
            result.Message = "VERSION DE RELACION NO ACTUALIZADA";
            return result;
        }

        string estatusCargaOriginal = dbRel.EstatusCarga;

        var estadosCerrados = new[] { "CARGADA", "AUTORIZADA", "SALIDA", "DEMASIADO RAPIDO", "BLOQUEO", "BLOQUEO REIMPRESION" };
        bool puedeTomarRelacion =
        (dbRel.Estatus == "COMPLETA" || dbRel.Estatus == "PRECARGA") &&
        !estadosCerrados.Contains(estatusCargaOriginal);

        if (puedeTomarRelacion)
        {
            string fechaStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            await conn.ExecuteAsync(
            $"UPDATE {tabla} SET ESTATUS_CARGA='CARGANDO', usucargamcia=@usuario, fechacruce=@fecha WHERE RELNO=@relno",
            new { usuario, fecha = fechaStr, relno = relnoBase });

            dbRel.Usucargamcia = usuario;
        }

        var etiquetas = await GetEtiqEntradasAsync(conn, relnoBase, sufijo);

        var (totBultos, manejo) = await GetTotBultosAsync(conn, relnoBase, sufijo);
        var (cliente, idCliente) = await GetClienteRelacionAsync(conn, relnoBase);
        string estatusCarga = estatusCargaOriginal;

        string estatusMercancia = ComputeEstatusMercancia(dbRel.Estatus, estatusCargaOriginal, etiquetas, totBultos, manejo);

        result.Success = true;
        result.Relacion = new RelacionInfo
        {
            Relno = relnoBase,
            Estatus = estatusMercancia,
            EstatusCarga = estatusCarga,
            Manejo = manejo,
            Cliente = cliente,
            IdCliente = idCliente,
            CidTransfer = dbRel.Cidtransfer,
            OpTransfer = dbRel.OpTransfer,
            Caja = dbRel.Caja,
            UsucargaMcia = dbRel.Usucargamcia,
            UsuAutoriza = dbRel.Usuautoriza,
            Sellos = dbRel.Sellos,
            NumTractor = dbRel.NumTractor,
            GafeteOp = dbRel.GafeteOp,
            Planta = dbRel.Planta,
            VersionHoja = dbRel.VersionHoja
        };
        result.Etiquetas = etiquetas;
        result.EstatusCarga = estatusCarga;
        result.TotBultos = totBultos;
        result.VersionHoja = dbRel.VersionHoja;

        return result;
    }

    public async Task<CargarRelacionResult> GetDetalleSupervisorAsync(string relno)
    {
        var result = new CargarRelacionResult();

        if (relno.Length < 5 || relno.Length > 15)
        {
            result.Message = "NO ES RELACION";
            return result;
        }

        char idx2 = relno.Length > 2 ? relno[2] : ' ';
        if (idx2 != 'I' && idx2 != 'C')
        {
            result.Message = "NO ES RELACION";
            return result;
        }

        string sufijo = idx2 == 'C' ? "C" : "I";
        string tabla = idx2 == 'C' ? "relacion_imex" : "relacion";

        using IDbConnection conn = _db.CreateConnection();

        var dbRel = await conn.QueryFirstOrDefaultAsync<SalidaDbRelation>(
        $"SELECT {RelacionSelectFields} FROM {tabla} WHERE TIPOP=@sufijo AND relno=@relno",
        new { relno, sufijo });

        if (dbRel == null)
        {
            result.Message = "NO ES RELACION";
            return result;
        }

        var etiquetas = await GetEtiqEntradasAsync(conn, relno, sufijo);
        var (totBultos, manejo) = await GetTotBultosAsync(conn, relno, sufijo);
        var (cliente, idCliente) = await GetClienteRelacionAsync(conn, relno);

        string estatus = dbRel.Estatus;
        if (!string.IsNullOrWhiteSpace(dbRel.Usuedit))
            estatus = "RELACION OCUPADA";
        if (etiquetas.Any(e => string.Equals(e.Estatus, "ERROR", StringComparison.OrdinalIgnoreCase)))
            estatus = "ERROR, retorne relacion, posible descargo";

        result.Success = true;
        result.Relacion = new RelacionInfo
        {
            Relno = relno,
            Estatus = estatus,
            EstatusCarga = dbRel.EstatusCarga,
            Manejo = manejo,
            Cliente = cliente,
            IdCliente = idCliente,
            CidTransfer = dbRel.Cidtransfer,
            OpTransfer = dbRel.OpTransfer,
            Caja = dbRel.Caja,
            UsucargaMcia = dbRel.Usucargamcia,
            UsuAutoriza = dbRel.Usuautoriza,
            Sellos = dbRel.Sellos,
            NumTractor = dbRel.NumTractor,
            GafeteOp = dbRel.GafeteOp,
            Planta = dbRel.Planta,
            VersionHoja = dbRel.VersionHoja
        };
        result.Etiquetas = etiquetas;
        result.EstatusCarga = dbRel.EstatusCarga;
        result.TotBultos = totBultos;
        result.VersionHoja = dbRel.VersionHoja;

        return result;
    }

    private static async Task<(string Cliente, string IdCliente)> GetClienteRelacionAsync(IDbConnection conn, string relno)
    {
        var row = await conn.QueryFirstOrDefaultAsync(@"
   SELECT
  IF(COUNT(DISTINCT traf.cclienteidtraf) > 1, 'varios', MAX(clientes.cnombre)) AS Cliente,
  MAX(traf.cclienteidtraf) AS IdCliente
   FROM traf2
   LEFT JOIN traf ON traf.creferencia = traf2.creferencia
   LEFT JOIN clientes ON traf.cclienteidtraf = clientes.cidcte
   WHERE traf2.nrelacion = @relno
   GROUP BY traf2.nrelacion", new { relno });

        if (row == null) return ("", "");
        string cliente = row.Cliente?.ToString() ?? "";
        if (cliente.Length > 10) cliente = cliente[..10];
        return (cliente, row.IdCliente?.ToString() ?? "");
    }

    private static bool IsManejoExento(string? manejo)
    {
        if (string.IsNullOrEmpty(manejo)) return false;
        var exentos = new[] { "DIRECTO", "TRANSBORDO", "PIPA" };
        return exentos.Any(e => manejo.Contains(e, StringComparison.OrdinalIgnoreCase));
    }

    private static string ComputeEstatusMercancia(
string estatusBase, string estatusCarga, List<EtiquetaInfo> etiquetas, int totBultos, string? manejo)
    {
        string estatus = estatusBase;
        bool cargaCerrada = estatusCarga is "CARGADA" or "AUTORIZADA" or "SALIDA";

        if (!cargaCerrada && etiquetas.Any(e => e.Estatus == "SALIDA"))
            estatus = "INFORMACION INCORRECTA EN RELACION, NOTIFICAR A ENCARGADO";

        var otra = etiquetas.FirstOrDefault(e => e.Estatus == "ERROR");
        if (otra != null)
            estatus = "INFORMACION INCORRECTA EN RELACION, NOTIFICAR A ENCARGADO, ENTRADA DE OTRA RELACION AGRUPADA " + otra.Creferencia;

        if (totBultos != etiquetas.Count && !IsManejoExento(manejo))
            estatus = "DIFERENCIA EN BULTOS-ETIQUETAS";

        if (etiquetas.Count == 0)
            estatus = "NO DISP ETIQUETAS EN MCIA";

        return estatus;
    }


    private async Task<(int Bultos, string Manejo)> GetTotBultosAsync(IDbConnection conn, string relno, string sufijo)
    {
        string sql;
        if (sufijo == "C")
        {
            sql = @"SELECT FORMAT(SUM(bultos),0) AS bultos, ANY_VALUE(manejo) AS manejo FROM (
  SELECT relno AS rel1, trafdet.ncantidad AS bultos, ANY_VALUE(cdescmanejo) AS manejo
  FROM relacion_imex
  LEFT JOIN traf2 ON nrelacion=relno
  LEFT JOIN agrupa ON traf2.creferencia=agrupa.cref_agrupada
  LEFT JOIN traf ON agrupa.cref_agrupada=traf.creferencia
  LEFT JOIN manejo ON manejo.cidmanejo=traf.cidmanejo
  LEFT JOIN trafdet ON traf.creferencia=trafdet.creferencia
  WHERE relno=@relno GROUP BY idtrafdet
   ) AS tabla GROUP BY rel1";
        }
        else
        {
            sql = @"SELECT FORMAT(SUM(bultos),0) AS bultos, ANY_VALUE(manejo) AS manejo FROM (
  SELECT relno AS rel1, trafdet.ncantidad AS bultos, ANY_VALUE(cdescmanejo) AS manejo
  FROM relacion
  LEFT JOIN traf2 ON nrelacion=relno
  LEFT JOIN agrupa ON traf2.creferencia=agrupa.creferencia
  LEFT JOIN traf ON agrupa.cref_agrupada=traf.creferencia
  LEFT JOIN manejo ON manejo.cidmanejo=traf.cidmanejo
  LEFT JOIN trafdet ON traf.creferencia=trafdet.creferencia
  WHERE relno=@relno GROUP BY idtrafdet
   ) AS tabla GROUP BY rel1";
        }

        var row = await conn.QueryFirstOrDefaultAsync<TotBultosDbRow>(sql, new { relno });
        if (row == null || string.IsNullOrEmpty(row.Bultos)) return (0, row?.Manejo ?? "");
        int n = int.TryParse(row.Bultos.Replace(",", ""), out int parsed) ? parsed : 0;
        return (n, row.Manejo ?? "");
    }


    private async Task<List<EtiquetaInfo>> GetEtiqEntradasAsync(IDbConnection conn, string relno, string sufijo)
    {
        string sql;
        if (sufijo == "C")
        {
            sql = @"SELECT e.id_etiqueta_mcia AS IdEtiquetaMcia,
 e.textoetiqueta AS Textoetiqueta,
 e.creferencia AS Creferencia,
 e.nlineno AS Nlineno,
 e.total AS Total,
 IF(YEAR(e.time_scan) > 0, e.time_scan, NULL) AS TimeScan,
 e.estatus AS Estatus,
 e.usucarga AS Usucarga,
 e.relacion AS Relacion,
  ANY_VALUE(c.cidmanejo) AS Cidmanejo,
  e.historial AS Historial
  FROM traf2
  LEFT JOIN agrupa ON traf2.creferencia = agrupa.cref_agrupada
  LEFT JOIN traf c ON c.creferencia = agrupa.cref_agrupada
  LEFT JOIN etiqueta_mcia AS e ON c.creferencia = e.creferencia
  WHERE nrelacion = @relno AND textoetiqueta <> ''
  GROUP BY e.id_etiqueta_mcia";
        }
        else
        {
            sql = @"SELECT e.id_etiqueta_mcia AS IdEtiquetaMcia,
 e.textoetiqueta AS Textoetiqueta,
 e.creferencia AS Creferencia,
 e.nlineno AS Nlineno,
 e.total AS Total,
 IF(YEAR(e.time_scan) > 0, e.time_scan, NULL) AS TimeScan,
 e.estatus AS Estatus,
 e.usucarga AS Usucarga,
 e.relacion AS Relacion,
 ANY_VALUE(c.cidmanejo) AS Cidmanejo,
 e.historial AS Historial
  FROM traf2
  LEFT JOIN agrupa ON traf2.creferencia = agrupa.creferencia
   LEFT JOIN traf c ON c.creferencia = agrupa.cref_agrupada
   LEFT JOIN etiqueta_mcia AS e ON c.creferencia = e.creferencia
   WHERE nrelacion = @relno AND textoetiqueta <> ''
   GROUP BY e.id_etiqueta_mcia";
        }

        var rows = (await conn.QueryAsync<EtiquetaDbRow>(sql, new { relno })).ToList();

        string checkSql = @"SELECT e.id_etiqueta_mcia AS IdEtiquetaMcia,
  e.textoetiqueta AS Textoetiqueta,
  e.creferencia AS Creferencia,
  e.nlineno AS Nlineno,
  e.total AS Total,
  IF(YEAR(e.time_scan) > 0, e.time_scan, NULL) AS TimeScan,
  e.estatus AS Estatus,
  e.usucarga AS Usucarga,
  e.relacion AS Relacion,
   ANY_VALUE(t.cidmanejo) AS Cidmanejo,
   e.historial AS Historial
   FROM etiqueta_mcia AS e
   LEFT JOIN traf AS t ON e.creferencia = t.creferencia
   WHERE e.relacion = @relno AND textoetiqueta <> ''
   GROUP BY e.id_etiqueta_mcia";

        var scannedRows = (await conn.QueryAsync<EtiquetaDbRow>(checkSql, new { relno })).ToList();

        var result = new List<EtiquetaInfo>();
        var seenTextos = new HashSet<string>();

        foreach (var row in scannedRows)
        {
            if (seenTextos.Contains(row.Textoetiqueta)) continue;
            seenTextos.Add(row.Textoetiqueta);
            result.Add(MapEtiqueta(row, true));
        }

        foreach (var row in rows)
        {
            if (seenTextos.Contains(row.Textoetiqueta)) continue;
            seenTextos.Add(row.Textoetiqueta);
            string estatus = row.Relacion == relno ? "CARGADO" :
            string.IsNullOrEmpty(row.Relacion) ? "" : "ERROR";
            result.Add(new EtiquetaInfo
            {
                TextoEtiqueta = row.Textoetiqueta,
                TextoPagina = ConvShort(row.Textoetiqueta),
                Creferencia = row.Creferencia,
                Nlineno = row.Nlineno,
                Total = row.Total,
                Escaneado = row.Relacion == relno,
                Estatus = estatus,
                Usucarga = row.Usucarga,
                TimeScan = row.TimeScan,
                Manejo = row.Cidmanejo,
                Historial = row.Historial
            });
        }

        result.Sort((a, b) => string.Compare(a.TextoPagina, b.TextoPagina, StringComparison.Ordinal));
        return result;
    }

    private static EtiquetaInfo MapEtiqueta(EtiquetaDbRow row, bool escaneado)
    {
        return new EtiquetaInfo
        {
            TextoEtiqueta = row.Textoetiqueta,
            TextoPagina = ConvShort(row.Textoetiqueta),
            Creferencia = row.Creferencia,
            Nlineno = row.Nlineno,
            Total = row.Total,
            Escaneado = escaneado,
            Estatus = row.Estatus,
            Usucarga = row.Usucarga,
            TimeScan = row.TimeScan,
            Manejo = row.Cidmanejo,
            Historial = row.Historial
        };
    }


    private async Task<CasetaQrAuthorizationValidation> ValidateCasetaQrAuthorizationAsync(
        IDbConnection conn,
        IDbTransaction transaction,
        string relno,
        string token,
        string usuario)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new CasetaQrAuthorizationValidation(
                false,
                "QR_REQUERIDO",
                "PRIMERO ESCANEA EL QR DE CASETA");
        }

        var authorization = await conn.QueryFirstOrDefaultAsync<CasetaQrAuthorizationDbRow>(
            $@"SELECT id AS Id,
                      estado AS Estado,
                      IF(expira_at <= NOW(), 1, 0) AS Expirada
               FROM {TablaAutorizacionCasetaQr}
               WHERE token = @token
                 AND relno = @relno
                 AND usuario = @usuario
               LIMIT 1
               FOR UPDATE",
            new { token, relno, usuario },
            transaction);

        if (authorization == null)
        {
            return new CasetaQrAuthorizationValidation(
                false,
                "SECUENCIA_INVALIDA",
                "EL QR DE CASETA YA NO ES VALIDO. ESCANEALO DE NUEVO");
        }

        if (authorization.Expirada == 1 && authorization.Estado == "PENDIENTE")
        {
            await conn.ExecuteAsync(
                $@"UPDATE {TablaAutorizacionCasetaQr}
                   SET estado = 'EXPIRADO', consumido_at = NOW(), motivo = 'TIEMPO_EXCEDIDO'
                   WHERE id = @id",
                new { authorization.Id },
                transaction);

            return new CasetaQrAuthorizationValidation(
                false,
                "QR_EXPIRADO",
                "TARDARON MAS DE 10 SEGUNDOS. ESCANEA DE NUEVO EL QR DE CASETA");
        }

        if (authorization.Estado != "PENDIENTE")
        {
            return new CasetaQrAuthorizationValidation(
                false,
                "SECUENCIA_INVALIDA",
                "ESCANEA DE NUEVO EL QR DE CASETA ANTES DEL BULTO");
        }

        return new CasetaQrAuthorizationValidation(true, "OK", "", authorization.Id);
    }

    private async Task ConsumeCasetaQrAuthorizationAsync(
        IDbConnection conn,
        IDbTransaction transaction,
        long authorizationId,
        string estado,
        string motivo,
        string textoBulto)
    {
        await conn.ExecuteAsync(
            $@"UPDATE {TablaAutorizacionCasetaQr}
               SET estado = @estado,
                   consumido_at = NOW(),
                   texto_bulto = @textoBulto,
                   motivo = @motivo
               WHERE id = @authorizationId",
            new { authorizationId, estado, motivo, textoBulto },
            transaction);
    }

    public async Task<ScanLabelResult> ScanLabelAsync(ScanLabelRequest request)
    {
        var result = new ScanLabelResult { Relno = request.Relno };
        using IDbConnection conn = _db.CreateConnection();
        await EnsureAutorizacionCasetaQrAsync(conn);
        conn.Open();

        var (tabla, sufijo) = ResolverTabla(request.Relno);
        string estatusCargaActual = "";
        EtiquetaDbRow? etiqueta = null;

        using var transaction = conn.BeginTransaction();
        try
        {
            estatusCargaActual = await conn.ExecuteScalarAsync<string>(
                $"SELECT estatus_carga FROM {tabla} WHERE RELNO=@relno",
                new { relno = request.Relno },
                transaction) ?? "";

            if (estatusCargaActual == "BLOQUEO" || estatusCargaActual == "BLOQUEO REIMPRESION")
            {
                result.Message = "RELACION BLOQUEADA";
                result.Tipo = "BLOQUEADO";
                result.EstatusCarga = estatusCargaActual;
                result.BloqueoMotivo = estatusCargaActual == "BLOQUEO REIMPRESION"
                    ? "La relacion esta bloqueada por reimpresion."
                    : "La relacion esta bloqueada por escaneo rapido.";
                transaction.Commit();
                return result;
            }

            var authorization = await ValidateCasetaQrAuthorizationAsync(
                conn,
                transaction,
                request.Relno,
                request.AutorizacionToken,
                request.Usuario);

            if (!authorization.Valida)
            {
                result.Message = authorization.Mensaje;
                result.Tipo = authorization.Tipo;
                transaction.Commit();
                return result;
            }

            var buscaSql = @"SELECT e.id_etiqueta_mcia AS IdEtiquetaMcia,
    e.textoetiqueta AS Textoetiqueta,
    e.creferencia AS Creferencia,
    e.nlineno AS Nlineno,
    e.total AS Total,
    IF(YEAR(e.time_scan) > 0, e.time_scan, NULL) AS TimeScan,
    e.estatus AS Estatus,
    e.usucarga AS Usucarga,
    e.relacion AS Relacion,
    e.historial AS Historial,
    c.cidmanejo
   FROM etiqueta_mcia e
   LEFT JOIN traf c ON e.creferencia = c.creferencia
   LEFT JOIN agrupa ON agrupa.cref_agrupada = c.creferencia
   LEFT JOIN traf2 ON traf2.creferencia = agrupa.creferencia
   WHERE traf2.nrelacion = @relno AND e.textoetiqueta = @texto
   LIMIT 1";

            etiqueta = await conn.QueryFirstOrDefaultAsync<EtiquetaDbRow>(
                buscaSql,
                new { relno = request.Relno, texto = request.TextoEtiqueta },
                transaction);

            if (etiqueta == null)
            {
                await RegistrarErrorEvitadoInternalAsync(
                    conn,
                    request.Relno,
                    request.TextoEtiqueta,
                    request.Usuario,
                    transaction);
                await ConsumeCasetaQrAuthorizationAsync(
                    conn,
                    transaction,
                    authorization.Id,
                    "RECHAZADO",
                    "NO_PERTENECE",
                    request.TextoEtiqueta);
                result.Message = "NO PERTENECE";
                result.Tipo = "NO_PERTENECE";
                transaction.Commit();
                return result;
            }

            if (!string.IsNullOrEmpty(etiqueta.Relacion) && etiqueta.Relacion != request.Relno)
            {
                await ConsumeCasetaQrAuthorizationAsync(
                    conn,
                    transaction,
                    authorization.Id,
                    "RECHAZADO",
                    "ETIQUETA_OTRA_RELACION",
                    request.TextoEtiqueta);
                result.Message = "ETIQUETA DE OTRA RELACION";
                result.Tipo = "ETIQUETA_OTRA_RELACION";
                transaction.Commit();
                return result;
            }

            string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string ipSuffix = string.IsNullOrWhiteSpace(request.IpAddress) ? "" : $" {request.IpAddress}";
            string historialScan = $"ESCANEO {timeStr}{ipSuffix} , ";
            await conn.ExecuteAsync(
                @"UPDATE etiqueta_mcia
                  SET relacion=@relno, estatus='CARGADO', fmodi=@time, usumodi=@usuario,
                      time_scan=@time, usucarga=@usuario,
                      historial=CONCAT(IFNULL(historial,''), @ip)
                  WHERE id_etiqueta_mcia=@idEtiqueta",
                new
                {
                    relno = request.Relno,
                    time = timeStr,
                    usuario = request.Usuario,
                    idEtiqueta = etiqueta.IdEtiquetaMcia,
                    ip = historialScan
                },
                transaction);

            await ConsumeCasetaQrAuthorizationAsync(
                conn,
                transaction,
                authorization.Id,
                "CONSUMIDO",
                "BULTO_OK",
                request.TextoEtiqueta);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        var review = await RevisaTiempoRealAsync(conn, request.Relno, request.Usuario, sufijo);
        if (review.Bloqueado)
        {
            result.Message = "RELACION BLOQUEADA";
            result.Tipo = "BLOQUEADO";
            result.EstatusCarga = "BLOQUEO";
            result.BloqueoMotivo = review.Motivo;
            result.EtiquetasBloqueadas = review.EtiquetasBloqueadas;
            result.EntradaCref = etiqueta?.Creferencia;
            return result;
        }

        result.Success = true;
        result.Message = "BULTO OK";
        result.Tipo = "OK";
        result.EstatusCarga = estatusCargaActual;
        result.EntradaCref = etiqueta?.Creferencia;
        return result;
    }

    private async Task RegistrarErrorEvitadoInternalAsync(
        IDbConnection conn,
        string relno,
        string textoEtiqueta,
        string usuario,
        IDbTransaction? transaction = null)
    {
        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        await conn.ExecuteAsync(
        @"INSERT INTO log_etiquta_no_pertenecia (RELACION, TEXTOETIQUETA, TIME_SCAN, USUARIO)
  VALUES (@relno, @texto, @time, @usuario)",
        new { relno, texto = textoEtiqueta, time = timeStr, usuario },
        transaction);
    }


    private async Task<ScanSpeedReview> RevisaTiempoRealAsync(IDbConnection conn, string relno, string usuario, string sufijo)
    {
        var tabla = sufijo == "C" ? "relacion_imex" : "relacion";

        var transbordo = await conn.QueryFirstOrDefaultAsync<int>(
     @"SELECT COUNT(*) FROM traf2 t2
  LEFT JOIN agrupa ag ON t2.creferencia = ag.creferencia
  LEFT JOIN traf t ON ag.cref_agrupada = t.creferencia
  WHERE nrelacion = @relno AND t.cidmanejo='T'
  LIMIT 1",
     new { relno });

        if (transbordo > 0) return new ScanSpeedReview(false, 0, "");

        var cliRow = await conn.QueryFirstOrDefaultAsync<string>(
     $@"SELECT GROUP_CONCAT(DISTINCT cclienteidtraf,'') AS cidcte
 FROM {tabla}
  LEFT JOIN traf2 ON {tabla}.relno = traf2.nrelacion
 LEFT JOIN {(sufijo == "C" ? "agrupa ON agrupa.creferencia = traf2.creferencia LEFT JOIN traf ON agrupa.cref_agrupada = traf.creferencia" : "traf ON traf2.creferencia = traf.creferencia")}
 LEFT JOIN clientes ON traf.cclienteidtraf = clientes.cidcte
 WHERE {tabla}.relno = @relno",
     new { relno });

        var clientesEspeciales = new[] { "2944", "CL7283", "CL7027", "CL7331", "CL7597", "CL7467" };
        bool esClienteEspecial = clientesEspeciales.Any(c => cliRow?.Contains(c) == true);

        var etiquetas = (await conn.QueryAsync<EtiquetaDbRow>(
@"SELECT e.textoetiqueta AS Textoetiqueta,
  IF(YEAR(e.time_scan) > 0, e.time_scan, NULL) AS TimeScan,
  e.historial AS Historial,
  t.cidmanejo AS Cidmanejo
  FROM etiqueta_mcia e
  LEFT JOIN traf t ON e.creferencia = t.creferencia
  WHERE e.relacion = @relno AND e.estatus = 'CARGADO'
  ORDER BY e.time_scan ASC",
new { relno })).ToList();

        var enBloque = new List<EtiquetaDbRow>();
        DateTime? inicioBloque = null; int count = 0;
        bool bien = true;

        foreach (var et in etiquetas)
        {
            if (et.Cidmanejo == "T" && !string.IsNullOrEmpty(et.Historial)) continue;
            if (et.Cidmanejo == "PAQ/M") continue;
            if (!et.TimeScan.HasValue || et.TimeScan.Value.Year <= 2010) continue;

            count++;
            if (inicioBloque == null)
            {
                inicioBloque = et.TimeScan.Value; count = 1; enBloque.Clear();
            }
            else
            {
                double gap = (et.TimeScan.Value - inicioBloque.Value).TotalSeconds;
                if (gap > 20) { inicioBloque = null; count = 1; enBloque.Clear(); }
                else { inicioBloque = et.TimeScan.Value; }
            }
            enBloque.Add(et);

            bool umbral = (count > 3 && esClienteEspecial)
            || (count > 1 && !esClienteEspecial && inicioBloque != null);
            if (umbral && enBloque.Any(e => RequiereBloqueoPorHistorial(e.Historial)))
            {
                bien = false;
                break;
            }
        }

        if (bien) return new ScanSpeedReview(false, 0, "");

        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string hist = $"BLOQUEO {usuario} {timeStr} , ";
        foreach (var scan in enBloque)
        {
            await conn.ExecuteAsync(
           @"UPDATE etiqueta_mcia
    SET relacion=@relno, fmodi=@time, usumodi=@usuario,
   historial=CONCAT(IFNULL(historial,''), @hist)
    WHERE textoetiqueta=@texto",
           new { relno, time = timeStr, usuario, hist, texto = scan.Textoetiqueta });
        }

        await conn.ExecuteAsync(
        $"UPDATE {tabla} SET estatus_carga='BLOQUEO' WHERE RELNO=@relno",
        new { relno });

        _logger.LogWarning("BLOQUEO detected for relation {Relno} by user {Usuario}", relno, usuario);
        await NotificarBloqueoSalidaAsync(conn, relno, usuario, sufijo, enBloque.Count,
        "Escaneo rapido detectado. La relacion requiere revision de supervisor.");
        return new ScanSpeedReview(true, enBloque.Count, "Escaneo rapido detectado. La relacion requiere revision de supervisor.");

    }

    private async Task NotificarBloqueoSalidaAsync(
   IDbConnection conn,
   string relno,
   string usuario,
   string sufijo,
   int etiquetasBloqueadas,
   string motivo)
    {
        try
        {
            string tabla = sufijo == "C" ? "relacion_imex" : "relacion";
            var relacion = await conn.QueryFirstOrDefaultAsync<SalidaDbRelation>(
            $"SELECT {RelacionSelectFields} FROM {tabla} WHERE relno=@relno",
            new { relno });
            var (cliente, _) = await GetClienteRelacionAsync(conn, relno);

            var notification = new SalidaBloqueoNotification
            {
                EventoId = $"{relno}-{DateTime.Now:yyyyMMddHHmmssfff}",
                Relno = relno,
                Caja = relacion?.Caja ?? "",
                Usuario = usuario,
                Cliente = cliente,
                Motivo = motivo,
                EtiquetasBloqueadas = etiquetasBloqueadas,
                Fecha = DateTime.Now,
                Url = $"/bodega/relacion?relno={Uri.EscapeDataString(relno)}"
            };

            await _hub.Clients.Group(WarehouseHub.SupervisoresBloqueoGroup)
           .SendAsync("onSalidaBloqueada", notification);
            await _notifications.SendSalidaBloqueoAsync(notification, usuario);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo notificar bloqueo de salida {Relno}.", relno);
        }
    }

    private async Task EnviarCorreoBloqueoAsync(IDbConnection conn, string relno, string usuario, string sufijo)
    {
        var recipients = ObtenerCorreosNotificacion();
        var caja = await conn.QueryFirstOrDefaultAsync<string>(
        $"SELECT caja FROM {(sufijo == "C" ? "relacion_imex" : "relacion")} WHERE relno=@relno", new { relno });

        string body = $"Relación: {relno}<br>Usuario: {usuario}<br>Caja: {caja}";
        await _smtp.EnviarAsync(recipients, $"BLOQUEO - {relno}", body);
    }

    private static bool RequiereBloqueoPorHistorial(string? historial)
    {
        if (string.IsNullOrWhiteSpace(historial)) return true;
        if (!historial.Contains("DESBLOQUEO")) return true;

        var partes = historial.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string ultimo = partes.LastOrDefault() ?? historial;
        return ultimo.Contains("ESCANEO");
    }

    private List<string> ObtenerCorreosNotificacion()
    {
        var recipients = _config.GetSection("EmailNotifications:SalidaBloqueo")
        .Get<List<string>>() ?? new List<string>();
        if (recipients.Count == 0)
        {
            recipients = new List<string>
   {
  "sergio@imexfwd.com", "javier@imexfwd.com",
  "manuel.ortegon@imexfwd.com", "amontoya@imexfwd.com",
  "atencion.sistemas@imexfwd.com", "humberto.sanchez@imexfwd.com",
  "maru@imexfwd.com", "desarrollo@imexfwd.com"
   };
        }
        return recipients;
    }


    public async Task<ResultadoOperacion> FinalizarAsync(string relno, string usuario)
    {
        var result = new ResultadoOperacion();
        var (tabla, sufijo) = ResolverTabla(relno);

        using IDbConnection conn = _db.CreateConnection();

        var etiquetas = await GetEtiqEntradasAsync(conn, relno, sufijo);
        int scannedCount = etiquetas.Count(e => e.Escaneado);
        int totalCount = etiquetas.Count;

        if (scannedCount < totalCount)
        {
            result.Message = "FALTAN BULTOS";
            return result;
        }

        var dbRel = await conn.QueryFirstOrDefaultAsync<SalidaDbRelation>(
     $"SELECT {RelacionSelectFields} FROM {tabla} WHERE relno=@relno", new { relno });

        if (dbRel == null)
        {
            result.Message = "RELACION NO ENCONTRADA";
            return result;
        }

        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        var (totBultos, manejo) = await GetTotBultosAsync(conn, relno, sufijo);
        string estatusMercancia = ComputeEstatusMercancia(dbRel.Estatus, dbRel.EstatusCarga, etiquetas, totBultos, manejo);

        if (estatusMercancia != "COMPLETA")
        {
            result.Message = "REQUIERE AUTORIZACION";
            result.Success = false;
            return result;
        }

        foreach (var et in etiquetas.Where(e => e.Escaneado && !string.IsNullOrWhiteSpace(e.TextoEtiqueta)))
        {
            await conn.ExecuteAsync(
           @"UPDATE etiqueta_mcia
    SET relacion=@relno, estatus='CARGADO', fmodi=@time, usumodi=@usuario,
   time_scan=@time, usucarga=@usuario
    WHERE textoetiqueta=@texto",
           new { relno, time = timeStr, usuario, texto = et.TextoEtiqueta });
        }

        string estatusFinal = await RevisaTiempoAsync(conn, relno, sufijo, usuario);

        await conn.ExecuteAsync(
     $@"UPDATE {tabla}
 SET ESTATUS_CARGA=@estatus, usucargamcia=@usuario,
     inicio_carga=@time, fin_carga=@time
 WHERE RELNO=@relno",
     new { estatus = estatusFinal, usuario, time = timeStr, relno });

        result.Success = true;
        result.Message = estatusFinal == "CARGADA" ? "FINALIZADO" : "DEMASIADO RAPIDO";
        return result;
    }


    private async Task<string> RevisaTiempoAsync(IDbConnection conn, string relno, string sufijo, string usuario)
    {
        var tabla = sufijo == "C" ? "relacion_imex" : "relacion";

        var scannedLabels = (await conn.QueryAsync<EtiquetaDbRow>(
     @"SELECT e.textoetiqueta, IF(YEAR(e.time_scan) > 0, e.time_scan, NULL) AS TimeScan, t.cidmanejo
  FROM etiqueta_mcia e
  LEFT JOIN traf t ON e.creferencia = t.creferencia
  WHERE e.relacion = @relno AND e.estatus = 'CARGADO'
  AND e.time_scan IS NOT NULL AND YEAR(e.time_scan) > 2000
  ORDER BY e.time_scan ASC",
     new { relno })).ToList();

        if (scannedLabels.Count <= 1) return "CARGADA";

        var validLabels = scannedLabels.Where(s => s.Cidmanejo != "PAQ/M").ToList();
        if (validLabels.Count <= 1) return "CARGADA";

        var cliRow = await conn.QueryFirstOrDefaultAsync<string>(
     $@"SELECT GROUP_CONCAT(DISTINCT cclienteidtraf,'') AS cidcte
 FROM {tabla}
 LEFT JOIN traf2 ON {tabla}.relno = traf2.nrelacion
 LEFT JOIN {(sufijo == "C" ? "agrupa ON agrupa.creferencia = traf2.creferencia LEFT JOIN traf ON agrupa.cref_agrupada = traf.creferencia" : "traf ON traf2.creferencia = traf.creferencia")}
 LEFT JOIN clientes ON traf.cclienteidtraf = clientes.cidcte
 WHERE {tabla}.relno = @relno",
     new { relno });

        var clientesEspeciales = new[] { "2944", "CL7283", "CL7027", "CL7331", "CL7597", "CL7467" };
        if (clientesEspeciales.Any(c => cliRow?.Contains(c) == true))
            return "CARGADA";

        var firstScan = validLabels.First().TimeScan!.Value;
        var lastScan = validLabels.Last().TimeScan!.Value;
        double totalSeconds = (lastScan - firstScan).TotalSeconds;
        double avgSeconds = totalSeconds / validLabels.Count;

        if (avgSeconds >= 45) return "CARGADA";

        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        await conn.ExecuteAsync(
        $@"UPDATE {tabla}
 SET ESTATUS_CARGA='DEMASIADO RAPIDO', usucargamcia=@usuario,
     inicio_carga=@time, fin_carga=@time
 WHERE RELNO=@relno",
        new { usuario, time = timeStr, relno });

        return "DEMASIADO RAPIDO";
    }

    private async Task EnviarCorreoCargaRapidaAsync(IDbConnection conn, string relno, string usuario, string sufijo,
   int cantidad, double tiempoSegundos)
    {
        var recipients = ObtenerCorreosNotificacion();
        var caja = await conn.QueryFirstOrDefaultAsync<string>(
        $"SELECT caja FROM {(sufijo == "C" ? "relacion_imex" : "relacion")} WHERE relno=@relno", new { relno });

        string body = $"Relación: {relno}<br>Usuario: {usuario}<br>Caja: {caja}<br>" +
        $"Cantidad: {cantidad} bultos<br>Tiempo: {tiempoSegundos:F1} segundos ({tiempoSegundos / cantidad:F1}s promedio)";
        await _smtp.EnviarAsync(recipients, $"CARGA RÁPIDA - {relno}", body);
    }


    public async Task<ResultadoOperacion> PausarAsync(string relno, string usuario)
    {
        var result = new ResultadoOperacion();
        var (tabla, sufijo) = ResolverTabla(relno);
        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using IDbConnection conn = _db.CreateConnection();

        var etiquetas = await GetEtiqEntradasAsync(conn, relno, sufijo);
        foreach (var et in etiquetas.Where(e => e.Escaneado && !string.IsNullOrWhiteSpace(e.TextoEtiqueta)))
        {
            await conn.ExecuteAsync(
           @"UPDATE etiqueta_mcia
    SET relacion=@relno, estatus='CARGADO', fmodi=@time, usumodi=@usuario,
   time_scan=@time, usucarga=@usuario
    WHERE textoetiqueta=@texto",
           new { relno, time = timeStr, usuario, texto = et.TextoEtiqueta });
        }

        await conn.ExecuteAsync(
        $@"UPDATE {tabla}
 SET ESTATUS_CARGA='EN ESPERA', usucargamcia=@usuario, inicio_carga=@time
 WHERE RELNO=@relno",
        new { usuario, time = timeStr, relno });

        result.Success = true;
        result.Message = "PAUSADO";
        return result;
    }


    public async Task<ResultadoOperacion> CancelarAsync(string relno, string usuario)
    {
        var result = new ResultadoOperacion();
        var (tabla, _) = ResolverTabla(relno);

        using IDbConnection conn = _db.CreateConnection();

        var actual = await conn.QueryFirstOrDefaultAsync<SalidaDbRelation>(
  $"SELECT {RelacionSelectFields} FROM {tabla} WHERE RELNO=@relno", new { relno });

        if (actual != null && string.IsNullOrWhiteSpace(actual.Usuedit))
        {
            string nuevoEstatus = actual.EstatusCarga == "CARGANDO" ? "" : actual.EstatusCarga;
            await conn.ExecuteAsync(
           $@"UPDATE {tabla}
     SET ESTATUS_CARGA=@estatus, usucargamcia=''
     WHERE RELNO=@relno",
           new { estatus = nuevoEstatus, relno });
        }

        result.Success = true;
        result.Message = "CANCELADO";
        return result;
    }


    public async Task<ResultadoOperacion> SolicitarAutorizacionAsync(string relno, string usuario)
    {
        var result = new ResultadoOperacion();
        var (tabla, sufijo) = ResolverTabla(relno);
        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using IDbConnection conn = _db.CreateConnection();

        var etiquetas = await GetEtiqEntradasAsync(conn, relno, sufijo);

        var dbRel = await conn.QueryFirstOrDefaultAsync<SalidaDbRelation>(
  $"SELECT {RelacionSelectFields} FROM {tabla} WHERE relno=@relno", new { relno });
        var (totBultos, manejo) = await GetTotBultosAsync(conn, relno, sufijo);
        string estatusFinal = ComputeEstatusMercancia(dbRel?.Estatus ?? "", dbRel?.EstatusCarga ?? "", etiquetas, totBultos, manejo);
        if (estatusFinal == "COMPLETA") estatusFinal = "INCOMPLETA";

        foreach (var et in etiquetas.Where(e => !string.IsNullOrWhiteSpace(e.TextoEtiqueta)))
        {
            string etEstatus = et.Escaneado ? "CARGADO" : "FALTA";
            string etTimeScan = et.Escaneado ? timeStr : "0000-00-00 00:00:00";
            await conn.ExecuteAsync(
           @"UPDATE etiqueta_mcia
    SET relacion=@relno, estatus=@estatus, fmodi=@time, usumodi=@usuario,
   time_scan=@timescan, usucarga=@usuario
    WHERE textoetiqueta=@texto",
           new
           {
               relno,
               estatus = etEstatus,
               time = timeStr,
               usuario,
               timescan = etTimeScan,
               texto = et.TextoEtiqueta
           });
        }

        await conn.ExecuteAsync(
        $@"UPDATE {tabla}
 SET ESTATUS_CARGA=@estatus, usucargamcia=@usuario,
     inicio_carga=@time, fin_carga=@time
 WHERE RELNO=@relno",
        new { estatus = estatusFinal, usuario, time = timeStr, relno });

        result.Success = true;
        result.Message = "ESPERANDO SALIDA";
        return result;
    }


    public async Task<ResultadoOperacion> DescargarLabelAsync(DescargarRequest request)
    {
        var result = new ResultadoOperacion();
        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using IDbConnection conn = _db.CreateConnection();

        var etiqueta = await conn.QueryFirstOrDefaultAsync<EtiquetaDbRow>(
        @"SELECT id_etiqueta_mcia AS IdEtiquetaMcia,
  textoetiqueta AS Textoetiqueta,
  creferencia AS Creferencia,
  nlineno AS Nlineno,
  total AS Total,
  NULLIF(CAST(time_scan AS CHAR), '0000-00-00 00:00:00') AS TimeScan,
  estatus AS Estatus,
  usucarga AS Usucarga,
  relacion AS Relacion,
  historial AS Historial
  FROM etiqueta_mcia WHERE textoetiqueta=@texto AND relacion=@relno",
        new { texto = request.TextoEtiqueta, relno = request.Relno });

        if (etiqueta == null)
        {
            result.Message = "NO SE DEBE DESCARGAR";
            return result;
        }

        if (etiqueta.Estatus == "ELIMINAR")
        {
            await conn.ExecuteAsync(
           "DELETE FROM etiqueta_mcia WHERE textoetiqueta=@texto",
           new { texto = request.TextoEtiqueta });
        }
        else
        {
            await conn.ExecuteAsync(
           @"UPDATE etiqueta_mcia
    SET relacion='', estatus='', fmodi=@time, usumodi=@usuario,
   usucarga='', time_scan='0000-00-00 00:00:00'
    WHERE textoetiqueta=@texto",
           new { time = timeStr, usuario = request.Usuario, texto = request.TextoEtiqueta });
        }

        result.Success = true;
        result.Message = "DESCARGADO";
        return result;
    }

    public async Task<ResultadoOperacion> FinalizarDescargaAsync(string relno, string usuario)
    {
        var result = new ResultadoOperacion();
        var (tabla, _) = ResolverTabla(relno);
        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using IDbConnection conn = _db.CreateConnection();

        await conn.ExecuteAsync(
        $@"UPDATE {tabla}
 SET ESTATUS_CARGA='', usucargamcia=''
 WHERE RELNO=@relno",
        new { relno });

        result.Success = true;
        result.Message = "DESCARGA COMPLETA";
        return result;
    }


    public async Task<List<EtiquetaInfo>> GetEtiquetasDescargaAsync(string relno)
    {
        using IDbConnection conn = _db.CreateConnection();

        var rows = (await conn.QueryAsync<EtiquetaDbRow>(
        @"SELECT e.id_etiqueta_mcia AS IdEtiquetaMcia,
  e.textoetiqueta AS Textoetiqueta,
  e.creferencia AS Creferencia,
  e.nlineno AS Nlineno,
  e.total AS Total,
  IF(YEAR(e.time_scan) > 0, e.time_scan, NULL) AS TimeScan,
  e.estatus AS Estatus,
  e.usucarga AS Usucarga,
  e.relacion AS Relacion,
  e.historial AS Historial
  FROM etiqueta_mcia e WHERE e.relacion=@relno AND textoetiqueta<>''",
        new { relno })).ToList();

        return rows.Select(r => new EtiquetaInfo
        {
            TextoEtiqueta = r.Textoetiqueta,
            TextoPagina = ConvShort(r.Textoetiqueta),
            Creferencia = r.Creferencia,
            Nlineno = r.Nlineno,
            Total = r.Total,
            Escaneado = true,
            Estatus = r.Estatus,
            Manejo = r.Cidmanejo,
            Historial = r.Historial
        }).ToList();
    }


    public async Task<CargarRelacionResult> CargarSinRelacionAsync(string entrada, string usuario)
    {
        var result = new CargarRelacionResult();

        if (entrada.Length < 5 || entrada.Length > 15)
        {
            result.Message = "NO ES ENTRADA";
            return result;
        }

        if (entrada.Length < 3 || entrada[2] != 'C')
        {
            result.Message = "NO ES ENTRADA";
            return result;
        }

        using IDbConnection conn = _db.CreateConnection();

        var traf = await conn.QueryFirstOrDefaultAsync(
        @"SELECT * FROM traf WHERE TIPOP='C' AND creferencia=@entrada",
        new { entrada });

        if (traf == null)
        {
            result.Message = "NO ES ENTRADA";
            return result;
        }

        string cidmanejo = traf.cidmanejo ?? "";
        if (cidmanejo != "D" && cidmanejo != "T")
        {
            result.Message = "ESTA ENTRADA NO ES DIRECTA/TRANSBORDO";
            return result;
        }

        var etiquetas = await conn.QueryAsync<EtiquetaDbRow>(
     @"SELECT e.id_etiqueta_mcia AS IdEtiquetaMcia,
  e.textoetiqueta AS Textoetiqueta,
  e.creferencia AS Creferencia,
  e.nlineno AS Nlineno,
  e.total AS Total,
  IF(YEAR(e.time_scan) > 0, e.time_scan, NULL) AS TimeScan,
  e.estatus AS Estatus,
  e.usucarga AS Usucarga,
  e.relacion AS Relacion,
  null AS Cidmanejo,
  e.historial AS Historial
  FROM traf c
  LEFT JOIN etiqueta_mcia AS e ON c.creferencia = e.creferencia
  WHERE c.creferencia = @entrada AND textoetiqueta <> ''
  GROUP BY e.id_etiqueta_mcia",
     new { entrada });

        var etiquetaList = etiquetas.Select(r => new EtiquetaInfo
        {
            TextoEtiqueta = r.Textoetiqueta,
            TextoPagina = ConvShort(r.Textoetiqueta),
            Creferencia = r.Creferencia,
            Nlineno = r.Nlineno,
            Total = r.Total,
            Escaneado = r.Relacion == entrada,
            Estatus = r.Estatus,
            Usucarga = r.Usucarga,
            TimeScan = r.TimeScan,
            Historial = r.Historial
        }).ToList();

        var totRow = await conn.QueryFirstOrDefaultAsync<TotBultosDbRow>(
     @"SELECT FORMAT(SUM(bultos),0) AS bultos, ANY_VALUE(manejo) AS manejo FROM (
  SELECT ANY_VALUE(traf.creferencia) AS creferencia, trafdet.ncantidad AS bultos, ANY_VALUE(cdescmanejo) AS manejo
  FROM traf
  LEFT JOIN manejo ON manejo.cidmanejo=traf.cidmanejo
  LEFT JOIN trafdet ON traf.creferencia=trafdet.creferencia
  WHERE traf.creferencia=@entrada GROUP BY idtrafdet
   ) AS tabla GROUP BY creferencia",
     new { entrada });

        int totBultos = 0;
        if (totRow != null && int.TryParse(totRow.Bultos?.Replace(",", ""), out int n)) totBultos = n;

        string estatusEntrada = "COMPLETA";
        if (etiquetaList.Any(e => e.Estatus == "SALIDA"))
            estatusEntrada = "INFORMACION INCORRECTA EN RELACION, NOTIFICAR A ENCARGADO";
        if (totBultos != etiquetaList.Count)
            estatusEntrada = "ENTRADA NO SE HA ETIQUETADO POR COMPLETO";
        if (etiquetaList.Count == 0)
            estatusEntrada = "NO DISP ETIQUETAS EN MCIA";

        result.Success = true;
        result.Etiquetas = etiquetaList;
        result.EstatusCarga = "";
        result.TotBultos = totBultos;
        result.Relacion = new RelacionInfo
        {
            Relno = entrada,
            Estatus = estatusEntrada,
            Manejo = cidmanejo,
            Cliente = traf.cnombre ?? ""
        };

        return result;
    }

    public async Task<ScanLabelResult> ScanSinRelacionAsync(SinRelacionRequest request)
    {
        var result = new ScanLabelResult { Relno = request.Entrada };

        using IDbConnection conn = _db.CreateConnection();
        await EnsureAutorizacionCasetaQrAsync(conn);
        conn.Open();

        using var transaction = conn.BeginTransaction();
        try
        {
            var authorization = await ValidateCasetaQrAuthorizationAsync(
                conn,
                transaction,
                request.Entrada,
                request.AutorizacionToken,
                request.Usuario);

            if (!authorization.Valida)
            {
                result.Message = authorization.Mensaje;
                result.Tipo = authorization.Tipo;
                transaction.Commit();
                return result;
            }

            var etiqueta = await conn.QueryFirstOrDefaultAsync<EtiquetaDbRow>(
        @"SELECT e.id_etiqueta_mcia AS IdEtiquetaMcia,
  e.textoetiqueta AS Textoetiqueta,
  e.creferencia AS Creferencia,
  e.nlineno AS Nlineno,
  e.total AS Total,
  IF(YEAR(e.time_scan) > 0, e.time_scan, NULL) AS TimeScan,
  e.estatus AS Estatus,
  e.usucarga AS Usucarga,
  e.relacion AS Relacion,
  e.historial AS Historial
  FROM etiqueta_mcia e
  LEFT JOIN traf c ON e.creferencia = c.creferencia
  WHERE c.creferencia = @entrada AND e.textoetiqueta = @texto
  LIMIT 1",
            new { entrada = request.Entrada, texto = request.TextoEtiqueta },
            transaction);

            if (etiqueta == null)
            {
                await RegistrarErrorEvitadoInternalAsync(
                    conn,
                    request.Entrada.Length >= 13 ? request.Entrada[..13] : request.Entrada,
                    request.TextoEtiqueta,
                    request.Usuario,
                    transaction);
                await ConsumeCasetaQrAuthorizationAsync(
                    conn,
                    transaction,
                    authorization.Id,
                    "RECHAZADO",
                    "NO_PERTENECE",
                    request.TextoEtiqueta);
                result.Message = "NO PERTENECE";
                result.Tipo = "NO_PERTENECE";
                transaction.Commit();
                return result;
            }

            string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            await conn.ExecuteAsync(
        @"UPDATE etiqueta_mcia
  SET relacion=@relno, estatus='CARGADO', fmodi=@time, usumodi=@usuario,
    time_scan=@time, usucarga=@usuario,
    historial=CONCAT(IFNULL(historial,''), 'ESCANEO')
  WHERE id_etiqueta_mcia=@idEtiqueta",
            new
            {
                relno = request.Entrada,
                time = timeStr,
                usuario = request.Usuario,
                idEtiqueta = etiqueta.IdEtiquetaMcia
            },
            transaction);

            await ConsumeCasetaQrAuthorizationAsync(
                conn,
                transaction,
                authorization.Id,
                "CONSUMIDO",
                "BULTO_OK",
                request.TextoEtiqueta);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        result.Success = true;
        result.Message = "BULTO OK";
        result.Tipo = "OK";
        return result;
    }


    public async Task<ResultadoOperacion> RegistrarErrorEvitadoAsync(ErrorEvitadoRequest request)
    {
        var result = new ResultadoOperacion();

        using IDbConnection conn = _db.CreateConnection();
        await RegistrarErrorEvitadoInternalAsync(conn, request.Relno, request.TextoEtiqueta, request.Usuario);

        result.Success = true;
        result.Message = "ERROR REGISTRADO";
        return result;
    }


    public async Task<ResultadoOperacion> AutorizarAsync(string relno, string usuario)
    {
        var result = new ResultadoOperacion();
        var (tabla, sufijo) = ResolverTabla(relno);
        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using IDbConnection conn = _db.CreateConnection();

        var etiquetas = await GetEtiqEntradasAsync(conn, relno, sufijo);
        foreach (var etiqueta in etiquetas.Where(e => !string.IsNullOrWhiteSpace(e.TextoEtiqueta)))
        {
            await conn.ExecuteAsync(
           @"UPDATE etiqueta_mcia
    SET relacion=@relno, estatus='SALIDA', fmodi=@time, usumodi=@usuario
    WHERE textoetiqueta=@texto",
           new { relno, time = timeStr, usuario, texto = etiqueta.TextoEtiqueta });
        }

        await conn.ExecuteAsync(
     $@"UPDATE {tabla}
 SET ESTATUS_CARGA='AUTORIZADA', autoriza_carga=@time, usuautoriza=@usuario,
     inicio_carga=@time, fin_carga=@time
 WHERE RELNO=@relno",
     new { relno, time = timeStr, usuario });

        result.Success = true;
        result.Message = "AUTORIZADO";
        return result;
    }


    public async Task<ResultadoOperacion> EliminarCargadoAsync(string relno, string usuario)
    {
        var result = new ResultadoOperacion();
        var (tabla, _) = ResolverTabla(relno);
        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using IDbConnection conn = _db.CreateConnection();

        await conn.ExecuteAsync(
     @"UPDATE etiqueta_mcia
  SET relacion='', estatus='', fmodi=@time, usumodi=@usuario,
    usucarga='', time_scan='0000-00-00 00:00:00'
  WHERE relacion=@relno",
     new { relno, time = timeStr, usuario });

        await conn.ExecuteAsync(
     $@"UPDATE {tabla}
 SET usucargamcia='', estatus_carga='', usuautoriza='', fmodi=@time, usumodi=@usuario,
     inicio_carga='0000-00-00 00:00:00', fin_carga='0000-00-00 00:00:00',
     autoriza_carga='0000-00-00 00:00:00'
 WHERE relno=@relno",
     new { relno, time = timeStr, usuario });

        result.Success = true;
        result.Message = "CARGADO ELIMINADO";
        return result;
    }


    public async Task<ResultadoOperacion> BloquearAsync(string relno, string usuario)
    {
        return await RelBloqDesbAsync(relno, "BLOQUEO", usuario);
    }

    public async Task<ResultadoOperacion> DesbloquearAsync(string relno, string usuario)
    {
        return await RelBloqDesbAsync(relno, "DESBLOQUEO", usuario);
    }

    private async Task<ResultadoOperacion> RelBloqDesbAsync(string relno, string accion, string usuario)
    {
        var result = new ResultadoOperacion();
        var (tabla, _) = ResolverTabla(relno);
        string accionRelacion = accion;
        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using IDbConnection conn = _db.CreateConnection();
        string usuarioCarga = await conn.ExecuteScalarAsync<string>(
        $"SELECT usucargamcia FROM {tabla} WHERE RELNO=@relno",
        new { relno }) ?? "";

        if (accion == "DESBLOQUEO")
        {
            var etiquetas = (await conn.QueryAsync<EtiquetaDbRow>(
           @"SELECT textoetiqueta AS Textoetiqueta, historial AS Historial
    FROM etiqueta_mcia
    WHERE relacion=@relno AND historial LIKE '%BLOQUEO%'",
           new { relno })).ToList();

            string hist = $"DESBLOQUEO {usuario} {timeStr} , ";
            foreach (var etiqueta in etiquetas.Where(e => EsHistorialBloqueado(e.Historial)))
            {
                await conn.ExecuteAsync(
               @"UPDATE etiqueta_mcia
   SET relacion=@relno, fmodi=@time, usumodi=@usuario, time_scan=@time,
   historial=CONCAT(IFNULL(historial,''), @hist)
   WHERE textoetiqueta=@texto",
               new { relno, time = timeStr, usuario, hist, texto = etiqueta.Textoetiqueta });
            }

            accionRelacion = "EN ESPERA";
        }

        await conn.ExecuteAsync(
        $"UPDATE {tabla} SET estatus_carga=@accion WHERE RELNO=@relno",
        new { accion = accionRelacion, relno });

        if (accion == "DESBLOQUEO")
        {
            var notification = new SalidaDesbloqueoNotification
            {
                Relno = relno,
                Supervisor = usuario,
                UsuarioCarga = usuarioCarga,
                Fecha = DateTime.Now,
                Url = $"/bodega/relacion?relno={Uri.EscapeDataString(relno)}"
            };

            await _hub.Clients.Group(WarehouseHub.SupervisoresBloqueoGroup)
           .SendAsync("onSalidaDesbloqueada", notification);

            if (!string.IsNullOrWhiteSpace(usuarioCarga))
            {
                await _hub.Clients.Group("USER_" + usuarioCarga)
               .SendAsync("onSalidaDesbloqueada", notification);
            }
        }

        result.Success = true;
        result.Message = accion == "BLOQUEO" ? "BLOQUEADO" : "DESBLOQUEADO";
        return result;
    }

    private static bool EsHistorialBloqueado(string? historial)
    {
        if (string.IsNullOrWhiteSpace(historial)) return false;

        var partes = historial.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string ultimo = partes.LastOrDefault() ?? historial;
        return (historial.Contains("BLOQUEO") && !historial.Contains("DESBLOQUEO")) ||
      (ultimo.Contains("BLOQUEO") && !ultimo.Contains("DESBLOQUEO"));
    }


    public async Task<ResultadoOperacion> RetornarAEsperaAsync(string relno, string usuario)
    {
        var result = new ResultadoOperacion();
        var (tabla, sufijo) = ResolverTabla(relno);
        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using IDbConnection conn = _db.CreateConnection();

        var etiquetas = await GetEtiqEntradasAsync(conn, relno, sufijo);
        foreach (var etiqueta in etiquetas.Where(e => !string.IsNullOrWhiteSpace(e.TextoEtiqueta)))
        {
            string estatus = etiqueta.Estatus;
            if (estatus == "SALIDA" || estatus == "ERROR")
                estatus = "CARGADO";

            await conn.ExecuteAsync(
           @"UPDATE etiqueta_mcia
    SET relacion=@relno, fmodi=@time, usumodi=@usuario, estatus=@estatus
    WHERE textoetiqueta=@texto",
           new { relno, time = timeStr, usuario, estatus, texto = etiqueta.TextoEtiqueta });
        }

        await conn.ExecuteAsync(
        $"UPDATE {tabla} SET ESTATUS_CARGA='EN ESPERA' WHERE RELNO=@relno",
        new { relno });

        result.Success = true;
        result.Message = "RETORNADO A ESPERA";
        return result;
    }


    public async Task<RelacionInfo?> GetStatusAsync(string relno)
    {
        var (tabla, _) = ResolverTabla(relno);

        using IDbConnection conn = _db.CreateConnection();
        var dbRel = await conn.QueryFirstOrDefaultAsync<SalidaDbRelation>(
        $"SELECT {RelacionSelectFields} FROM {tabla} WHERE relno=@relno", new { relno });

        if (dbRel == null) return null;

        return new RelacionInfo
        {
            Relno = dbRel.Relno,
            Estatus = dbRel.Estatus,
            EstatusCarga = dbRel.EstatusCarga,
            Manejo = dbRel.Manejo,
            Cliente = dbRel.Cliente,
            IdCliente = dbRel.Idcliente,
            CidTransfer = dbRel.Cidtransfer,
            OpTransfer = dbRel.OpTransfer,
            Caja = dbRel.Caja,
            Sellos = dbRel.Sellos,
            NumTractor = dbRel.NumTractor,
            GafeteOp = dbRel.GafeteOp,
            Planta = dbRel.Planta,
            VersionHoja = dbRel.VersionHoja
        };
    }

    public async Task<List<PendienteAutorizarItem>> GetPendientesAutorizarAsync(string busqueda = "")
    {
        const string statusIn = "('CARGADA','INCOMPLETA','DIFERENCIA EN BULTOS-ETIQUETAS','NO DISP ETIQUETAS EN MCIA','DEMASIADO RAPIDO','EN ESPERA','BLOQUEO','BLOQUEO REIMPRESION')";

        string sql = $@"
   SELECT
  u.relno   AS Relno,
  u.caja    AS Caja,
  u.usucargamcia AS Usucargamcia,
  u.estatus_carga AS EstatusCarga,
  u.planta  AS Planta,
  u.version_hoja AS VersionHoja,
  '' AS Cliente,
  0  AS TotBultos,
  (SELECT COUNT(*) FROM etiqueta_mcia em
   WHERE em.relacion = u.relno AND em.estatus = 'CARGADO') AS EtiquetasCount
   FROM (
  SELECT relno, caja, usucargamcia, estatus_carga, planta, version_hoja
  FROM relacion
  WHERE TIPOP = 'I'
    AND (estatus_carga IN {statusIn}
    OR (estatus_carga = '' AND usucargamcia = 'NO REQUERIDO'))
  UNION ALL
  SELECT relno, caja, usucargamcia, estatus_carga, planta, version_hoja
  FROM relacion_imex
  WHERE TIPOP = 'C'
    AND (estatus_carga IN {statusIn}
    OR (estatus_carga = '' AND usucargamcia = 'NO REQUERIDO'))
   ) u
   ORDER BY u.relno";

        using IDbConnection conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<PendienteAutorizarItem>(sql)).ToList();

        foreach (var row in rows)
        {
            try
            {
                row.Cliente = await conn.ExecuteScalarAsync<string>(@"
 SELECT MAX(c.cnombre)
 FROM traf2 t2
 JOIN fact f ON f.cref_agrupada = t2.CREF_ORIGEN
 JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0
 WHERE t2.nrelacion = @relno LIMIT 1", new { row.Relno }) ?? "";
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(busqueda))
        {
            var b = busqueda.ToUpperInvariant();
            rows = rows.Where(r =>
           r.Relno.ToUpperInvariant().Contains(b) ||
           r.Caja.ToUpperInvariant().Contains(b) ||
           r.Cliente.ToUpperInvariant().Contains(b)).ToList();
        }

        return rows;
    }
}
