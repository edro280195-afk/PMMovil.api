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
        // El token real ya NO se guarda en texto plano -- solo su hash (para
        // validar, ver ValidateCasetaQrAuthorizationAsync) y un "hint" de 8
        // caracteres en la columna legacy `token` (mismo criterio que
        // EntradaShareService/RevisionShareService/ReportShareService). Aquí
        // el UNIQUE KEY vive en token_hash, no en token: esta tabla inserta
        // una fila por cada escaneo de QR de caseta (mucho más volumen que
        // los enlaces compartidos), así que un UNIQUE sobre un hint de solo
        // 8 caracteres eventualmente chocaría (cumpleaños) y tronaría el
        // INSERT en producción.
        await conn.ExecuteAsync($@"
CREATE TABLE IF NOT EXISTS {TablaAutorizacionCasetaQr} (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    relno VARCHAR(32) NOT NULL,
    casetaid VARCHAR(64) NOT NULL,
    usuario VARCHAR(100) NOT NULL,
    token CHAR(32) NOT NULL,
    token_hash CHAR(64) NOT NULL,
    estado VARCHAR(20) NOT NULL DEFAULT 'PENDIENTE',
    leido_at DATETIME NOT NULL,
    expira_at DATETIME NOT NULL,
    consumido_at DATETIME NULL,
    texto_bulto VARCHAR(255) NULL,
    motivo VARCHAR(100) NULL,
    ip_address VARCHAR(45) NOT NULL DEFAULT '',
    PRIMARY KEY (id),
    UNIQUE KEY uq_salida_carga_qr_token_hash (token_hash),
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
        // El token real ya NO se guarda en texto plano -- solo su hash (para
        // validar) y un "hint" de 8 caracteres en la columna legacy `token`
        // (mismo criterio que EntradaShareService/RevisionShareService/
        // ReportShareService). El token completo solo se le regresa al
        // cliente (result.AutorizacionToken); nunca vuelve a viajar en texto
        // plano hacia la BD.
        string tokenHash = ShareTokenHasher.Hash(token);
        string tokenHint = token[..8];
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
                   (relno, casetaid, usuario, token, token_hash, estado, leido_at, expira_at, ip_address)
                   VALUES (@relno, @casetaId, @usuario, @tokenHint, @tokenHash, 'PENDIENTE',
                           NOW(), DATE_ADD(NOW(), INTERVAL {VentanaCasetaQrSegundos} SECOND), @ipAddress)",
                new { relno, casetaId, usuario, tokenHint, tokenHash, ipAddress },
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

        var etiquetas = await GetEtiqEntradasAsync(conn, relnoBase, sufijo);
        var (totBultos, manejo) = await GetTotBultosAsync(conn, relnoBase, sufijo);
        var (cliente, idCliente) = await GetClienteRelacionAsync(conn, relnoBase);
        string estatusCarga = estatusCargaOriginal;

        string estatusMercancia = ComputeEstatusMercancia(dbRel.Estatus, estatusCargaOriginal, etiquetas, totBultos, manejo);

        // Se toma la relación (candado CARGANDO) solo si el cargador puede trabajar con ella. El legacy la tomaba
        // siempre y la dejaba pegada hasta que alguien cancelaba; aquí un estado como "INFORMACION INCORRECTA…"
        // se muestra sin dejar a la relación ocupada.
        var estadosCerrados = new[] { "CARGADA", "AUTORIZADA", "SALIDA", "DEMASIADO RAPIDO", "BLOQUEO", "BLOQUEO REIMPRESION" };
        bool puedeTomarRelacion =
        (dbRel.Estatus == "COMPLETA" || dbRel.Estatus == "PRECARGA") &&
        !estadosCerrados.Contains(estatusCargaOriginal) &&
        SalidaReglas.EstadoPermiteCaptura(estatusMercancia);

        if (puedeTomarRelacion)
        {
            string ahora = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            await conn.ExecuteAsync(
            $@"UPDATE {tabla}
               SET ESTATUS_CARGA='CARGANDO', usucargamcia=@usuario, fechacruce=@fecha,
                   inicio_carga = CASE WHEN inicio_carga IS NULL OR YEAR(inicio_carga) < 2000 THEN @ahora ELSE inicio_carga END
               WHERE RELNO=@relno",
            new { usuario, fecha = DateTime.Now.ToString("yyyy-MM-dd"), ahora, relno = relnoBase });

            dbRel.Usucargamcia = usuario;
        }

        result.Success = true;
        if (!SalidaReglas.EstadoPermiteCaptura(estatusMercancia) && !SalidaReglas.EsEstadoCargaCerrada(estatusCargaOriginal)
            && !SalidaReglas.EsEstadoBloqueo(estatusCargaOriginal))
        {
            result.Message = estatusMercancia;
        }
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
        // Bultos asignados a esta relación que ya no le pertenecen (posible descargo): el legacy los lista con ERROR
        // y pide retornar la relación para que el cargador los baje (GetEtiqenRelacion, Relacion.cs:1400).
        var ajenas = await GetEtiquetasAjenasAsync(conn, relno, sufijo);
        foreach (var ajena in ajenas)
        {
            ajena.Estatus = "ERROR";
            ajena.Escaneado = true;
            etiquetas.Add(ajena);
        }
        var (totBultos, manejo) = await GetTotBultosAsync(conn, relno, sufijo);
        var (cliente, idCliente) = await GetClienteRelacionAsync(conn, relno);

        string estatus = dbRel.Estatus;
        if (!string.IsNullOrWhiteSpace(dbRel.Usuedit))
            estatus = "RELACION OCUPADA";
        if (ajenas.Count > 0)
            estatus = "ERROR, retorne relacion, posible descargo";
        else if (etiquetas.Any(e => string.Equals(e.Estatus, "ERROR", StringComparison.OrdinalIgnoreCase)))
            estatus = "INFORMACION INCORRECTA EN RELACION, NOTIFICAR A ENCARGADO, ENTRADA DE OTRA RELACION AGRUPADA";

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


    /// <summary>Etiquetas que le tocan a la relación según tráfico/agrupa (la unión es distinta para relación normal e IMEX).</summary>
    private async Task<List<EtiquetaDbRow>> QueryEtiquetasDeRelacionAsync(IDbConnection conn, string relno, string sufijo)
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
  e.historial AS Historial,
  IF(COALESCE(e.reimpresa, 0) = 1, 1, 0) AS Reimpresa
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
 e.historial AS Historial,
 IF(COALESCE(e.reimpresa, 0) = 1, 1, 0) AS Reimpresa
  FROM traf2
  LEFT JOIN agrupa ON traf2.creferencia = agrupa.creferencia
   LEFT JOIN traf c ON c.creferencia = agrupa.cref_agrupada
   LEFT JOIN etiqueta_mcia AS e ON c.creferencia = e.creferencia
   WHERE nrelacion = @relno AND textoetiqueta <> ''
   GROUP BY e.id_etiqueta_mcia";
        }

        return (await conn.QueryAsync<EtiquetaDbRow>(sql, new { relno })).ToList();
    }

    /// <summary>
    /// Etiquetas de la relación. Igual que el legacy (<c>GetEtiqEntradas</c>): la lista son las que le tocan por tráfico;
    /// está escaneada si ya tiene hora de escaneo y está asignada a esta relación; si está asignada a otra, es ERROR.
    /// Los bultos asignados a esta relación que ya no le pertenecen NO van aquí: ver <see cref="GetEtiquetasAjenasAsync"/>.
    /// </summary>
    private async Task<List<EtiquetaInfo>> GetEtiqEntradasAsync(IDbConnection conn, string relno, string sufijo)
    {
        var rows = await QueryEtiquetasDeRelacionAsync(conn, relno, sufijo);

        var result = new List<EtiquetaInfo>();
        var seenTextos = new HashSet<string>();

        foreach (var row in rows)
        {
            if (!seenTextos.Add(row.Textoetiqueta)) continue;

            bool propia = row.Relacion == relno;
            bool ajena = !propia && !string.IsNullOrEmpty(row.Relacion);
            bool escaneada = propia && row.TimeScan.HasValue;

            var info = MapEtiqueta(row, escaneada);
            info.Estatus = ajena ? "ERROR" : (propia ? row.Estatus : "");
            result.Add(info);
        }

        result.Sort((a, b) => string.Compare(a.TextoPagina, b.TextoPagina, StringComparison.Ordinal));
        return result;
    }

    /// <summary>
    /// Bultos asignados a la relación (<c>etiqueta_mcia.relacion</c>) que ya no le pertenecen según tráfico/agrupa: los que
    /// hay que bajar del camión tras un descargo (legacy <c>GetEtiqADescargar</c>, <c>Relacion.cs:1579-1617</c>).
    /// </summary>
    private async Task<List<EtiquetaInfo>> GetEtiquetasAjenasAsync(IDbConnection conn, string relno, string sufijo)
    {
        var propias = (await QueryEtiquetasDeRelacionAsync(conn, relno, sufijo))
            .Select(r => r.Textoetiqueta)
            .ToHashSet();

        var asignadas = (await conn.QueryAsync<EtiquetaDbRow>(
            @"SELECT e.id_etiqueta_mcia AS IdEtiquetaMcia,
  e.textoetiqueta AS Textoetiqueta,
  e.creferencia AS Creferencia,
  e.nlineno AS Nlineno,
  e.total AS Total,
  IF(YEAR(e.time_scan) > 0, e.time_scan, NULL) AS TimeScan,
  e.estatus AS Estatus,
  e.usucarga AS Usucarga,
  e.relacion AS Relacion,
  ANY_VALUE(t.cidmanejo) AS Cidmanejo,
  e.historial AS Historial,
  IF(COALESCE(e.reimpresa, 0) = 1, 1, 0) AS Reimpresa
  FROM etiqueta_mcia AS e
  LEFT JOIN traf AS t ON e.creferencia = t.creferencia
  WHERE e.relacion = @relno AND e.textoetiqueta <> ''
  GROUP BY e.id_etiqueta_mcia",
            new { relno })).ToList();

        var vistas = new HashSet<string>();
        return asignadas
            .Where(r => !propias.Contains(r.Textoetiqueta) && vistas.Add(r.Textoetiqueta))
            .Select(r => MapEtiqueta(r, false))
            .OrderBy(e => e.TextoPagina, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Ids de cliente de la relación separados por coma (los usan las reglas de clientes exentos).</summary>
    private static async Task<string> ObtenerIdsClienteAsync(IDbConnection conn, string relno, string sufijo)
    {
        string tabla = sufijo == "C" ? "relacion_imex" : "relacion";
        string union = sufijo == "C"
            ? "agrupa ON agrupa.creferencia = traf2.creferencia LEFT JOIN traf ON agrupa.cref_agrupada = traf.creferencia"
            : "traf ON traf2.creferencia = traf.creferencia";

        return await conn.QueryFirstOrDefaultAsync<string>(
            $@"SELECT GROUP_CONCAT(DISTINCT cclienteidtraf,'') AS cidcte
 FROM {tabla}
 LEFT JOIN traf2 ON {tabla}.relno = traf2.nrelacion
 LEFT JOIN {union}
 LEFT JOIN clientes ON traf.cclienteidtraf = clientes.cidcte
 WHERE {tabla}.relno = @relno",
            new { relno }) ?? "";
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
            Historial = row.Historial,
            Reimpresa = row.Reimpresa
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

        string tokenHash = ShareTokenHasher.Hash(token);
        var authorization = await conn.QueryFirstOrDefaultAsync<CasetaQrAuthorizationDbRow>(
            $@"SELECT id AS Id,
                      estado AS Estado,
                      IF(expira_at <= NOW(), 1, 0) AS Expirada
               FROM {TablaAutorizacionCasetaQr}
               WHERE token_hash = @tokenHash
                 AND relno = @relno
                 AND usuario = @usuario
               LIMIT 1
               FOR UPDATE",
            new { tokenHash, relno, usuario },
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

            if (SalidaReglas.EsEstadoBloqueo(estatusCargaActual))
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

            // Una relación ya cargada/autorizada/fuera no admite más escaneos (el legacy solo lo impedía en la pantalla).
            if (SalidaReglas.EsEstadoCargaCerrada(estatusCargaActual))
            {
                result.Message = "RELACION YA CARGADA";
                result.Tipo = "RELACION_CERRADA";
                result.EstatusCarga = estatusCargaActual;
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
    IF(COALESCE(e.reimpresa, 0) = 1, 1, 0) AS Reimpresa,
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

        // Etiqueta reimpresa: el bulto se registra y la relación queda bloqueada hasta que un supervisor la desbloquee
        // (legacy SalidaBodega.aspx.cs:233-279). OJO: en el legacy esta rama nunca se cumple (la consulta del escaneo no
        // lee `reimpresa`) y hay muchas reimpresas (158 en jun-ago 2026; una relación con 8 de 14), así que se enciende
        // con Salida:BloqueoPorReimpresion = true cuando el negocio lo decida. Apagado = mismo comportamiento que hoy.
        if (etiqueta != null && etiqueta.Reimpresa && _config.GetValue("Salida:BloqueoPorReimpresion", false))
        {
            await conn.ExecuteAsync(
                $"UPDATE {tabla} SET estatus_carga='BLOQUEO REIMPRESION' WHERE RELNO=@relno",
                new { relno = request.Relno });

            await NotificarBloqueoSalidaAsync(conn, request.Relno, request.Usuario, sufijo, 1,
                "Etiqueta reimpresa: la relacion queda bloqueada hasta que un supervisor la desbloquee.");

            result.Message = "RELACION BLOQUEADA";
            result.Tipo = "BLOQUEADO";
            result.EstatusCarga = "BLOQUEO REIMPRESION";
            result.BloqueoMotivo = "La relacion esta bloqueada por reimpresion.";
            result.EtiquetasBloqueadas = 1;
            result.EntradaCref = etiqueta.Creferencia;
            return result;
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
        // Las columnas de log_etiquta_no_pertenecia miden 13 / 50 / 15: en modo estricto un código largo (por ejemplo el
        // PDF417 de un proveedor) haría fallar el escaneo en vez de responder "NO PERTENECE". El legacy recortaba a 50.
        await conn.ExecuteAsync(
        @"INSERT INTO log_etiquta_no_pertenecia (RELACION, TEXTOETIQUETA, TIME_SCAN, USUARIO)
  VALUES (@relno, @texto, @time, @usuario)",
        new
        {
            relno = SalidaReglas.Recortar(relno, 13),
            texto = SalidaReglas.Recortar(textoEtiqueta, 50),
            time = timeStr,
            usuario = SalidaReglas.Recortar(usuario, 15)
        },
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

        string idsCliente = await ObtenerIdsClienteAsync(conn, relno, sufijo);

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

        var enBloque = SalidaReglas.DetectarRafaga(
            etiquetas.Select(e => new EtiquetaRafaga(e.Textoetiqueta, e.Cidmanejo, e.TimeScan, e.Historial)),
            idsCliente);

        if (enBloque == null) return new ScanSpeedReview(false, 0, "");

        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string hist = $"BLOQUEO {usuario} {timeStr} , ";
        foreach (var scan in enBloque)
        {
            await conn.ExecuteAsync(
           @"UPDATE etiqueta_mcia
    SET relacion=@relno, fmodi=@time, usumodi=@usuario,
   historial=CONCAT(IFNULL(historial,''), @hist)
    WHERE textoetiqueta=@texto",
           new { relno, time = timeStr, usuario, hist, texto = scan.Texto });
        }

        await conn.ExecuteAsync(
        $"UPDATE {tabla} SET estatus_carga='BLOQUEO' WHERE RELNO=@relno",
        new { relno });

        _logger.LogWarning("BLOQUEO detected for relation {Relno} by user {Usuario}", relno, usuario);
        const string motivo = "Escaneo rapido detectado. La relacion requiere revision de supervisor.";
        await NotificarBloqueoSalidaAsync(conn, relno, usuario, sufijo, enBloque.Count, motivo);

        // Correo CARGA_BLOQUEO del legacy (Relacion.cs:681-755): a los encargados de bodega y sistemas.
        await EnviarAlertaCargaAsync(
            conn, relno, sufijo,
            "ALERTA: CAJA: [caja] CARGA DEMASIADO RAPIDO.",
            "Relación bloqueada por escaneo rápido",
            ("Relación", relno),
            ("Usuario", usuario),
            ("Bultos en la ráfaga", enBloque.Count.ToString()),
            ("Fecha", timeStr));

        return new ScanSpeedReview(true, enBloque.Count, motivo);
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

    /// <summary>
    /// Manda un correo de alerta de carga a los encargados (misma lista que el legacy). Nunca lanza: un fallo de correo
    /// no debe impedir el escaneo ni el cierre de la carga. Va a la cola durable para no esperar al SMTP.
    /// </summary>
    private async Task EnviarAlertaCargaAsync(
        IDbConnection conn,
        string relno,
        string sufijo,
        string asunto,
        string titulo,
        params (string Campo, string Valor)[] datos)
    {
        try
        {
            var recipients = ObtenerCorreosNotificacion();
            string tabla = sufijo == "C" ? "relacion_imex" : "relacion";
            string caja = await conn.QueryFirstOrDefaultAsync<string>(
                $"SELECT caja FROM {tabla} WHERE relno=@relno", new { relno }) ?? "";

            string filas = string.Concat(datos.Concat(new (string Campo, string Valor)[] { ("Caja", caja) }).Select(d =>
                $"<tr><td style='padding:3px 14px 3px 0;color:#555'><b>{System.Net.WebUtility.HtmlEncode(d.Campo)}</b></td>" +
                $"<td>{System.Net.WebUtility.HtmlEncode(d.Valor)}</td></tr>"));
            string cuerpo =
                "<div style='font-family:Segoe UI,Arial,sans-serif;font-size:14px'>" +
                $"<h3 style='color:#b00020;margin:0 0 8px'>{System.Net.WebUtility.HtmlEncode(titulo)}</h3>" +
                $"<table>{filas}</table></div>";

            await _smtp.EnviarAsync(recipients, asunto.Replace("[caja]", caja), cuerpo, preferirCola: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo enviar la alerta de carga {Relno}.", relno);
        }
    }

    private static bool RequiereBloqueoPorHistorial(string? historial) => SalidaReglas.RequiereBloqueoPorHistorial(historial);

    /// <summary>
    /// Destinatarios de las alertas de carga. Configurable en EmailNotifications:SalidaBloqueo; por omisión los mismos del
    /// legacy (Relacion.cs:660-667).
    /// </summary>
    private List<string> ObtenerCorreosNotificacion()
    {
        var recipients = _config.GetSection("EmailNotifications:SalidaBloqueo")
        .Get<List<string>>() ?? new List<string>();
        if (recipients.Count == 0)
        {
            recipients = new List<string>
   {
  "sergio.miranda@grupopmm.com",
  "manuel.ortegon@grupopmm.com",
  "argelia.montoya@grupopmm.com",
  "atencion.sistemas@grupopmm.com",
  "humberto.sanchez@grupopmm.com",
  "desarrollo@grupopmm.com"
   };
        }
        return recipients;
    }


    public async Task<ResultadoOperacion> FinalizarAsync(string relno, string usuario)
    {
        var result = new ResultadoOperacion();
        var (tabla, sufijo) = ResolverTabla(relno);

        using IDbConnection conn = _db.CreateConnection();

        var dbRel = await conn.QueryFirstOrDefaultAsync<SalidaDbRelation>(
     $"SELECT {RelacionSelectFields} FROM {tabla} WHERE relno=@relno", new { relno });

        if (dbRel == null)
        {
            result.Message = "RELACION NO ENCONTRADA";
            return result;
        }

        if (SalidaReglas.EsEstadoBloqueo(dbRel.EstatusCarga))
        {
            result.Message = "RELACION BLOQUEADA";
            return result;
        }

        if (SalidaReglas.EsEstadoCargaCerrada(dbRel.EstatusCarga))
        {
            result.Message = "RELACION YA CARGADA";
            return result;
        }

        var etiquetas = await GetEtiqEntradasAsync(conn, relno, sufijo);
        int scannedCount = etiquetas.Count(e => e.Escaneado);
        int totalCount = etiquetas.Count;

        if (scannedCount < totalCount)
        {
            result.Message = "FALTAN BULTOS";
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

        // Revisión de tiempo con las horas REALES de escaneo (el legacy conservaba la hora de cada etiqueta).
        string idsCliente = await ObtenerIdsClienteAsync(conn, relno, sufijo);
        bool rapida = SalidaReglas.EsCargaDemasiadoRapida(
            etiquetas.Select(e => new EtiquetaTiempo(e.Manejo, e.TimeScan)).ToList(),
            idsCliente,
            out DateTime inicioEscaneo,
            out DateTime finEscaneo,
            out int cantidadRevisada);
        string estatusFinal = rapida ? "DEMASIADO RAPIDO" : "CARGADA";

        var textos = etiquetas
            .Where(e => e.Escaneado && !string.IsNullOrWhiteSpace(e.TextoEtiqueta))
            .Select(e => e.TextoEtiqueta)
            .ToList();
        if (textos.Count > 0)
        {
            // No se toca time_scan ni usucarga: son el registro real de quién y cuándo escaneó cada bulto.
            await conn.ExecuteAsync(
           @"UPDATE etiqueta_mcia
    SET relacion=@relno, estatus='CARGADO', fmodi=@time, usumodi=@usuario
    WHERE textoetiqueta IN @textos",
           new { relno, time = timeStr, usuario, textos });
        }

        // inicio_carga se conserva (se fijó al tomar la relación); fin_carga es el momento de finalizar.
        await conn.ExecuteAsync(
     $@"UPDATE {tabla}
 SET ESTATUS_CARGA=@estatus, usucargamcia=@usuario,
     inicio_carga = CASE WHEN inicio_carga IS NULL OR YEAR(inicio_carga) < 2000 THEN @time ELSE inicio_carga END,
     fin_carga=@time
 WHERE RELNO=@relno",
     new { estatus = estatusFinal, usuario, time = timeStr, relno });

        if (rapida)
        {
            // Correo CARGA_RAPIDA del legacy (Relacion.cs:604-679).
            await EnviarAlertaCargaAsync(
                conn, relno, sufijo,
                "ALERTA: CAJA: [caja] TIEMPO FUERA DE ESTANDAR.",
                "Carga fuera del tiempo estándar",
                ("Relación", relno),
                ("Usuario", usuario),
                ("Inicio", inicioEscaneo.ToString("yyyy-MM-dd HH:mm:ss")),
                ("Fin", finEscaneo.ToString("yyyy-MM-dd HH:mm:ss")),
                ("Cantidad", cantidadRevisada.ToString()),
                ("Tiempo", (finEscaneo - inicioEscaneo).ToString()));
        }

        result.Success = true;
        result.Message = estatusFinal == "CARGADA" ? "FINALIZADO" : "DEMASIADO RAPIDO";
        return result;
    }


    public async Task<ResultadoOperacion> PausarAsync(string relno, string usuario)
    {
        var result = new ResultadoOperacion();
        var (tabla, sufijo) = ResolverTabla(relno);
        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using IDbConnection conn = _db.CreateConnection();

        var etiquetas = await GetEtiqEntradasAsync(conn, relno, sufijo);
        var textos = etiquetas
            .Where(e => e.Escaneado && !string.IsNullOrWhiteSpace(e.TextoEtiqueta))
            .Select(e => e.TextoEtiqueta)
            .ToList();
        if (textos.Count > 0)
        {
            // Solo se confirma la asignación; time_scan y usucarga se respetan (así la revisión de ráfagas no se confunde al reanudar).
            await conn.ExecuteAsync(
           @"UPDATE etiqueta_mcia
    SET relacion=@relno, estatus='CARGADO', fmodi=@time, usumodi=@usuario
    WHERE textoetiqueta IN @textos",
           new { relno, time = timeStr, usuario, textos });
        }

        await conn.ExecuteAsync(
        $@"UPDATE {tabla}
 SET ESTATUS_CARGA='EN ESPERA', usucargamcia=@usuario,
     inicio_carga = CASE WHEN inicio_carga IS NULL OR YEAR(inicio_carga) < 2000 THEN @time ELSE inicio_carga END
 WHERE RELNO=@relno",
        new { usuario, time = timeStr, relno });

        result.Success = true;
        result.Message = "PAUSADO";
        return result;
    }


    /// <param name="restaurar">
    /// Estado con el que llegó la relación antes de tomarla. El legacy lo guardaba en memoria y lo restauraba al cancelar
    /// (<c>liberar</c>, Relacion.cs:847): una relación que estaba EN ESPERA vuelve a EN ESPERA y no se pierde la pausa.
    /// </param>
    public async Task<ResultadoOperacion> CancelarAsync(string relno, string usuario, string? restaurar = null)
    {
        var result = new ResultadoOperacion();
        var (tabla, _) = ResolverTabla(relno);

        using IDbConnection conn = _db.CreateConnection();

        var actual = await conn.QueryFirstOrDefaultAsync<SalidaDbRelation>(
  $"SELECT {RelacionSelectFields} FROM {tabla} WHERE RELNO=@relno", new { relno });

        if (actual != null && string.IsNullOrWhiteSpace(actual.Usuedit))
        {
            string nuevoEstatus = actual.EstatusCarga == "CARGANDO"
                ? (string.Equals(restaurar?.Trim(), "EN ESPERA", StringComparison.OrdinalIgnoreCase) ? "EN ESPERA" : "")
                : actual.EstatusCarga;
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

        // Las etiquetas asignadas a otra relación (ERROR) no se tocan: no son de esta carga.
        var propias = etiquetas.Where(e => !string.IsNullOrWhiteSpace(e.TextoEtiqueta) && e.Estatus != "ERROR").ToList();
        var escaneadas = propias.Where(e => e.Escaneado).Select(e => e.TextoEtiqueta).ToList();
        var faltantes = propias.Where(e => !e.Escaneado).Select(e => e.TextoEtiqueta).ToList();

        // Igual que el legacy (PedirAutorizacion): lo escaneado queda CARGADO conservando su hora y quién lo escaneó;
        // lo que falta queda FALTA, sin hora.
        if (escaneadas.Count > 0)
        {
            await conn.ExecuteAsync(
           @"UPDATE etiqueta_mcia
    SET relacion=@relno, estatus='CARGADO', fmodi=@time, usumodi=@usuario
    WHERE textoetiqueta IN @textos",
           new { relno, time = timeStr, usuario, textos = escaneadas });
        }
        if (faltantes.Count > 0)
        {
            await conn.ExecuteAsync(
           @"UPDATE etiqueta_mcia
    SET relacion=@relno, estatus='FALTA', fmodi=@time, usumodi=@usuario
    WHERE textoetiqueta IN @textos",
           new { relno, time = timeStr, usuario, textos = faltantes });
        }

        await conn.ExecuteAsync(
        $@"UPDATE {tabla}
 SET ESTATUS_CARGA=@estatus, usucargamcia=@usuario,
     inicio_carga = CASE WHEN inicio_carga IS NULL OR YEAR(inicio_carga) < 2000 THEN @time ELSE inicio_carga END,
     fin_carga=@time
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
        var (_, sufijo) = ResolverTabla(request.Relno);

        using IDbConnection conn = _db.CreateConnection();

        // Solo se pueden bajar los bultos que ya no pertenecen a la relación (en el legacy, la lista de descarga).
        var ajenas = await GetEtiquetasAjenasAsync(conn, request.Relno, sufijo);
        var etiqueta = ajenas.FirstOrDefault(e => e.TextoEtiqueta == request.TextoEtiqueta);

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
        var (_, sufijo) = ResolverTabla(relno);

        using IDbConnection conn = _db.CreateConnection();

        // Igual que el legacy (Descargar): no se toca el estado de la relación, que sigue CARGANDO por quien la tomó.
        var pendientes = await GetEtiquetasAjenasAsync(conn, relno, sufijo);
        if (pendientes.Count > 0)
        {
            result.Message = $"FALTAN {pendientes.Count} BULTOS POR DESCARGAR";
            return result;
        }

        result.Success = true;
        result.Message = "DESCARGA COMPLETA";
        return result;
    }


    public async Task<List<EtiquetaInfo>> GetEtiquetasDescargaAsync(string relno)
    {
        var (_, sufijo) = ResolverTabla(relno);
        using IDbConnection conn = _db.CreateConnection();

        // Solo los bultos que ya no pertenecen a la relación y siguen asignados a ella; ninguno viene como ya descargado.
        return await GetEtiquetasAjenasAsync(conn, relno, sufijo);
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

        string usuedit = ((IDictionary<string, object>)traf).TryGetValue("usuedit", out var valorUsuedit)
            ? valorUsuedit?.ToString() ?? ""
            : "";

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

        // Igual que el legacy (GetEtiqEntradas_SR): está cargada si ya tiene hora de escaneo.
        var etiquetaList = etiquetas.Select(r => new EtiquetaInfo
        {
            TextoEtiqueta = r.Textoetiqueta,
            TextoPagina = ConvShort(r.Textoetiqueta),
            Creferencia = r.Creferencia,
            Nlineno = r.Nlineno,
            Total = r.Total,
            Escaneado = r.TimeScan.HasValue && r.Estatus == "CARGADO",
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
        if (!string.IsNullOrWhiteSpace(usuedit))
            estatusEntrada = "ENTRADA OCUPADA";

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

            // Un bulto de una entrada directa/transbordo no debe estar asignado a una relación.
            if (!string.IsNullOrEmpty(etiqueta.Relacion) && etiqueta.Relacion != request.Entrada)
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

            // Igual que el legacy (SaveScanParticular("", …)): el bulto queda CARGADO con relacion vacía y la huella
            // completa en el historial (hora e IP). Antes se guardaba la entrada en `relacion`, que el legacy leería
            // como "entrada de otra relación agrupada".
            string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string ipSuffix = string.IsNullOrWhiteSpace(request.IpAddress) ? "" : $" {request.IpAddress}";
            string historialScan = $"ESCANEO {timeStr}{ipSuffix} , ";
            await conn.ExecuteAsync(
        @"UPDATE etiqueta_mcia
  SET relacion='', estatus='CARGADO', fmodi=@time, usumodi=@usuario,
    time_scan=@time, usucarga=@usuario,
    historial=CONCAT(IFNULL(historial,''), @historial)
  WHERE id_etiqueta_mcia=@idEtiqueta",
            new
            {
                time = timeStr,
                usuario = request.Usuario,
                historial = historialScan,
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

    /// <summary>
    /// Cierra una carga sin relación (entrada directa/transbordo). Los bultos ya quedaron guardados al escanear; aquí solo se
    /// verifica que no falte ninguno. (Antes la app mandaba la entrada a "finalizar" de relaciones y respondía RELACION NO ENCONTRADA.)
    /// </summary>
    public async Task<ResultadoOperacion> FinalizarSinRelacionAsync(string entrada, string usuario)
    {
        var result = new ResultadoOperacion();

        var carga = await CargarSinRelacionAsync(entrada, usuario);
        if (!carga.Success)
        {
            result.Message = carga.Message;
            return result;
        }

        if (carga.Relacion?.Estatus != "COMPLETA")
        {
            result.Message = carga.Relacion?.Estatus ?? "ENTRADA NO DISPONIBLE";
            return result;
        }

        int faltan = carga.Etiquetas.Count(e => !e.Escaneado);
        if (faltan > 0)
        {
            result.Message = "FALTAN BULTOS";
            return result;
        }

        result.Success = true;
        result.Message = "FINALIZADO";
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

        var dbRel = await conn.QueryFirstOrDefaultAsync<SalidaDbRelation>(
            $"SELECT {RelacionSelectFields} FROM {tabla} WHERE relno=@relno", new { relno });
        if (dbRel == null)
        {
            result.Message = "RELACION NO ENCONTRADA";
            return result;
        }

        var etiquetas = await GetEtiqEntradasAsync(conn, relno, sufijo);
        var ajenas = await GetEtiquetasAjenasAsync(conn, relno, sufijo);

        // Mismas condiciones con las que el legacy deshabilita el botón AUTORIZAR (RelacionBodega.aspx.cs:189-247),
        // ahora también del lado del servidor.
        string? motivo = SalidaReglas.MotivoNoSePuedeAutorizar(
            dbRel.Estatus,
            dbRel.EstatusCarga,
            dbRel.Usucargamcia,
            usuario,
            !string.IsNullOrWhiteSpace(dbRel.Usuedit),
            ajenas.Count > 0,
            etiquetas.Any(e => e.Estatus == "ERROR"));
        if (motivo != null)
        {
            result.Message = motivo;
            return result;
        }

        var textos = etiquetas
            .Where(e => !string.IsNullOrWhiteSpace(e.TextoEtiqueta))
            .Select(e => e.TextoEtiqueta)
            .ToList();
        if (textos.Count > 0)
        {
            await conn.ExecuteAsync(
           @"UPDATE etiqueta_mcia
    SET relacion=@relno, estatus='SALIDA', fmodi=@time, usumodi=@usuario
    WHERE textoetiqueta IN @textos",
           new { relno, time = timeStr, usuario, textos });
        }

        // Inicio y fin de la carga solo se escriben si nunca se cerró (legacy Autorizar, Relacion.cs:911-914);
        // antes se pisaban siempre con la hora de la autorización.
        await conn.ExecuteAsync(
     $@"UPDATE {tabla}
 SET ESTATUS_CARGA='AUTORIZADA', autoriza_carga=@time, usuautoriza=@usuario,
     inicio_carga = CASE WHEN fin_carga IS NULL OR YEAR(fin_carga) < 2000 THEN @time ELSE inicio_carga END,
     fin_carga = CASE WHEN fin_carga IS NULL OR YEAR(fin_carga) < 2000 THEN @time ELSE fin_carga END
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
        var actual = await conn.QueryFirstOrDefaultAsync<SalidaDbRelation>(
            $"SELECT {RelacionSelectFields} FROM {tabla} WHERE RELNO=@relno", new { relno });
        string usuarioCarga = actual?.Usucargamcia ?? "";

        if (accion == "DESBLOQUEO")
        {
            if (!SalidaReglas.EsEstadoBloqueo(actual?.EstatusCarga))
            {
                result.Message = "LA RELACION NO ESTA BLOQUEADA";
                return result;
            }

            // Bloqueo por reimpresión: el supervisor libera las etiquetas reimpresas (legacy DesbloquearReimpresion,
            // Relacion.cs:1023-1035) para que no vuelvan a bloquear la relación.
            if (actual!.EstatusCarga == "BLOQUEO REIMPRESION")
            {
                await conn.ExecuteAsync(
                    "UPDATE etiqueta_mcia SET reimpresa=0, desblq_etiq=@usuario WHERE relacion=@relno AND reimpresa=1",
                    new { relno, usuario = SalidaReglas.Recortar(usuario, 15) });
            }

            var etiquetas = (await conn.QueryAsync<EtiquetaDbRow>(
           @"SELECT textoetiqueta AS Textoetiqueta, historial AS Historial
    FROM etiqueta_mcia
    WHERE relacion=@relno AND historial LIKE '%BLOQUEO%'",
           new { relno })).ToList();

            // Se conserva la hora de escaneo de cada etiqueta (el legacy no la cambia al desbloquear).
            string hist = $"DESBLOQUEO {usuario} {timeStr} , ";
            foreach (var etiqueta in etiquetas.Where(e => EsHistorialBloqueado(e.Historial)))
            {
                await conn.ExecuteAsync(
               @"UPDATE etiqueta_mcia
   SET relacion=@relno, fmodi=@time, usumodi=@usuario,
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
        var (tabla, _) = ResolverTabla(relno);
        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using IDbConnection conn = _db.CreateConnection();

        string estatusCarga = await conn.ExecuteScalarAsync<string>(
            $"SELECT estatus_carga FROM {tabla} WHERE RELNO=@relno", new { relno }) ?? "";
        string? motivo = SalidaReglas.MotivoNoSePuedeRetornar(estatusCarga);
        if (motivo != null)
        {
            result.Message = motivo;
            return result;
        }

        // Todo lo asignado a la relación que quedó SALIDA/ERROR vuelve a CARGADO (legacy RetornaraEspera,
        // Relacion.cs:926-955). Las etiquetas de otra relación no se tocan.
        await conn.ExecuteAsync(
        @"UPDATE etiqueta_mcia
    SET estatus='CARGADO', fmodi=@time, usumodi=@usuario
    WHERE relacion=@relno AND estatus IN ('SALIDA','ERROR')",
        new { relno, time = timeStr, usuario });

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
