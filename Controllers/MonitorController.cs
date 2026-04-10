using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Dapper;
using PMMovil.Api.Data;
using PMMovil.Api.Hubs;

namespace PMMovil.Api.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class MonitorController : ControllerBase
    {
        private readonly BaseRepository _db;
        private readonly IHubContext<WarehouseHub> _hub;
        private readonly ILogger<MonitorController> _logger;

        public MonitorController(BaseRepository db, IHubContext<WarehouseHub> hub, ILogger<MonitorController> logger)
        {
            _db = db;
            _hub = hub;
            _logger = logger;
        }

        // =====================================================================
        // REVISORES ACTIVOS
        // Misma lógica que MonitorRevisadores.aspx.cs → GetRevisadoresActivos()
        // =====================================================================
        [HttpGet("revisores")]
        public async Task<IActionResult> GetRevisoresActivos()
        {
            try
            {
                using var conn = _db.CreateConnection();

                string sql = @"
SELECT
    t.creferencia,
    MAX(CASE WHEN t.finiciorevision >= '2000-01-01' THEN t.finiciorevision ELSE NULL END) AS finiciorevision,
    MAX(CASE WHEN t.ftomadocumento >= '2000-01-01' THEN t.ftomadocumento ELSE NULL END)  AS ftomadocumento,
    MAX(COALESCE(t.estatus_final, 0))                                        AS estatus_final,
    (SELECT usuario FROM traftiemposrevision
     WHERE creferencia = t.creferencia
       AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
     ORDER BY finiciorevision DESC, ftomadocumento DESC
     LIMIT 1)                                                                AS usuario,
    MAX(c.cnombre)                                                           AS nombre_cliente,
    MAX(td.ubicacion)                                                        AS ubicacion,
    COUNT(DISTINCT f.cnofact)                                                AS total_facturas,
    COUNT(DISTINCT CASE WHEN f.status_rev = 1 THEN f.cnofact END)           AS facturas_completas,
    (SELECT MAX(CASE WHEN rd.lhazmat IN ('S','1') THEN 1 ELSE 0 END)
     FROM revdet rd WHERE rd.cref = t.creferencia)                          AS lhazmat,
    (SELECT MAX(CASE WHEN r2.STATUS != 'COMPLETA' THEN 1 ELSE 0 END)
     FROM traf2 t2
     INNER JOIN relacion r2 ON t2.nrelacion = r2.relno
     WHERE t2.CREF_ORIGEN = t.creferencia)                                  AS has_incomplete_relations
FROM traftiemposrevision t
LEFT JOIN fact f     ON t.creferencia = f.cref_agrupada
LEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0
LEFT JOIN trafdet td ON td.creferencia = t.creferencia
WHERE (
        -- Sesión activa (sin cerrar) y CON usuario asignado
        (t.ffinrevision IS NULL OR YEAR(t.ffinrevision) < 2000)
        AND t.usuario IS NOT NULL AND t.usuario != ''
        AND (
            (t.finiciorevision IS NOT NULL AND YEAR(t.finiciorevision) >= 2000
             AND t.finiciorevision >= DATE_SUB(NOW(), INTERVAL 18 HOUR)) -- Reducido de 24h a 18h
            OR
            (t.ftomadocumento IS NOT NULL AND YEAR(t.ftomadocumento) >= 2000
             AND t.ftomadocumento >= DATE_SUB(NOW(), INTERVAL 1 HOUR)) -- Reducido de 2h a 1h (PickUp es rápido)
        )
     )
  OR (
        -- Revisión recién terminada (completada en los últimos 15 min)
        t.ffinrevision IS NOT NULL AND YEAR(t.ffinrevision) >= 2000
        AND t.ffinrevision >= DATE_SUB(NOW(), INTERVAL 15 MINUTE)
        AND t.estatus_final = 1
     )
GROUP BY t.creferencia
ORDER BY COALESCE(MAX(t.finiciorevision), MAX(t.ftomadocumento)) DESC";

                var rows = await conn.QueryAsync<dynamic>(sql);

                var now = DateTime.Now;
                var items = rows.Select(r => new
                {
                    creferencia       = (string)r.creferencia,
                    usuario           = r.usuario != null ? (string)r.usuario : "",
                    nombreCliente     = r.nombre_cliente != null ? (string)r.nombre_cliente : "",
                    ubicacion         = r.ubicacion != null ? (string)r.ubicacion : "",
                    inicioRevision    = r.finiciorevision != null ? ((DateTime)r.finiciorevision).ToString("yyyy-MM-ddTHH:mm:ss") : "",
                    tomaDocumento     = r.ftomadocumento != null ? ((DateTime)r.ftomadocumento).ToString("yyyy-MM-ddTHH:mm:ss") : "",
                    estatusFinal      = r.estatus_final != null ? (int)r.estatus_final : 0,
                    totalFacturas     = r.total_facturas != null ? (int)r.total_facturas : 0,
                    facturasCompletas = r.facturas_completas != null ? (int)r.facturas_completas : 0,
                    hazmat            = r.lhazmat != null ? (int)r.lhazmat : 0,
                    relacionCerrada   = r.has_incomplete_relations == null || (int)r.has_incomplete_relations == 0,
                    // true cuando la revisión se completó en los últimos 15 min (para el monitor mostrarla brevemente)
                    recienTerminada   = r.estatus_final != null && (int)r.estatus_final == 1
                                        && r.finiciorevision != null
                                        && (now - (DateTime)r.finiciorevision).TotalMinutes <= 15
                });

                return Ok(new { success = true, items });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en GetRevisoresActivos");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // =====================================================================
        // SOLICITUDES PENDIENTES DE DESBLOQUEO
        // =====================================================================
        [HttpGet("solicitudes")]
        public async Task<IActionResult> GetSolicitudesPendientes()
        {
            try
            {
                using var conn = _db.CreateConnection();

                string sql = @"SELECT id_desbloqueo AS id, cnofact, usuario_solicita AS usuario, motivo,
                                      DATE_FORMAT(fecha_solicitud, '%Y-%m-%dT%H:%i:%s') AS fechaSolicitud
                               FROM rev_desbloqueos
                               WHERE estatus = 0
                               ORDER BY fecha_solicitud DESC";

                var rows = await conn.QueryAsync<dynamic>(sql);

                var items = rows.Select(r =>
                {
                    string rawCnofact = (string)r.cnofact;
                    string clean = rawCnofact.StartsWith("ENTRY_", StringComparison.OrdinalIgnoreCase)
                        ? rawCnofact[6..] : rawCnofact;
                    return new
                    {
                        id            = (int)r.id,
                        cnofact       = clean,
                        usuario       = (string)r.usuario,
                        motivo        = (string)r.motivo,
                        fechaSolicitud = (string)r.fechaSolicitud
                    };
                });

                return Ok(new { success = true, items });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en GetSolicitudesPendientes");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // =====================================================================
        // AUTORIZAR DESBLOQUEO
        // =====================================================================
        [HttpPost("autorizar/{idDesbloqueo}")]
        public async Task<IActionResult> AutorizarDesbloqueo(int idDesbloqueo)
        {
            try
            {
                string supervisor = User.Identity?.Name ?? "Supervisor";
                string cnofact = "";

                using var conn = _db.CreateConnection();

                // Obtener cnofact y usuario_solicita antes de actualizar
                var solicitud = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT cnofact, usuario_solicita FROM rev_desbloqueos WHERE id_desbloqueo = @id", new { id = idDesbloqueo });
                
                if (solicitud == null)
                    return BadRequest(new { success = false, message = "Solicitud no encontrada." });

                cnofact = (string)solicitud.cnofact;
                string usuarioSolicita = (string)solicitud.usuario_solicita;

                string sql = @"UPDATE rev_desbloqueos
                               SET estatus = 1,
                                   usuario_autoriza   = @supervisor,
                                   fecha_autorizacion = NOW(),
                                   fecha_expiracion   = DATE_ADD(NOW(), INTERVAL 30 MINUTE)
                               WHERE id_desbloqueo = @id AND estatus = 0";

                int rows = await conn.ExecuteAsync(sql, new { supervisor, id = idDesbloqueo });

                if (rows == 0)
                    return BadRequest(new { success = false, message = "Solicitud no encontrada o ya procesada." });

                // Notificar via SignalR (PARIDAD 1:1 con legacy)
                // Se envía al usuario que solicitó para que su App reaccione al instante.
                await _hub.Clients.Group($"USER_{usuarioSolicita}").SendAsync("onFacturaAutorizada", cnofact, supervisor);
                // También se puede enviar a All para refrescar dashboards generales (opcional en legacy, pero recomendado aquí)
                await _hub.Clients.All.SendAsync("onFacturaAutorizada", cnofact, supervisor);

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en AutorizarDesbloqueo");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // =====================================================================
        // RECHAZAR DESBLOQUEO
        // =====================================================================
        [HttpPost("rechazar/{idDesbloqueo}")]
        public async Task<IActionResult> RechazarDesbloqueo(int idDesbloqueo)
        {
            try
            {
                string supervisor = User.Identity?.Name ?? "Supervisor";
                string cnofact = "";

                using var conn = _db.CreateConnection();

                var solicitud = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT cnofact, usuario_solicita FROM rev_desbloqueos WHERE id_desbloqueo = @id", new { id = idDesbloqueo });

                if (solicitud == null)
                    return BadRequest(new { success = false, message = "Solicitud no encontrada." });

                cnofact = (string)solicitud.cnofact;
                string usuarioSolicita = (string)solicitud.usuario_solicita;

                string sql = @"UPDATE rev_desbloqueos
                               SET estatus = 2,
                                   usuario_autoriza   = @supervisor,
                                   fecha_autorizacion = NOW()
                               WHERE id_desbloqueo = @id AND estatus = 0";

                await conn.ExecuteAsync(sql, new { supervisor, id = idDesbloqueo });

                // Notificar rechazo via SignalR al usuario específico (PARIDAD 1:1)
                await _hub.Clients.Group($"USER_{usuarioSolicita}").SendAsync("onFacturaRechazada", cnofact, supervisor);

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en RechazarDesbloqueo");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // =====================================================================
        // KILL SWITCH: FORZAR CIERRE DE SESIÓN
        // =====================================================================
        [HttpPost("kill/{usuario}")]
        public async Task<IActionResult> ForzarCierre(string usuario)
        {
            try
            {
                string supervisor = User.Identity?.Name ?? "Supervisor";
                _logger.LogInformation("Kill Switch activado por {sup} contra {usr}", supervisor, usuario);

                using var conn = _db.CreateConnection();

                // 1. Cerrar todas las sesiones de timer abiertas de este usuario en BD.
                //    Usamos estatus_final = 0 para indicar abandono forzoso (no completado).
                string sqlCerrarTimer = @"
                    UPDATE traftiemposrevision
                    SET ffinrevision  = NOW(),
                        estatus_final = 0
                    WHERE usuario = @usuario
                      AND finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000
                      AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)";

                await conn.ExecuteAsync(sqlCerrarTimer, new { usuario });

                // 2. Notificar vía Hub que el revisor salió (para actualizar badges en tiempo real)
                // PARIDAD: Se envían 4 argumentos separados
                await _hub.Clients.All.SendAsync("onActividadRecibida", "", usuario, "", "");

                // 3. Enviar la señal de cierre forzoso al dispositivo del revisor
                // PARIDAD: El mensaje es el argumento principal
                await _hub.Clients.Group($"USER_{usuario}").SendAsync("onCierreForzoso", $"Tu sesión ha sido finalizada por {supervisor}.");

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en ForzarCierre");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }
    }
}
