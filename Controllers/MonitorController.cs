using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Attributes;
using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class MonitorController : ControllerBase
{

    private readonly BaseRepository _db;

    private readonly IHubContext<WarehouseHub> _hub;

    private readonly ILogger<MonitorController> _logger;

    private readonly IRealtimeNotificationService _notifications;
    private readonly RevisionActivityService _activityService;

    private readonly IMonitorTvLinkService _monitorTvLinkService;

    public MonitorController(
        BaseRepository db,
        IHubContext<WarehouseHub> hub,
        ILogger<MonitorController> logger,
        IRealtimeNotificationService notifications,
        RevisionActivityService activityService,
        IMonitorTvLinkService monitorTvLinkService)
    {
        _db = db;
        _hub = hub;
        _logger = logger;
        _notifications = notifications;
        _activityService = activityService;
        _monitorTvLinkService = monitorTvLinkService;
    }

    [HttpGet("tv-links")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetTvLinks(CancellationToken cancellationToken)
    {
        IReadOnlyList<MonitorTvLinkItem> items = await _monitorTvLinkService.ListarAsync(cancellationToken);
        return Ok(new { success = true, items });
    }

    [HttpPost("tv-links")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor, "Modificar")]
    public async Task<IActionResult> CreateTvLink(
        [FromBody] CrearMonitorTvLinkRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ExpiraEnDias is < 0 or > 3650)
        {
            return BadRequest(new
            {
                success = false,
                message = "La vigencia debe estar entre 0 y 3650 días."
            });
        }

        CrearMonitorTvLinkResult result = await _monitorTvLinkService.CrearAsync(
            request,
            User.Identity?.Name ?? "sistema",
            cancellationToken);

        return Ok(new { success = true, item = result });
    }

    [HttpDelete("tv-links/{id:long}")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor, "Modificar")]
    public async Task<IActionResult> RevokeTvLink(long id, CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest(new { success = false, message = "Identificador inválido." });
        }

        bool revoked = await _monitorTvLinkService.RevocarAsync(
            id,
            User.Identity?.Name ?? "sistema",
            cancellationToken);

        return revoked
            ? Ok(new { success = true })
            : NotFound(new { success = false, message = "El enlace no existe o ya fue revocado." });
    }

    [HttpGet("actividades")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetActividades(
        [FromQuery] string? creferencia = null,
        [FromQuery] string? usuario = null,
        [FromQuery] DateTime? desde = null,
        [FromQuery] DateTime? hasta = null,
        [FromQuery] long? antesDe = null,
        [FromQuery] int limite = 30)
    {
        IReadOnlyList<RevisionActivityEntry> items = await _activityService.ObtenerAsync(
            creferencia,
            usuario,
            desde,
            hasta,
            antesDe,
            limite);

        return Ok(new
        {
            success = true,
            items,
            pagination = new
            {
                limit = Math.Clamp(limite, 1, 100),
                hasMore = items.Count == Math.Clamp(limite, 1, 100),
                nextCursor = items.Count == 0 ? (long?)null : items[^1].Id
            }
        });
    }

    [HttpGet("revisores")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetRevisoresActivos()
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            string sql = "SELECT t.creferencia, MAX(CASE WHEN t.finiciorevision >= '2000-01-01' THEN t.finiciorevision ELSE NULL END) AS finiciorevision,    MAX(CASE WHEN t.ffinrevision >= '2000-01-01' THEN t.ffinrevision ELSE NULL END) AS ffinrevision,    MAX(CASE WHEN t.ftomadocumento >= '2000-01-01' THEN t.ftomadocumento ELSE NULL END)  AS ftomadocumento,   MAX(COALESCE(t.estatus_final, 0))   AS estatus_final,    COALESCE( (SELECT usuario FROM traftiemposrevision  WHERE creferencia = t.creferencia    AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)  ORDER BY finiciorevision DESC, ftomadocumento DESC  LIMIT 1), (SELECT usuario FROM traftiemposrevision  WHERE creferencia = t.creferencia    AND usuario IS NOT NULL AND usuario != ''  ORDER BY idtiemposrevision DESC  LIMIT 1)    )    AS usuario,    MAX(c.cnombre)     AS nombre_cliente,    MAX(td.ubicacion)  AS ubicacion,    COUNT(DISTINCT f.cnofact) AS total_facturas,    COUNT(DISTINCT CASE WHEN f.status_rev = 1 THEN f.cnofact END)  AS facturas_completas,    COUNT(DISTINCT CASE WHEN f.status_rev = 2 THEN f.cnofact END)  AS facturas_discrepancia,    (SELECT discrepancias FROM fact WHERE cref_agrupada = t.creferencia AND status_rev = 2 ORDER BY dfecha DESC LIMIT 1) AS ultima_discrepancia,    (SELECT MAX(CASE WHEN rd.lhazmat = 1 THEN 1 ELSE 0 END)     FROM revdet rd WHERE rd.cref = t.creferencia)   AS lhazmat,    (SELECT MAX(CASE WHEN r2.STATUS != 'COMPLETA' THEN 1 ELSE 0 END)     FROM traf2 t2     INNER JOIN relacion r2 ON t2.nrelacion = r2.relno     WHERE t2.CREF_ORIGEN = t.creferencia) AS has_incomplete_relations,   (SELECT COALESCE(SUM(TIMESTAMPDIFF(SECOND, finiciorevision, ffinrevision)), 0)     FROM traftiemposrevision t2     WHERE t2.creferencia = t.creferencia AND t2.finiciorevision IS NOT NULL AND YEAR(t2.finiciorevision) >= 2000 AND t2.ffinrevision   IS NOT NULL AND YEAR(t2.ffinrevision)   >= 2000) AS segundos_acumulados,    (SELECT MAX(t3.finiciorevision)     FROM traftiemposrevision t3     WHERE t3.creferencia = t.creferencia AND t3.finiciorevision IS NOT NULL AND YEAR(t3.finiciorevision) >= 2000 AND (t3.ffinrevision IS NULL OR YEAR(t3.ffinrevision) < 2000) AND t3.finiciorevision >= DATE_SUB(NOW(), INTERVAL 18 HOUR)) AS inicio_sesion_actual FROM traftiemposrevision t LEFT JOIN fact f     ON t.creferencia = f.cref_agrupada LEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0 LEFT JOIN trafdet td ON td.creferencia = t.creferencia WHERE ( (t.ffinrevision IS NULL OR YEAR(t.ffinrevision) < 2000) AND t.usuario IS NOT NULL AND t.usuario != '' AND (     (t.finiciorevision IS NOT NULL AND YEAR(t.finiciorevision) >= 2000 AND t.finiciorevision >= DATE_SUB(NOW(), INTERVAL 18 HOUR)) OR     (t.ftomadocumento IS NOT NULL AND YEAR(t.ftomadocumento) >= 2000 AND t.ftomadocumento >= DATE_SUB(NOW(), INTERVAL 1 HOUR)) ))  OR ( t.ffinrevision IS NOT NULL AND YEAR(t.ffinrevision) >= 2000 AND t.ffinrevision >= DATE_SUB(NOW(), INTERVAL 15 MINUTE) AND t.estatus_final = 1     )GROUP BY t.creferencia ORDER BY COALESCE(MAX(t.finiciorevision), MAX(t.ftomadocumento)) DESC";
            // Esta consulta histórica se construyó originalmente en una sola
            // línea. Sustituimos únicamente el predicado del acumulado para
            // que el Monitor comparta la misma regla de sesiones sanas que el
            // Timer y los reportes, sin alterar sus demás filtros.
            sql = sql.Replace(
                "t2.finiciorevision IS NOT NULL AND YEAR(t2.finiciorevision) >= 2000 AND t2.ffinrevision   IS NOT NULL AND YEAR(t2.ffinrevision)   >= 2000",
                RevisionTimeRules.ClosedSessionPredicate("t2"),
                StringComparison.Ordinal);
            List<dynamic> rows = (await conn.QueryAsync<dynamic>(sql)).ToList();
            List<string> referencias = rows
                .Select((dynamic r) => (string)r.creferencia)
                .Where(referencia => !string.IsNullOrWhiteSpace(referencia))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Dictionary<string, string> estados = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (referencias.Count > 0)
            {
                string sqlEstados = @"
                    SELECT creferencia,
                           CASE
                               WHEN EXISTS (
                                   SELECT 1
                                   FROM traftiemposrevision abierta
                                   WHERE abierta.creferencia = historial.creferencia
                                     AND (abierta.ffinrevision IS NULL OR YEAR(abierta.ffinrevision) < 2000)
                                     AND abierta.finiciorevision IS NOT NULL
                                     AND YEAR(abierta.finiciorevision) >= 2000
                               ) THEN 'EN_REVISION'
                               WHEN EXISTS (
                                   SELECT 1
                                   FROM traftiemposrevision abierta
                                   WHERE abierta.creferencia = historial.creferencia
                                     AND (abierta.ffinrevision IS NULL OR YEAR(abierta.ffinrevision) < 2000)
                                     AND abierta.ftomadocumento IS NOT NULL
                                     AND YEAR(abierta.ftomadocumento) >= 2000
                               ) THEN 'DOCUMENTOS_TOMADOS'
                               WHEN EXISTS (
                                   SELECT 1
                                   FROM traftiemposrevision cerrada
                                   WHERE cerrada.creferencia = historial.creferencia
                                     AND cerrada.ffinrevision IS NOT NULL
                                     AND YEAR(cerrada.ffinrevision) >= 2000
                                     AND cerrada.ffinrevision >= DATE_SUB(NOW(), INTERVAL 15 MINUTE)
                                     AND cerrada.estatus_final = 1
                               ) THEN 'FINALIZADA'
                               ELSE 'INACTIVA'
                           END AS estado
                    FROM traftiemposrevision historial
                    WHERE creferencia IN @referencias
                    GROUP BY creferencia";
                IEnumerable<dynamic> stateRows = await conn.QueryAsync<dynamic>(sqlEstados, new { referencias });
                foreach (dynamic stateRow in stateRows)
                {
                    estados[(string)stateRow.creferencia] = (string)stateRow.estado;
                }
            }
            Dictionary<string, (int Total, int Revisadas)> partidasPorReferencia = new Dictionary<string, (int Total, int Revisadas)>(StringComparer.OrdinalIgnoreCase);
            if (referencias.Count > 0)
            {
                string sqlPartidas = @"
                    SELECT f.cref_agrupada AS creferencia,
                           COUNT(DISTINCT rd.idrevdet) AS total_partidas,
                           COUNT(DISTINCT CASE WHEN rd.revisada = 1 THEN rd.idrevdet END) AS partidas_revisadas
                    FROM fact f
                    LEFT JOIN revdet rd ON rd.cnofact = f.cnofact
                    WHERE f.cref_agrupada IN @referencias
                    GROUP BY f.cref_agrupada";
                IEnumerable<dynamic> partidaRows = await conn.QueryAsync<dynamic>(sqlPartidas, new { referencias });
                foreach (dynamic partidaRow in partidaRows)
                {
                    partidasPorReferencia[(string)partidaRow.creferencia] = (
                        Convert.ToInt32(partidaRow.total_partidas),
                        Convert.ToInt32(partidaRow.partidas_revisadas));
                }
            }
            var items = rows.Select((dynamic r) => new
            {
                creferencia = (string)r.creferencia,
                usuario = ((r.usuario != null) ? ((string)r.usuario) : ""),
                nombreCliente = ((r.nombre_cliente != null) ? ((string)r.nombre_cliente) : ""),
                ubicacion = ((r.ubicacion != null) ? ((string)r.ubicacion) : ""),
                inicioRevision = ((r.finiciorevision != null) ? ((DateTime)r.finiciorevision).ToString("yyyy-MM-ddTHH:mm:ss") : ""),
                finRevision = ((r.ffinrevision != null) ? ((DateTime)r.ffinrevision).ToString("yyyy-MM-ddTHH:mm:ss") : ""),
                tomaDocumento = ((r.ftomadocumento != null) ? ((DateTime)r.ftomadocumento).ToString("yyyy-MM-ddTHH:mm:ss") : ""),
                estatusFinal = ((r.estatus_final != null) ? ((int)r.estatus_final) : 0),
                totalFacturas = ((r.total_facturas != null) ? ((int)r.total_facturas) : 0),
                facturasCompletas = ((r.facturas_completas != null) ? ((int)r.facturas_completas) : 0),
                totalPartidas = partidasPorReferencia.TryGetValue((string)r.creferencia, out (int Total, int Revisadas) partidaCount) ? partidaCount.Total : 0,
                partidasRevisadas = partidasPorReferencia.TryGetValue((string)r.creferencia, out partidaCount) ? partidaCount.Revisadas : 0,
                facturasDiscrepancia = ((r.facturas_discrepancia != null) ? ((int)r.facturas_discrepancia) : 0),
                ultimaDiscrepancia = ((r.ultima_discrepancia != null) ? ((string)r.ultima_discrepancia) : ""),
                hazmat = ((r.lhazmat != null) ? ((int)r.lhazmat) : 0),
                relacionCerrada = (object)(r.has_incomplete_relations == null || (int)r.has_incomplete_relations == 0),
                segundosAcumulados = ((r.segundos_acumulados != null) ? ((long)r.segundos_acumulados) : 0),
                // Ancla de la sesión ABIERTA (finiciorevision de la revisión en curso, si
                // hay una); segundosAcumulados de arriba solo suma sesiones YA CERRADAS,
                // igual que TimerController. El cliente calcula el tiempo en vivo como
                // segundosAcumulados + (ahora - inicioSesionActual), en vez de confiar en
                // un contador local que este mismo endpoint pisaba en cada refresh -- ver
                // detalle.ts/restaurarSesion, que ya usa este mismo patrón.
                inicioSesionActual = ((r.inicio_sesion_actual != null) ? ((DateTime)r.inicio_sesion_actual).ToString("yyyy-MM-ddTHH:mm:ss") : null),
                estado = estados.TryGetValue((string)r.creferencia, out string? estadoActual) ? estadoActual : "INACTIVA",
                recienTerminada = (object)(estados.TryGetValue((string)r.creferencia, out string? estadoReciente) && estadoReciente == "FINALIZADA")
            });
            return Ok(new
            {
                success = true,
                items = items
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetRevisoresActivos");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudieron consultar los revisores activos."
            });
        }
    }

    [HttpGet("solicitudes")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetSolicitudesPendientes()
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            string sql = "SELECT id_desbloqueo AS id, cnofact, usuario_solicita AS usuario, motivo,  DATE_FORMAT(fecha_solicitud, '%Y-%m-%dT%H:%i:%s') AS fechaSolicitud FROM rev_desbloqueos WHERE estatus = 0 ORDER BY fecha_solicitud DESC";
            var items = (await conn.QueryAsync<object>(sql)).Select(delegate (dynamic r)
            {
                string text = (string)r.cnofact;
                string text2;
                if (!text.StartsWith("ENTRY_", StringComparison.OrdinalIgnoreCase))
                {
                    text2 = text;
                }
                else
                {
                    string text3 = text;
                    text2 = text3.Substring(6, text3.Length - 6);
                }
                string cnofact = text2;
                return new
                {
                    id = (int)r.id,
                    cnofact = cnofact,
                    usuario = (string)r.usuario,
                    motivo = (string)r.motivo,
                    fechaSolicitud = (string)r.fechaSolicitud
                };
            });
            return Ok(new
            {
                success = true,
                items = items
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetSolicitudesPendientes");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudieron consultar las solicitudes pendientes."
            });
        }
    }

    /// <summary>
    /// El "cnofact" de una solicitud de desbloqueo puede ser una factura real
    /// o "ENTRY_{crefAgrupada}" (solicitud a nivel entrada, desde la lista).
    /// La app necesita el crefAgrupada para poder abrir directo la entrada
    /// desde la notificación -- en el segundo caso ya lo tenemos; en el
    /// primero hay que resolverlo contra "fact".
    /// </summary>
    private static async Task<string?> ResolveCrefAgrupadaAsync(IDbConnection conn, string cnofact)
    {
        if (cnofact.StartsWith("ENTRY_", StringComparison.OrdinalIgnoreCase))
        {
            return cnofact[6..];
        }
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT cref_agrupada FROM fact WHERE cnofact = @cnofact LIMIT 1",
            new { cnofact });
    }

    [RequiereModulo(MobileModuleIds.SystemsMonitor, "Modificar")]
    [HttpPost("autorizar/{idDesbloqueo}")]
    public async Task<IActionResult> AutorizarDesbloqueo(int idDesbloqueo)
    {
        try
        {
            string supervisor = base.User.Identity?.Name ?? "Supervisor";
            using IDbConnection conn = _db.CreateConnection();
            dynamic solicitud = await conn.QueryFirstOrDefaultAsync<object>("SELECT cnofact, estatus, usuario_solicita FROM rev_desbloqueos WHERE id_desbloqueo = @id", new
            {
                id = idDesbloqueo
            });
            if (solicitud == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Solicitud no encontrada."
                });
            }

            if (Convert.ToInt32(solicitud.estatus) != 0)
            {
                return Conflict(new
                {
                    success = false,
                    message = "La solicitud ya fue procesada por otro supervisor."
                });
            }

            string cnofact = (string)solicitud.cnofact;
            string usuarioSolicita = (string)solicitud.usuario_solicita;
            string sql = "UPDATE rev_desbloqueos SET estatus = 1,  usuario_autoriza   = @supervisor,  fecha_autorizacion = NOW(),  fecha_expiracion   = DATE_ADD(NOW(), INTERVAL 30 MINUTE) WHERE id_desbloqueo = @id AND estatus = 0";
            int affectedRows = await conn.ExecuteAsync(sql, new
            {
                supervisor = supervisor,
                id = idDesbloqueo
            });
            if (affectedRows == 0)
            {
                return Conflict(new
                {
                    success = false,
                    message = "La solicitud ya fue procesada por otro supervisor."
                });
            }

            await _hub.Clients.All.SendAsync("onFacturaAutorizada", cnofact, supervisor);
            // App Android escucha "onDesbloqueoAprobado" con un objeto {cnofact, supervisor}
            // (no 2 argumentos sueltos) -- antes nunca le llegaba en vivo, solo por el
            // polling de respaldo cada 8s. El panel web ya escuchaba ambos nombres.
            await _hub.Clients.All.SendAsync("onDesbloqueoAprobado", new { cnofact, supervisor });

            string refAutorizada = cnofact.StartsWith("ENTRY_", StringComparison.OrdinalIgnoreCase) ? cnofact[6..] : cnofact;
            string? crefParaAbrir = await ResolveCrefAgrupadaAsync(conn, cnofact);
            var dataAutorizada = new Dictionary<string, string> { ["type"] = "desbloqueo_aprobado", ["cnofact"] = cnofact };
            if (crefParaAbrir != null) dataAutorizada["crefAgrupada"] = crefParaAbrir;
            _ = _notifications.SendToUserAsync(
                usuarioSolicita,
                "Entrada autorizada",
                $"{supervisor} te dio acceso a {refAutorizada}. Ya puedes continuar.",
                dataAutorizada);

            return Ok(new
            {
                success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en AutorizarDesbloqueo");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo autorizar la solicitud."
            });
        }
    }

    [RequiereModulo(MobileModuleIds.SystemsMonitor, "Modificar")]
    [HttpPost("rechazar/{idDesbloqueo}")]
    public async Task<IActionResult> RechazarDesbloqueo(int idDesbloqueo)
    {

        try
        {
            string supervisor = base.User.Identity?.Name ?? "Supervisor";
            using IDbConnection conn = _db.CreateConnection();
            dynamic solicitud = await conn.QueryFirstOrDefaultAsync<object>("SELECT cnofact, estatus, usuario_solicita FROM rev_desbloqueos WHERE id_desbloqueo = @id", new
            {
                id = idDesbloqueo
            });
            if (solicitud == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Solicitud no encontrada."
                });
            }

            if (Convert.ToInt32(solicitud.estatus) != 0)
            {
                return Conflict(new
                {
                    success = false,
                    message = "La solicitud ya fue procesada por otro supervisor."
                });
            }

            string cnofact = (string)solicitud.cnofact;
            string usuarioSolicita = (string)solicitud.usuario_solicita;
            string sql = "UPDATE rev_desbloqueos SET estatus = 2,  usuario_autoriza   = @supervisor,  fecha_autorizacion = NOW() WHERE id_desbloqueo = @id AND estatus = 0";
            int affectedRows = await conn.ExecuteAsync(sql, new
            {
                supervisor = supervisor,
                id = idDesbloqueo
            });
            if (affectedRows == 0)
            {
                return Conflict(new
                {
                    success = false,
                    message = "La solicitud ya fue procesada por otro supervisor."
                });
            }

            await _hub.Clients.All.SendAsync("onFacturaRechazada", cnofact, supervisor);
            await _hub.Clients.All.SendAsync("onDesbloqueoRechazado", new { cnofact, supervisor });

            string refRechazada = cnofact.StartsWith("ENTRY_", StringComparison.OrdinalIgnoreCase) ? cnofact[6..] : cnofact;
            string? crefRechazada = await ResolveCrefAgrupadaAsync(conn, cnofact);
            var dataRechazada = new Dictionary<string, string> { ["type"] = "desbloqueo_rechazado", ["cnofact"] = cnofact };
            if (crefRechazada != null) dataRechazada["crefAgrupada"] = crefRechazada;
            _ = _notifications.SendToUserAsync(
                usuarioSolicita,
                "Solicitud rechazada",
                $"{supervisor} rechazó tu solicitud para {refRechazada}.",
                dataRechazada);

            return Ok(new
            {
                success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en RechazarDesbloqueo");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo rechazar la solicitud."
            });
        }
    }

    [RequiereModulo(MobileModuleIds.SystemsMonitor, "Modificar")]
    [HttpPost("kill/{usuario}")]
    public async Task<IActionResult> ForzarCierre(string usuario, [FromBody] ForzarCierreRequest? request)
    {

        try
        {
            string supervisor = base.User.Identity?.Name ?? "Supervisor";
            string motivo = request?.Motivo?.Trim() ?? "";
            if (motivo.Length > 500)
            {
                motivo = motivo[..500];
            }
            if (string.IsNullOrWhiteSpace(motivo))
            {
                motivo = "Cierre solicitado por supervisión.";
            }

            _logger.LogInformation("Kill Switch activado por {sup} contra {usr}", supervisor, usuario);
            using IDbConnection conn = _db.CreateConnection();
            List<string> referencias = (await conn.QueryAsync<string>(@"
                SELECT DISTINCT creferencia
                FROM traftiemposrevision
                WHERE usuario = @usuario
                  AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)", new { usuario })).ToList();

            // Cierra CUALQUIER fila abierta del usuario -- timer iniciado o
            // solo "tomó documento" (finiciorevision NULL). Antes exigía
            // finiciorevision IS NOT NULL "porque ese caso no bloquea nada",
            // pero sí bloquea la vista: es justo lo que deja "Documento
            // tomado" pegado en el Monitor aunque la persona ya se fue, y el
            // propósito del kill switch es que el supervisor pueda limpiarlo.
            string sqlCerrarTimer = " UPDATE traftiemposrevision SET ffinrevision  = NOW(),   estatus_final = 0 WHERE usuario = @usuario AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)";
            await conn.ExecuteAsync(sqlCerrarTimer, new { usuario });

            // El timer es solo la mitad: si el dispositivo del usuario está
            // offline nunca recibe "onCierreForzoso" y por lo tanto nunca corre
            // su propia limpieza de `usuedit` (eso pasaba antes -- el kill
            // switch liberaba el timer pero la factura seguía marcada "en uso"
            // por el usuario expulsado). Se libera aquí, del lado servidor,
            // sin depender de que el cliente siga conectado.
            string sqlLiberarEdicion = "UPDATE fact SET usuedit = '' WHERE usuedit = @usuario";
            await conn.ExecuteAsync(sqlLiberarEdicion, new { usuario });

            foreach (string referencia in referencias)
            {
                await _activityService.RegistrarAsync(
                    referencia,
                    usuario,
                    RevisionActivityTypes.Pausada,
                    detalle: $"Sesión cerrada por {supervisor}. Motivo: {motivo}");
                await _hub.Clients.All.SendAsync("onRevisionEstadoCambiado", new
                {
                    creferencia = referencia,
                    usuario,
                    estado = "PAUSADA",
                    fecha = DateTime.Now.ToString("o"),
                    estatusFinal = 0
                });
            }

            await _hub.Clients.All.SendAsync("onActividadRecibida", "", usuario, "", "");
            // Se publica a todos con el usuario objetivo porque una tableta
            // puede perder la pertenencia al grupo durante una reconexión.
            // El cliente filtra el evento y solo la sesión indicada lo muestra.
            await _hub.Clients.All.SendAsync("onCierreForzoso", new
            {
                usuario,
                supervisor,
                motivo,
                mensaje = $"Tu sesión de revisión fue cerrada por {supervisor}."
            });
            return Ok(new
            {
                success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en ForzarCierre");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo forzar el cierre de sesión."
            });
        }
    }

    [HttpGet("permisos-abiertos")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetPermisosAbiertos()
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            string sql = @"
                SELECT id_desbloqueo AS id,
                       cnofact,
                       usuario_solicita AS usuario,
                       usuario_autoriza AS supervisor,
                       DATE_FORMAT(fecha_autorizacion, '%Y-%m-%dT%H:%i:%s') AS fechaAutorizacion,
                       DATE_FORMAT(fecha_expiracion, '%Y-%m-%dT%H:%i:%s') AS fechaExpiracion
                FROM rev_desbloqueos
                WHERE estatus = 1 AND fecha_expiracion > NOW()
                ORDER BY fecha_expiracion ASC";
            var items = await conn.QueryAsync<object>(sql);
            return Ok(new
            {
                success = true,
                items
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetPermisosAbiertos");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudieron consultar los permisos abiertos."
            });
        }
    }

    [RequiereModulo(MobileModuleIds.SystemsMonitor, "Modificar")]
    [HttpPost("revocar/{idDesbloqueo}")]
    public async Task<IActionResult> RevocarPermiso(int idDesbloqueo)
    {
        try
        {
            string supervisor = base.User.Identity?.Name ?? "Supervisor";
            using IDbConnection conn = _db.CreateConnection();

            dynamic? solicitud = await conn.QueryFirstOrDefaultAsync<object>(
                "SELECT cnofact, usuario_solicita FROM rev_desbloqueos WHERE id_desbloqueo = @id AND estatus = 1 AND fecha_expiracion > NOW()",
                new { id = idDesbloqueo });

            if (solicitud == null)
            {
                return Conflict(new
                {
                    success = false,
                    message = "El permiso ya no está activo (expiró o ya fue revocado)."
                });
            }

            // No se toca "estatus" ni se borra el registro: se cierra la
            // ventana de tiempo ahora mismo. VerificarDesbloqueoAsync ya
            // filtra por "fecha_expiracion > NOW()", así que basta con esto
            // para que el permiso deje de estar vigente al instante.
            int rows = await conn.ExecuteAsync(
                "UPDATE rev_desbloqueos SET fecha_expiracion = NOW() WHERE id_desbloqueo = @id AND estatus = 1 AND fecha_expiracion > NOW()",
                new { id = idDesbloqueo });

            if (rows == 0)
            {
                return Conflict(new
                {
                    success = false,
                    message = "El permiso ya no está activo (expiró o ya fue revocado)."
                });
            }

            string cnofact = (string)solicitud.cnofact;
            string usuarioSolicita = (string)solicitud.usuario_solicita;

            // Se reutiliza el mismo evento que "rechazar": Android y el panel
            // web ya reaccionan completo (cierran el modo edición, avisan al
            // revisor). Un evento nuevo exigiría cablear ambos clientes de
            // cero para lograr exactamente el mismo efecto.
            await _hub.Clients.All.SendAsync("onFacturaRechazada", cnofact, supervisor);
            await _hub.Clients.All.SendAsync("onDesbloqueoRechazado", new { cnofact, supervisor });

            _ = _notifications.SendToUserAsync(
                usuarioSolicita,
                "Permiso retirado",
                $"{supervisor} retiró tu permiso de edición para {cnofact}.",
                new Dictionary<string, string>
                {
                    ["type"] = "permiso_revocado",
                    ["cnofact"] = cnofact
                });

            return Ok(new
            {
                success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en RevocarPermiso");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo revocar el permiso."
            });
        }
    }

    [HttpGet("catalogos")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetCatalogos()
    {
        _ = 1;
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            return Ok(new
            {
                success = true,
                clientes = await conn.QueryAsync<object>("SELECT cidcte as id, cnombre as nombre FROM clientes WHERE inactivo = 0 ORDER BY cnombre"),
                proveedores = await conn.QueryAsync<object>("SELECT cidprov as id, cnombre as nombre FROM provs ORDER BY cnombre")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetCatalogos");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudieron consultar los catálogos."
            });
        }
    }

    [RequiereModulo(MobileModuleIds.SystemsAnalytics)]
    [HttpGet("velocidad-revisores")]
    public async Task<IActionResult> GetVelocidadRevisores([FromQuery] string? fechaInicio = null, [FromQuery] string? fechaFin = null, [FromQuery] string? cidcte = null, [FromQuery] string? cidprov = null)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            DateTime fi = (string.IsNullOrEmpty(fechaInicio) ? DateTime.Today : DateTime.Parse(fechaInicio));
            DateTime ff = (string.IsNullOrEmpty(fechaFin) ? DateTime.Today : DateTime.Parse(fechaFin));
            // Se traen las sesiones individuales (no agregadas) para poder calcular la
            // mediana en memoria. El volumen es bajo (~150 filas/semana, ~600 filas/mes).
            //
            // Dos correcciones respecto a la version anterior:
            //  1. Se elimina "t.estatus_final = 1". Esa columna siempre vale 0 en la base
            //     (el sistema legacy cierra las sesiones sin actualizarla), por lo que la
            //     consulta devolvia cero filas siempre y la grafica nunca pintaba nada.
            //  2. Se acotan las duraciones a [1, 240] minutos. El historico tiene 311
            //     duraciones negativas, 108 en cero y 322 mayores a 4 h (sesiones que
            //     alguien dejo abiertas), que distorsionaban el promedio.
            //
            // El DISTINCT sobre idtiemposrevision evita que los JOIN de filtro dupliquen
            // una misma sesion cuando la referencia tiene varias facturas o partidas.
            string sql = "\nSELECT DISTINCT\n    t.idtiemposrevision AS id,\n    t.usurevisa AS usuario,\n    TIMESTAMPDIFF(MINUTE, t.finiciorevision, t.ffinrevision) AS mins\nFROM traftiemposrevision t" + ((!string.IsNullOrEmpty(cidcte) || !string.IsNullOrEmpty(cidprov)) ? "\nJOIN fact f ON f.cref_agrupada = t.creferencia\nJOIN revdet rd ON rd.cnofact = f.cnofact" : "") + "\nWHERE t.finiciorevision IS NOT NULL AND YEAR(t.finiciorevision) >= 2000\n  AND t.ffinrevision IS NOT NULL AND YEAR(t.ffinrevision) >= 2000\n  AND TIMESTAMPDIFF(MINUTE, t.finiciorevision, t.ffinrevision) BETWEEN 1 AND 240\n  AND DATE(t.ffinrevision) BETWEEN @fi AND @ff\n  AND t.usurevisa IS NOT NULL AND t.usurevisa <> ''" + ((!string.IsNullOrEmpty(cidcte)) ? "\n  AND f.cclienteid = @cidcte" : "") + ((!string.IsNullOrEmpty(cidprov)) ? "\n  AND rd.cprovid = @cidprov" : "");
            var sesiones = await conn.QueryAsync<VelocidadSesionRow>(sql, new
            {
                fi = fi.ToString("yyyy-MM-dd"),
                ff = ff.ToString("yyyy-MM-dd"),
                cidcte = cidcte,
                cidprov = cidprov
            });
            var result = sesiones
                .GroupBy(s => s.Usuario)
                .Select(g =>
                {
                    var minutos = g.Select(s => (double)s.Mins).OrderBy(m => m).ToList();
                    return new
                    {
                        usuario = g.Key,
                        nombre = (string?)null,
                        tiempoPromedioMin = (object)Math.Round(minutos.Average(), 1),
                        tiempoMedianaMin = (object)Math.Round(Mediana(minutos), 1),
                        facturasRevisadas = minutos.Count
                    };
                })
                .OrderBy(r => Convert.ToDouble(r.tiempoMedianaMin))
                .ToList();
            return Ok(new
            {
                success = true,
                items = result
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetVelocidadRevisores");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo consultar la velocidad de los revisores."
            });
        }
    }

    [RequiereModulo(MobileModuleIds.SystemsAnalytics)]
    [HttpGet("meta-diaria")]
    public async Task<IActionResult> GetMetaDiaria([FromQuery] string? fecha = null, [FromQuery] string? fechaFin = null, [FromQuery] string? cidcte = null, [FromQuery] string? cidprov = null)
    {
        _ = 3;
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            DateTime targetDate = ((!string.IsNullOrEmpty(fechaFin)) ? DateTime.Parse(fechaFin) : ((!string.IsNullOrEmpty(fecha)) ? DateTime.Parse(fecha) : DateTime.Today));
            await conn.ExecuteAsync("\nCREATE TABLE IF NOT EXISTS monitor_config (\n    clave VARCHAR(50) PRIMARY KEY,\n    valor VARCHAR(200) NOT NULL,\n    fecha_mod DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP\n)");
            dynamic metaRow = await conn.QueryFirstOrDefaultAsync<object>("SELECT valor FROM monitor_config WHERE clave = 'meta_diaria'");
            int meta = ((metaRow != null) ? int.Parse((string)metaRow.valor) : 50);

            string joinFilter = ((!string.IsNullOrEmpty(cidcte) || !string.IsNullOrEmpty(cidprov)) ? "\nJOIN fact f ON f.cref_agrupada = t.creferencia\nJOIN revdet rd ON rd.cnofact = f.cnofact" : "");
            string whereCte = ((!string.IsNullOrEmpty(cidcte)) ? " AND f.cclienteid = @cidcte" : "");
            string whereProv = ((!string.IsNullOrEmpty(cidprov)) ? " AND rd.cprovid = @cidprov" : "");

            string sqlCompletadas = "\nSELECT COUNT(DISTINCT t.creferencia) AS cnt\nFROM traftiemposrevision t" + joinFilter + "\nWHERE t.ffinrevision IS NOT NULL AND YEAR(t.ffinrevision) >= 2000\n  AND DATE(t.ffinrevision) = @fecha" + whereCte + whereProv;
            int totalCompletadas = (int)(long)((dynamic)(await conn.QueryFirstAsync<object>(sqlCompletadas, new
            {
                fecha = targetDate.ToString("yyyy-MM-dd"),
                cidcte = cidcte,
                cidprov = cidprov
            }))).cnt;

            string sqlEnProceso = "\nSELECT COUNT(DISTINCT t.creferencia) AS cnt\nFROM traftiemposrevision t" + joinFilter + "\nWHERE (t.ffinrevision IS NULL OR YEAR(t.ffinrevision) < 2000)\n  AND t.finiciorevision IS NOT NULL AND YEAR(t.finiciorevision) >= 2000\n  AND DATE(t.finiciorevision) = @fecha" + whereCte + whereProv;
            int enProceso = (int)(long)((dynamic)(await conn.QueryFirstAsync<object>(sqlEnProceso, new
            {
                fecha = targetDate.ToString("yyyy-MM-dd"),
                cidcte = cidcte,
                cidprov = cidprov
            }))).cnt;

            DateTime fechaReferencia = targetDate;
            if (totalCompletadas == 0 && enProceso == 0)
            {
                string sqlUltimaFecha = "\nSELECT MAX(DATE(t.ffinrevision)) AS fecha\nFROM traftiemposrevision t" + joinFilter + "\nWHERE t.ffinrevision IS NOT NULL AND YEAR(t.ffinrevision) >= 2000" + whereCte + whereProv;
                var ultimaFecha = await conn.QueryFirstOrDefaultAsync<DateTime?>(sqlUltimaFecha, new { cidcte = cidcte, cidprov = cidprov });
                if (ultimaFecha.HasValue)
                {
                    fechaReferencia = ultimaFecha.Value;
                    totalCompletadas = (int)(long)((dynamic)(await conn.QueryFirstAsync<object>(sqlCompletadas, new
                    {
                        fecha = fechaReferencia.ToString("yyyy-MM-dd"),
                        cidcte = cidcte,
                        cidprov = cidprov
                    }))).cnt;
                    enProceso = (int)(long)((dynamic)(await conn.QueryFirstAsync<object>(sqlEnProceso, new
                    {
                        fecha = fechaReferencia.ToString("yyyy-MM-dd"),
                        cidcte = cidcte,
                        cidprov = cidprov
                    }))).cnt;
                }
            }

            int pendientes = Math.Max(0, meta - totalCompletadas - enProceso);
            double porcentaje = ((meta > 0) ? Math.Round((double)totalCompletadas / (double)meta * 100.0, 1) : 0.0);

            int desbloqueosActuales = await conn.ExecuteScalarAsync<int>(
    "SELECT COUNT(*) FROM rev_desbloqueos WHERE estatus = 0"
);
            int desbloqueosPeriodo = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM rev_desbloqueos WHERE estatus = 0 AND DATE(fecha_solicitud) = @fecha",
                new { fecha = fechaReferencia.ToString("yyyy-MM-dd") }
            );

            return Ok(new
            {
                success = true,
                data = new
                {
                    meta = meta,
                    completadas = totalCompletadas,
                    enProceso = enProceso,
                    pendientes = pendientes,
                    porcentaje = porcentaje,
                    fechaReferencia = fechaReferencia.ToString("yyyy-MM-dd"),
                    desbloqueosPendientesActuales = desbloqueosActuales,
                    desbloqueosPendientesPeriodo = desbloqueosPeriodo
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetMetaDiaria");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo consultar la meta diaria."
            });
        }
    }

    [RequiereModulo(MobileModuleIds.SystemsAnalytics, "Modificar")]
    [HttpPost("meta-diaria")]
    public async Task<IActionResult> SetMetaDiaria([FromBody] MetaDiariaRequest request)
    {
        _ = 1;
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            await conn.ExecuteAsync("\nCREATE TABLE IF NOT EXISTS monitor_config (\n    clave VARCHAR(50) PRIMARY KEY,\n    valor VARCHAR(200) NOT NULL,\n    fecha_mod DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP\n)");
            await conn.ExecuteAsync("\nINSERT INTO monitor_config (clave, valor) VALUES ('meta_diaria', @valor)\nON DUPLICATE KEY UPDATE valor = @valor", new
            {
                valor = request.Meta.ToString()
            });
            return Ok(new
            {
                success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en SetMetaDiaria");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo guardar la meta diaria."
            });
        }
    }

    [RequiereModulo(MobileModuleIds.SystemsAnalytics, "Modificar")]
    [HttpPost("configuracion")]
    public async Task<IActionResult> SetConfiguracion([FromBody] MonitorConfiguracionRequest request)
    {
        if (request.Meta < 1 || request.Meta > 10000)
        {
            return BadRequest(new
            {
                success = false,
                message = "La meta diaria debe estar entre 1 y 10,000."
            });
        }

        if (request.TurnoInicioHora < 0 || request.TurnoInicioHora > 23 ||
            request.TurnoFinHora < 0 || request.TurnoFinHora > 23 ||
            request.TurnoInicioHora >= request.TurnoFinHora)
        {
            return BadRequest(new
            {
                success = false,
                message = "El horario debe tener horas válidas y la hora inicial debe ser menor que la final."
            });
        }

        try
        {
            using IDbConnection conn = _db.CreateConnection();
            await conn.ExecuteAsync("\nCREATE TABLE IF NOT EXISTS monitor_config (\n    clave VARCHAR(50) PRIMARY KEY,\n    valor VARCHAR(200) NOT NULL,\n    fecha_mod DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP\n)");
            await conn.ExecuteAsync("""
                INSERT INTO monitor_config (clave, valor) VALUES
                    ('meta_diaria', @meta),
                    ('turno_inicio_hora', @turnoInicio),
                    ('turno_fin_hora', @turnoFin)
                ON DUPLICATE KEY UPDATE valor = VALUES(valor)
                """, new
            {
                meta = request.Meta.ToString(),
                turnoInicio = request.TurnoInicioHora.ToString(),
                turnoFin = request.TurnoFinHora.ToString()
            });

            return Ok(new
            {
                success = true,
                meta = request.Meta,
                turnoInicioHora = request.TurnoInicioHora,
                turnoFinHora = request.TurnoFinHora
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en SetConfiguracion");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo guardar la configuración del monitor."
            });
        }
    }

    [HttpGet("jornada")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetJornada([FromQuery] string? fecha = null)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            DateTime targetDate = string.IsNullOrEmpty(fecha) ? DateTime.Today : DateTime.Parse(fecha);
            string fechaSql = targetDate.ToString("yyyy-MM-dd");

            await conn.ExecuteAsync("\nCREATE TABLE IF NOT EXISTS monitor_config (\n    clave VARCHAR(50) PRIMARY KEY,\n    valor VARCHAR(200) NOT NULL,\n    fecha_mod DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP\n)");
            IEnumerable<dynamic> configRows = await conn.QueryAsync<dynamic>(
                "SELECT clave, valor FROM monitor_config WHERE clave IN ('meta_diaria', 'turno_inicio_hora', 'turno_fin_hora')");
            var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic row in configRows) config[(string)row.clave] = (string)row.valor;

            int meta = config.TryGetValue("meta_diaria", out string? metaStr) ? int.Parse(metaStr) : 50;
            int turnoInicio = config.TryGetValue("turno_inicio_hora", out string? tiStr) ? int.Parse(tiStr) : 8;
            int turnoFin = config.TryGetValue("turno_fin_hora", out string? tfStr) ? int.Parse(tfStr) : 17;

            // Cada fila cerrada de traftiemposrevision ES el cierre de una
            // entrada. usurevisa es quien la cerro -- AutoFinalizerService
            // ya deja 'AUTO-17H' ahi cuando cierra sola, así que ese mismo
            // campo distingue cierre manual de automático sin nada nuevo.
            string sqlCierres = @"
                SELECT t.creferencia,
                       DATE_FORMAT(t.ffinrevision, '%Y-%m-%dT%H:%i:%s') AS fecha,
                       COALESCE(NULLIF(TRIM(t.usurevisa), ''), NULLIF(TRIM(t.usuario), ''), 'N/D') AS usuario,
                       MAX(c.cnombre) AS nombreCliente
                FROM traftiemposrevision t
                LEFT JOIN fact f ON f.cref_agrupada = t.creferencia
                LEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0
                WHERE t.ffinrevision IS NOT NULL AND YEAR(t.ffinrevision) >= 2000
                  AND t.estatus_final = 1
                  AND DATE(t.ffinrevision) = @fecha
                GROUP BY t.creferencia, t.ffinrevision, t.usurevisa, t.usuario
                ORDER BY t.ffinrevision ASC";
            IEnumerable<dynamic> cierresRows = await conn.QueryAsync<dynamic>(sqlCierres, new { fecha = fechaSql });

            var cierres = cierresRows.Select((dynamic r) => new
            {
                creferencia = (string)r.creferencia,
                fecha = (string)r.fecha,
                usuario = (string)r.usuario,
                nombreCliente = r.nombreCliente != null ? (string)r.nombreCliente : "",
                automatico = ((string)r.usuario).StartsWith("AUTO", StringComparison.OrdinalIgnoreCase)
            }).ToList();

            return Ok(new
            {
                success = true,
                fecha = fechaSql,
                turnoInicioHora = turnoInicio,
                turnoFinHora = turnoFin,
                meta,
                cierres
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetJornada");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo consultar la jornada."
            });
        }
    }

    [RequiereModulo(MobileModuleIds.SystemsAnalytics)]
    [HttpGet("distribucion-clientes")]
    public async Task<IActionResult> GetDistribucionClientes([FromQuery] string? fechaInicio = null, [FromQuery] string? fechaFin = null, [FromQuery] string? cidcte = null, [FromQuery] string? cidprov = null)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            DateTime fi = (string.IsNullOrEmpty(fechaInicio) ? DateTime.Today : DateTime.Parse(fechaInicio));
            DateTime ff = (string.IsNullOrEmpty(fechaFin) ? DateTime.Today : DateTime.Parse(fechaFin));
            string sql = "\nSELECT\n    COALESCE(MAX(c.cnombre), 'Sin cliente') AS cliente,\n    COUNT(DISTINCT t.creferencia) AS cantidad\nFROM traftiemposrevision t\nLEFT JOIN fact f ON f.cref_agrupada = t.creferencia\nLEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0" + ((!string.IsNullOrEmpty(cidprov)) ? "\nJOIN revdet rd ON rd.cnofact = f.cnofact" : "") + "\nWHERE t.ffinrevision IS NOT NULL AND YEAR(t.ffinrevision) >= 2000\n  AND DATE(t.ffinrevision) BETWEEN @fi AND @ff" + ((!string.IsNullOrEmpty(cidcte)) ? "\n  AND f.cclienteid = @cidcte" : "") + ((!string.IsNullOrEmpty(cidprov)) ? "\n  AND rd.cprovid = @cidprov" : "") + "\nGROUP BY f.cclienteid\nORDER BY cantidad DESC\nLIMIT 10";
            var result = (await conn.QueryAsync<object>(sql, new
            {
                fi = fi.ToString("yyyy-MM-dd"),
                ff = ff.ToString("yyyy-MM-dd"),
                cidcte = cidcte,
                cidprov = cidprov
            })).Select((dynamic r) => new
            {
                cliente = (string)r.cliente,
                total = (int)(long)r.cantidad
            });
            return Ok(new
            {
                success = true,
                items = result
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetDistribucionClientes");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo consultar la distribución de clientes."
            });
        }
    }

    [RequiereModulo(MobileModuleIds.SystemsAnalytics)]
    [HttpGet("volumen-semanal")]
    public async Task<IActionResult> GetVolumenSemanal([FromQuery] string? fechaInicio = null, [FromQuery] string? fechaFin = null, [FromQuery] string? cidcte = null, [FromQuery] string? cidprov = null)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            DateTime fi = (string.IsNullOrEmpty(fechaInicio) ? DateTime.Today.AddDays(-6.0) : DateTime.Parse(fechaInicio));
            DateTime ff = (string.IsNullOrEmpty(fechaFin) ? DateTime.Today : DateTime.Parse(fechaFin));
            string joinProv = ((!string.IsNullOrEmpty(cidprov)) ? "\nJOIN revdet rd ON rd.cnofact = f.cnofact" : "");
            string whereCte = ((!string.IsNullOrEmpty(cidcte)) ? "\n  AND f.cclienteid = @cidcte" : "");
            string whereProv = ((!string.IsNullOrEmpty(cidprov)) ? "\n  AND rd.cprovid = @cidprov" : "");
            string sql = $"\nSELECT\n    DATE(f.dfecha) AS fecha_dia,\n    COUNT(DISTINCT f.cref_agrupada) AS total,\n    COUNT(DISTINCT CASE WHEN t.ffinrevision IS NOT NULL AND YEAR(t.ffinrevision) >= 2000 THEN t.creferencia END) AS completadas\nFROM fact f\nLEFT JOIN traftiemposrevision t ON t.creferencia = f.cref_agrupada{joinProv}\nWHERE DATE(f.dfecha) BETWEEN @fi AND @ff{whereCte}{whereProv}\nGROUP BY DATE(f.dfecha)\nORDER BY fecha_dia ASC";
            IEnumerable<dynamic> items = await conn.QueryAsync<object>(new CommandDefinition(sql, new
            {
                fi = fi.ToString("yyyy-MM-dd"),
                ff = ff.ToString("yyyy-MM-dd"),
                cidcte = cidcte,
                cidprov = cidprov
            }, null, 8));
            string[] daysEs = new string[7] { "Dom", "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb" };
            var result = items.Select(delegate (dynamic r)
            {
                DateTime dateTime = (DateTime)r.fecha_dia;
                return new
                {
                    fecha = dateTime.ToString("yyyy-MM-dd"),
                    dia = dateTime.ToString("dd") + " " + daysEs[(int)dateTime.DayOfWeek],
                    total = (int)(long)r.total,
                    completadas = (int)(long)r.completadas
                };
            });
            return Ok(new
            {
                success = true,
                items = result
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetVolumenSemanal");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo consultar el volumen semanal."
            });
        }
    }

    [HttpGet("bultos-hoy")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetBultosEscaneadosHoy()
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            var row = await conn.QueryFirstOrDefaultAsync<(int total, DateTime fecha)>(@"
SELECT
    COUNT(*)              AS total,
    MAX(DATE(time_scan))  AS fecha
FROM etiqueta_mcia
WHERE DATE(time_scan) = (
    SELECT COALESCE(
        (SELECT MAX(DATE(time_scan)) FROM etiqueta_mcia
         WHERE DATE(time_scan) = CURDATE()
           AND estatus NOT IN ('ELIMINAR','CANCELADO')),
        (SELECT MAX(DATE(time_scan)) FROM etiqueta_mcia
         WHERE estatus NOT IN ('ELIMINAR','CANCELADO')
           AND time_scan IS NOT NULL
           AND YEAR(time_scan) >= 2000)
    )
)
  AND estatus NOT IN ('ELIMINAR','CANCELADO')");
            return Ok(new { success = true, total = row.total, fechaReferencia = row.fecha.ToString("yyyy-MM-dd") });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetBultosEscaneadosHoy");
            return StatusCode(500, new { success = false, message = "No se pudo consultar el total de bultos escaneados." });
        }
    }

    [HttpGet("entradas-bodega-hoy")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetEntradasBodegaHoy()
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            var row = await conn.QueryFirstOrDefaultAsync<(int total, DateTime fecha)>(@"
SELECT
    COUNT(DISTINCT creferencia) AS total,
    MAX(DATE(fcreatrafico))     AS fecha
FROM traf
WHERE DATE(fcreatrafico) = (
    SELECT COALESCE(
        (SELECT MAX(DATE(fcreatrafico)) FROM traf
         WHERE DATE(fcreatrafico) = CURDATE() AND tipop IN ('C','D')),
        (SELECT MAX(DATE(fcreatrafico)) FROM traf
         WHERE tipop IN ('C','D'))
    )
)
  AND tipop IN ('C','D')");
            return Ok(new { success = true, total = row.total, fechaReferencia = row.fecha.ToString("yyyy-MM-dd") });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetEntradasBodegaHoy");
            return StatusCode(500, new { success = false, message = "No se pudo consultar el total de entradas a bodega." });
        }
    }

    [HttpGet("autorizaciones-stats")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetAutorizacionesStats()
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            var stats = await conn.QueryFirstOrDefaultAsync<object>(@"
SELECT
    SUM(CASE WHEN estatus = 1 THEN 1 ELSE 0 END) AS autorizadas,
    SUM(CASE WHEN estatus = 2 THEN 1 ELSE 0 END) AS rechazadas,
    ROUND(AVG(TIMESTAMPDIFF(SECOND, fecha_solicitud, fecha_autorizacion)) / 60.0, 1) AS tiempoPromedioMin
FROM rev_desbloqueos
WHERE estatus IN (1,2)
  AND fecha_autorizacion >= CURDATE()
  AND fecha_autorizacion < DATE_ADD(CURDATE(), INTERVAL 1 DAY)");
            int pendientes = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM rev_desbloqueos WHERE estatus = 0");

            dynamic s = stats;
            return Ok(new
            {
                success = true,
                data = new
                {
                    pendientes = pendientes,
                    autorizadas = (s != null && s.autorizadas != null) ? (int)(long)s.autorizadas : 0,
                    rechazadas = (s != null && s.rechazadas != null) ? (int)(long)s.rechazadas : 0,
                    tiempoPromedioMin = (s != null && s.tiempoPromedioMin != null) ? Convert.ToDouble(s.tiempoPromedioMin) : 0.0
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetAutorizacionesStats");
            return StatusCode(500, new { success = false, message = "No se pudo consultar el resumen de autorizaciones." });
        }
    }

    [HttpGet("autorizaciones-historial")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetAutorizacionesHistorial(
        [FromQuery] string? search = null,
        [FromQuery] DateTime? fechaInicio = null,
        [FromQuery] DateTime? fechaFin = null,
        [FromQuery] int pagina = 1,
        [FromQuery] int pageSize = 15)
    {
        try
        {
            pagina = Math.Max(1, pagina);
            pageSize = Math.Clamp(pageSize, 10, 100);
            DateTime startDate = (fechaInicio ?? DateTime.Today.AddDays(-6)).Date;
            DateTime endDate = (fechaFin ?? DateTime.Today).Date;
            if (startDate > endDate)
            {
                (startDate, endDate) = (endDate, startDate);
            }
            DateTime endDateExclusive = endDate.AddDays(1);

            string searchValue = search?.Trim() ?? string.Empty;
            var parameters = new
            {
                fechaInicio = startDate,
                fechaFinExclusiva = endDateExclusive,
                search = searchValue,
                searchPattern = $"%{searchValue}%",
                limit = pageSize,
                offset = (pagina - 1) * pageSize
            };

            using IDbConnection conn = _db.CreateConnection();
            const string whereClause = @"
WHERE d.estatus IN (1,2)
  AND d.fecha_autorizacion >= @fechaInicio
  AND d.fecha_autorizacion < @fechaFinExclusiva
  AND (
    @search = ''
    OR d.cnofact LIKE @searchPattern
    OR d.usuario_solicita LIKE @searchPattern
    OR COALESCE(d.usuario_autoriza, '') LIKE @searchPattern
    OR d.motivo LIKE @searchPattern
  )";

            long total = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM rev_desbloqueos d " + whereClause,
                parameters);

            var items = (await conn.QueryAsync<object>(@"
SELECT
    d.id_desbloqueo AS id,
    d.cnofact,
    CASE WHEN d.cnofact LIKE 'ENTRY_%' THEN SUBSTRING(d.cnofact, 7) ELSE d.cnofact END AS factura,
    d.usuario_solicita AS operador,
    d.motivo,
    d.estatus,
    CASE d.estatus WHEN 1 THEN 'Autorizada' ELSE 'Rechazada' END AS decision,
    d.usuario_autoriza AS supervisor,
    DATE_FORMAT(d.fecha_autorizacion, '%Y-%m-%dT%H:%i:%s') AS fechaDecision
FROM rev_desbloqueos d
" + whereClause + @"
ORDER BY d.fecha_autorizacion DESC, d.id_desbloqueo DESC
LIMIT @limit OFFSET @offset", parameters)).Select((dynamic r) => new
            {
                id = (int)r.id,
                referencia = (string)r.factura,
                operador = (string)r.operador,
                motivo = (string)r.motivo,
                estatus = (int)r.estatus,
                decision = (string)r.decision,
                supervisor = (r.supervisor != null) ? (string)r.supervisor : "",
                hora = (string)r.fechaDecision
            });
            return Ok(new
            {
                success = true,
                items = items,
                total = total,
                pagina = pagina,
                pageSize = pageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetAutorizacionesHistorial");
            return StatusCode(500, new { success = false, message = "No se pudo consultar el historial de autorizaciones." });
        }
    }

    [HttpGet("patio")]
    public async Task<IActionResult> GetPatio([FromServices] IModulePermissionService permissions, CancellationToken ct, [FromQuery] bool incluirAntiguos = false)
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username)) return Unauthorized();
        if (!await permissions.HasPermissionAsync(username, MobileModuleIds.SystemsMonitor, "Lectura", ct)
            && !await permissions.HasPermissionAsync(username, MobileModuleIds.Gate, "Lectura", ct)) return Forbid();
        try
        {
            using IDbConnection conn = _db.CreateConnection();

            const string sqlHistorico = @"
SELECT c.tipomov AS TipoMovimiento,
       TIMESTAMPDIFF(MINUTE, c.farribo, c.fsalida) / 60.0 AS Horas
FROM caseta c
WHERE YEAR(c.farribo) >= 2000
  AND YEAR(c.fsalida) >= 2000
  AND c.fsalida > c.farribo
  AND c.estatus = 'SALIDA'
  AND c.fsalida >= NOW() - INTERVAL 90 DAY";

            var historico = (await conn.QueryAsync<HistoricoDwellRow>(
                new CommandDefinition(sqlHistorico, commandTimeout: 15, cancellationToken: ct))).ToList();

            var baselines = historico
                .Where(h => !string.IsNullOrWhiteSpace(h.TipoMovimiento))
                .GroupBy(h => h.TipoMovimiento)
                .Where(g => g.Count() >= MuestraMinimaBaseline)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var horas = g.Select(h => h.Horas).OrderBy(h => h).ToList();
                        return new { Mediana = Percentil(horas, 0.50), Normal = Percentil(horas, 0.90), Muestra = horas.Count };
                    });

            const string sqlPatio = @"
SELECT c.idcaseta      AS IdCaseta,
       c.casetaid      AS CasetaId,
       c.nequipo       AS Caja,
       c.relacion      AS Relno,
       c.cclienteid    AS IdCliente,
       COALESCE((SELECT cl.cnombre FROM clientes cl WHERE cl.cidcte = c.cclienteid LIMIT 1), '') AS Cliente,
       c.transpore     AS LineaCaja,
       c.nunidad       AS Unidad,
       c.placas        AS Placas,
       c.transporu     AS Transportista,
       c.operador      AS Operador,
       c.estatus       AS Estatus,
       c.tipoop        AS TipoOperacion,
       c.tipomov       AS TipoMovimiento,
       c.bodega        AS Bodega,
       c.farribo       AS Arribo,
       TIMESTAMPDIFF(MINUTE, c.farribo, NOW()) / 60.0 AS HorasEnPiso
FROM caseta c
WHERE YEAR(c.farribo) >= 2000
  AND (c.fsalida IS NULL OR YEAR(c.fsalida) < 2000)
  AND c.estatus NOT IN ('CANCELADA', 'CANCELADO')
  AND (@incluirAntiguos = 1 OR c.farribo >= NOW() - INTERVAL @ventana DAY)
ORDER BY c.farribo ASC";

            var enPiso = (await conn.QueryAsync<PatioUnidadRow>(
                new CommandDefinition(sqlPatio, new { ventana = VentanaPatioDias, incluirAntiguos }, commandTimeout: 15, cancellationToken: ct))).ToList();

            const string sqlSinCerrar = @"
SELECT COUNT(*)
FROM caseta c
WHERE YEAR(c.farribo) >= 2000
  AND (c.fsalida IS NULL OR YEAR(c.fsalida) < 2000)
  AND c.estatus NOT IN ('CANCELADA', 'CANCELADO')
  AND c.farribo < NOW() - INTERVAL @ventana DAY";

            int registrosSinCerrar = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sqlSinCerrar, new { ventana = VentanaPatioDias }, commandTimeout: 15, cancellationToken: ct));

            var unidades = enPiso.Select(u =>
            {
                var tipo = u.TipoMovimiento ?? string.Empty;
                bool tieneBaseline = baselines.TryGetValue(tipo, out var b);
                double normal = tieneBaseline ? b!.Normal : 0;
                double horas = Math.Round(u.HorasEnPiso, 1);

                double veces = (tieneBaseline && normal > 0) ? Math.Round(horas / normal, 1) : 0;
                string severidad = !tieneBaseline || normal <= 0
                    ? "sin-referencia"
                    : horas > normal * 2 ? "critico"
                    : horas > normal ? "atencion"
                    : "ok";

                return new
                {
                    idCaseta = u.IdCaseta,
                    casetaId = u.CasetaId ?? "",
                    caja = u.Caja ?? "",
                    relno = u.Relno ?? "",
                    idCliente = u.IdCliente ?? "",
                    cliente = u.Cliente ?? "",
                    lineaCaja = u.LineaCaja ?? "",
                    unidad = u.Unidad ?? "",
                    placas = u.Placas ?? "",
                    transportista = u.Transportista ?? "",
                    operador = u.Operador ?? "",
                    estatus = u.Estatus ?? "",
                    tipoOperacion = u.TipoOperacion ?? "",
                    tipoMovimiento = tipo,
                    bodega = u.Bodega ?? "",
                    arribo = u.Arribo.ToString("yyyy-MM-ddTHH:mm:ss"),
                    horasEnPiso = horas,
                    normalHoras = tieneBaseline ? Math.Round(normal, 1) : (double?)null,
                    medianaHoras = tieneBaseline ? Math.Round(b!.Mediana, 1) : (double?)null,
                    veces = veces,
                    severidad = severidad
                };
            }).ToList();

            return Ok(new
            {
                success = true,
                generadoEn = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                ventanaDias = VentanaPatioDias,
                resumen = new
                {
                    enPiso = unidades.Count,
                    criticas = unidades.Count(u => u.severidad == "critico"),
                    requierenAtencion = unidades.Count(u => u.severidad == "atencion"),
                    registrosSinCerrar = registrosSinCerrar
                },
                baselines = baselines
                    .OrderByDescending(kv => kv.Value.Muestra)
                    .Select(kv => new
                    {
                        tipoMovimiento = kv.Key,
                        medianaHoras = Math.Round(kv.Value.Mediana, 1),
                        normalHoras = Math.Round(kv.Value.Normal, 1),
                        muestra = kv.Value.Muestra
                    }),
                unidades = unidades
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GetPatio");
            return StatusCode(500, new { success = false, message = "No se pudo consultar el estado del patio." });
        }
    }

    /// <summary>Dias de arribo que se consideran patio operativo.</summary>
    private const int VentanaPatioDias = 7;

    /// <summary>Visitas cerradas minimas para confiar en la referencia de un tipo de movimiento.</summary>
    private const int MuestraMinimaBaseline = 20;

    /// <summary>Percentil por interpolacion nula sobre una lista ya ordenada ascendentemente.</summary>
    private static double Percentil(IReadOnlyList<double> ordenados, double fraccion)
    {
        if (ordenados.Count == 0)
        {
            return 0;
        }

        int indice = (int)Math.Ceiling(ordenados.Count * fraccion) - 1;
        return ordenados[Math.Clamp(indice, 0, ordenados.Count - 1)];
    }

    private sealed class HistoricoDwellRow
    {
        public string TipoMovimiento { get; set; } = string.Empty;
        public double Horas { get; set; }
    }

    private sealed class PatioUnidadRow
    {
        public string? CasetaId { get; set; }
        public string? Caja { get; set; }
        public string? Relno { get; set; }
        public string? IdCliente { get; set; }
        public string? Cliente { get; set; }
        public string? LineaCaja { get; set; }
        public int IdCaseta { get; set; }
        public string? Unidad { get; set; }
        public string? Placas { get; set; }
        public string? Transportista { get; set; }
        public string? Operador { get; set; }
        public string? Estatus { get; set; }
        public string? TipoOperacion { get; set; }
        public string? TipoMovimiento { get; set; }
        public string? Bodega { get; set; }
        public DateTime Arribo { get; set; }
        public double HorasEnPiso { get; set; }
    }

    /// <summary>
    /// Mediana de una lista de valores ya ordenada de forma ascendente.
    /// Se usa en lugar del promedio para medir a los revisores: una sola sesion larga
    /// distorsiona el promedio lo suficiente como para cambiar el orden del ranking.
    /// </summary>
    private static double Mediana(IReadOnlyList<double> ordenados)
    {
        if (ordenados.Count == 0)
        {
            return 0;
        }

        int medio = ordenados.Count / 2;
        return (ordenados.Count % 2 == 1)
            ? ordenados[medio]
            : (ordenados[medio - 1] + ordenados[medio]) / 2.0;
    }

    /// <summary>Una sesion de revision individual, sin agregar.</summary>
    private sealed class VelocidadSesionRow
    {
        public long Id { get; set; }
        public string Usuario { get; set; } = string.Empty;
        public int Mins { get; set; }
    }
}
