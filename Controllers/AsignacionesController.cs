using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.Authorization;
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
[RequiereModulo(MobileModuleIds.Review)]
[ApiController]
[Route("api/[controller]")]
public class AsignacionesController : ControllerBase
{

    private readonly BaseRepository _db;

    private readonly IHubContext<WarehouseHub> _hub;

    private readonly ILogger<AsignacionesController> _logger;

    private readonly IConfiguration _config;

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly IRealtimeNotificationService _notifications;

    private readonly RevisionService _revisionService;

    private const string CreateTableSql = "\nCREATE TABLE IF NOT EXISTS asignaciones_revision (\n    id_asignacion INT AUTO_INCREMENT PRIMARY KEY,\n    cref_agrupada VARCHAR(50)  NOT NULL,\n    usuario_asignado   VARCHAR(50)  NOT NULL,\n    usuario_asigna     VARCHAR(50)  NOT NULL,\n    fecha_asignacion   DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,\n    fecha_visto   DATETIME     NULL,\n    fecha_completada   DATETIME     NULL,\n    prioridad TINYINT NOT NULL DEFAULT 0,\n    nota VARCHAR(500) NULL,\n    estatus   TINYINT NOT NULL DEFAULT 0,\n    INDEX idx_usuario_estatus (usuario_asignado, estatus),\n    INDEX idx_cref (cref_agrupada),\n    INDEX idx_fecha   (fecha_asignacion)\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

    public AsignacionesController(BaseRepository db, IHubContext<WarehouseHub> hub, ILogger<AsignacionesController> logger, IConfiguration config, IHttpClientFactory httpClientFactory, IRealtimeNotificationService notifications, RevisionService revisionService)
    {
        _db = db;
        _hub = hub;
        _logger = logger;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _notifications = notifications;
        _revisionService = revisionService;
    }

    [HttpPost("asignar")]
    public async Task<IActionResult> Asignar([FromBody] AsignarRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CrefAgrupada) || string.IsNullOrWhiteSpace(req.Usuario))
        {
            return BadRequest(new
            {
                success = false,
                message = "crefAgrupada y usuario son obligatorios"
            });
        }
        try
        {
            string supervisor = base.User.Identity?.Name ?? "Supervisor";
            using IDbConnection conn = _db.CreateConnection();
            await conn.ExecuteAsync("\nCREATE TABLE IF NOT EXISTS asignaciones_revision (\n    id_asignacion INT AUTO_INCREMENT PRIMARY KEY,\n    cref_agrupada VARCHAR(50)  NOT NULL,\n    usuario_asignado   VARCHAR(50)  NOT NULL,\n    usuario_asigna     VARCHAR(50)  NOT NULL,\n    fecha_asignacion   DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,\n    fecha_visto   DATETIME     NULL,\n    fecha_completada   DATETIME     NULL,\n    prioridad TINYINT NOT NULL DEFAULT 0,\n    nota VARCHAR(500) NULL,\n    estatus   TINYINT NOT NULL DEFAULT 0,\n    INDEX idx_usuario_estatus (usuario_asignado, estatus),\n    INDEX idx_cref (cref_agrupada),\n    INDEX idx_fecha   (fecha_asignacion)\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
            int? idExistente = await conn.ExecuteScalarAsync<int?>(" SELECT id_asignacion FROM asignaciones_revision WHERE cref_agrupada = @cref AND usuario_asignado = @usuario AND estatus IN (0, 1) ORDER BY fecha_asignacion DESC LIMIT 1", new
            {
                cref = req.CrefAgrupada,
                usuario = req.Usuario
            });
            int idAsignacion;
            if (idExistente.HasValue)
            {
                await conn.ExecuteAsync("UPDATE asignaciones_revision SET usuario_asigna  = @supervisor, prioridad = @prioridad, nota = @nota, fecha_asignacion = CURRENT_TIMESTAMP,  estatus    = 0,  fecha_visto     = NULL   WHERE id_asignacion = @id", new
                {
                    supervisor = supervisor,
                    prioridad = req.Prioridad,
                    nota = (req.Nota ?? ""),
                    id = idExistente.Value
                });
                idAsignacion = idExistente.Value;
            }
            else
            {
                idAsignacion = await conn.ExecuteScalarAsync<int>("INSERT INTO asignaciones_revision (cref_agrupada, usuario_asignado, usuario_asigna, prioridad, nota) VALUES (@cref, @usuario, @supervisor, @prioridad, @nota); SELECT LAST_INSERT_ID();", new
                {
                    cref = req.CrefAgrupada,
                    usuario = req.Usuario,
                    supervisor = supervisor,
                    prioridad = req.Prioridad,
                    nota = (req.Nota ?? "")
                });
            }
            try
            {
                await _hub.Clients.Group("USER_" + req.Usuario).SendAsync("onRevisionAsignada", new
                {
                    idAsignacion = idAsignacion,
                    crefAgrupada = req.CrefAgrupada,
                    supervisor = supervisor,
                    prioridad = req.Prioridad,
                    nota = (req.Nota ?? "")
                });
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "La asignación {Id} se guardó sin aviso en vivo.", idAsignacion);
            }

            // Que un supervisor asigne la entrada a alguien YA cuenta como
            // autorización de acceso -- no debería tener que, además,
            // solicitar desbloqueo y esperar a que otro supervisor se lo
            // apruebe por separado. Si tenía una solicitud pendiente para
            // esta entrada, se aprueba aquí mismo; si no tenía ninguna, se
            // crea una ya aprobada. Vigencia más larga que un desbloqueo
            // pedido "en caliente" (30 min en AutorizarDesbloqueo): una
            // asignación puede esperar hasta que el usuario de verdad se
            // siente a trabajarla.
            //
            // Excepción: si la relación de esta entrada ya se cerró, NO se
            // auto-desbloquea -- mismo criterio duro que
            // RevisionService.SolicitarDesbloqueoAsync (relación cerrada =
            // cierre físico/logístico definitivo, ni pidiendo se puede).
            if (!await _revisionService.EsRelacionCerradaAsync(req.CrefAgrupada, conn))
            {
                string entryId = $"ENTRY_{req.CrefAgrupada}";
                int filasAprobadas = await conn.ExecuteAsync(@"
                    UPDATE rev_desbloqueos
                    SET estatus = 1, usuario_autoriza = @supervisor, fecha_autorizacion = NOW(), fecha_expiracion = DATE_ADD(NOW(), INTERVAL 72 HOUR)
                    WHERE cnofact = @entryId AND usuario_solicita = @usuario AND estatus = 0",
                    new { entryId, usuario = req.Usuario, supervisor });

                if (filasAprobadas == 0)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO rev_desbloqueos (cnofact, usuario_solicita, motivo, estatus, usuario_autoriza, fecha_autorizacion, fecha_expiracion)
                        VALUES (@entryId, @usuario, 'Asignación directa del supervisor', 1, @supervisor, NOW(), DATE_ADD(NOW(), INTERVAL 72 HOUR))",
                        new { entryId, usuario = req.Usuario, supervisor });
                }

                try
                {
                    await _hub.Clients.Group("USER_" + req.Usuario).SendAsync("onDesbloqueoAprobado", new { cnofact = entryId, supervisor });
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "La autorización de {Usuario} se guardó sin aviso en vivo.", req.Usuario);
                }
            }

            // La bandeja conserva la asignación y FCM avisa a las sesiones activas.
            await _notifications.SendToUserOrInboxAsync(
                req.Usuario,
                "Revisión asignada",
                $"Entrada {req.CrefAgrupada}" + (string.IsNullOrWhiteSpace(req.Nota) ? "" : $" — {req.Nota}"),
                new Dictionary<string, string>
                {
                    ["type"] = "revision_asignada",
                    ["crefAgrupada"] = req.CrefAgrupada
                });

            return Ok(new
            {
                success = true,
                idAsignacion = idAsignacion
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error asignando {cref} a {usr}", req.CrefAgrupada, req.Usuario);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al asignar"
            });
        }
    }

    [HttpGet("mis-asignaciones")]
    public async Task<IActionResult> MisAsignaciones()
    {
        _ = 1;
        try
        {
            string usuario = base.User.Identity?.Name ?? "";
            if (string.IsNullOrEmpty(usuario))
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "No autenticado"
                });
            }
            using IDbConnection conn = _db.CreateConnection();
            await conn.ExecuteAsync("\nCREATE TABLE IF NOT EXISTS asignaciones_revision (\n    id_asignacion INT AUTO_INCREMENT PRIMARY KEY,\n    cref_agrupada VARCHAR(50)  NOT NULL,\n    usuario_asignado   VARCHAR(50)  NOT NULL,\n    usuario_asigna     VARCHAR(50)  NOT NULL,\n    fecha_asignacion   DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,\n    fecha_visto   DATETIME     NULL,\n    fecha_completada   DATETIME     NULL,\n    prioridad TINYINT NOT NULL DEFAULT 0,\n    nota VARCHAR(500) NULL,\n    estatus   TINYINT NOT NULL DEFAULT 0,\n    INDEX idx_usuario_estatus (usuario_asignado, estatus),\n    INDEX idx_cref (cref_agrupada),\n    INDEX idx_fecha   (fecha_asignacion)\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
            return Ok(new
            {
                success = true,
                items = await conn.QueryAsync<object>(" SELECT   a.id_asignacion    AS idAsignacion,   a.cref_agrupada    AS crefAgrupada,   a.usuario_asigna   AS supervisor,   DATE_FORMAT(a.fecha_asignacion, '%Y-%m-%dT%H:%i:%s') AS fechaAsignacion,   a.prioridad   AS prioridad,   a.nota AS nota,   a.estatus AS estatus,   COALESCE(MAX(c.cnombre), '')     AS nombreCliente,   COUNT(DISTINCT f.cnofact)   AS totalFacturas,   COUNT(DISTINCT CASE WHEN f.status_rev = 1 THEN f.cnofact END) AS facturasCompletas FROM asignaciones_revision a LEFT JOIN fact f     ON f.cref_agrupada = a.cref_agrupada LEFT JOIN clientes c ON c.cidcte = f.cclienteid AND c.inactivo = 0 WHERE a.usuario_asignado = @usuario AND a.estatus IN (0, 1) GROUP BY a.id_asignacion ORDER BY a.prioridad DESC, a.fecha_asignacion DESC", new { usuario })
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en MisAsignaciones");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al listar asignaciones"
            });
        }
    }

    [HttpGet("por-entrada/{crefAgrupada}")]
    public async Task<IActionResult> PorEntrada(string crefAgrupada)
    {
        _ = 1;
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            await conn.ExecuteAsync("\nCREATE TABLE IF NOT EXISTS asignaciones_revision (\n    id_asignacion INT AUTO_INCREMENT PRIMARY KEY,\n    cref_agrupada VARCHAR(50)  NOT NULL,\n    usuario_asignado   VARCHAR(50)  NOT NULL,\n    usuario_asigna     VARCHAR(50)  NOT NULL,\n    fecha_asignacion   DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,\n    fecha_visto   DATETIME     NULL,\n    fecha_completada   DATETIME     NULL,\n    prioridad TINYINT NOT NULL DEFAULT 0,\n    nota VARCHAR(500) NULL,\n    estatus   TINYINT NOT NULL DEFAULT 0,\n    INDEX idx_usuario_estatus (usuario_asignado, estatus),\n    INDEX idx_cref (cref_agrupada),\n    INDEX idx_fecha   (fecha_asignacion)\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
            return Ok(new
            {
                success = true,
                items = await conn.QueryAsync<object>(" SELECT   id_asignacion AS idAsignacion,   cref_agrupada AS crefAgrupada,   usuario_asignado  AS usuarioAsignado,   usuario_asigna    AS supervisor,   DATE_FORMAT(fecha_asignacion, '%Y-%m-%dT%H:%i:%s')  AS fechaAsignacion,   DATE_FORMAT(fecha_visto, '%Y-%m-%dT%H:%i:%s')  AS fechaVisto,   DATE_FORMAT(fecha_completada, '%Y-%m-%dT%H:%i:%s')  AS fechaCompletada,   prioridad,   nota,   estatus FROM asignaciones_revision WHERE cref_agrupada = @cref ORDER BY fecha_asignacion DESC", new
                {
                    cref = crefAgrupada
                })
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en PorEntrada {cref}", crefAgrupada);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al listar asignaciones"
            });
        }
    }

    [HttpPost("marcar-vista/{idAsignacion}")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> MarcarVista(int idAsignacion)
    {
        _ = 1;
        try
        {
            string usuario = base.User.Identity?.Name ?? "";
            using IDbConnection conn = _db.CreateConnection();
            await conn.ExecuteAsync("\nCREATE TABLE IF NOT EXISTS asignaciones_revision (\n    id_asignacion INT AUTO_INCREMENT PRIMARY KEY,\n    cref_agrupada VARCHAR(50)  NOT NULL,\n    usuario_asignado   VARCHAR(50)  NOT NULL,\n    usuario_asigna     VARCHAR(50)  NOT NULL,\n    fecha_asignacion   DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,\n    fecha_visto   DATETIME     NULL,\n    fecha_completada   DATETIME     NULL,\n    prioridad TINYINT NOT NULL DEFAULT 0,\n    nota VARCHAR(500) NULL,\n    estatus   TINYINT NOT NULL DEFAULT 0,\n    INDEX idx_usuario_estatus (usuario_asignado, estatus),\n    INDEX idx_cref (cref_agrupada),\n    INDEX idx_fecha   (fecha_asignacion)\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
            return Ok(new
            {
                success = true,
                cambio = (await conn.ExecuteAsync(" UPDATE asignaciones_revision SET fecha_visto = CURRENT_TIMESTAMP,   estatus     = 1 WHERE id_asignacion = @id AND usuario_asignado = @usuario AND estatus = 0", new
                {
                    id = idAsignacion,
                    usuario = usuario
                }) > 0)
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error marcando vista {id}", idAsignacion);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al marcar como vista"
            });
        }
    }

    [HttpDelete("{idAsignacion}")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> Cancelar(int idAsignacion)
    {
        _ = 3;
        try
        {
            string supervisor = base.User.Identity?.Name ?? "Supervisor";
            using IDbConnection conn = _db.CreateConnection();
            await conn.ExecuteAsync("\nCREATE TABLE IF NOT EXISTS asignaciones_revision (\n    id_asignacion INT AUTO_INCREMENT PRIMARY KEY,\n    cref_agrupada VARCHAR(50)  NOT NULL,\n    usuario_asignado   VARCHAR(50)  NOT NULL,\n    usuario_asigna     VARCHAR(50)  NOT NULL,\n    fecha_asignacion   DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,\n    fecha_visto   DATETIME     NULL,\n    fecha_completada   DATETIME     NULL,\n    prioridad TINYINT NOT NULL DEFAULT 0,\n    nota VARCHAR(500) NULL,\n    estatus   TINYINT NOT NULL DEFAULT 0,\n    INDEX idx_usuario_estatus (usuario_asignado, estatus),\n    INDEX idx_cref (cref_agrupada),\n    INDEX idx_fecha   (fecha_asignacion)\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
            dynamic row = await conn.QueryFirstOrDefaultAsync<object>("SELECT cref_agrupada, usuario_asignado FROM asignaciones_revision WHERE id_asignacion = @id", new
            {
                id = idAsignacion
            });
            if (row == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Asignación no encontrada"
                });
            }
            await conn.ExecuteAsync(" UPDATE asignaciones_revision SET estatus = 3 WHERE id_asignacion = @id", new
            {
                id = idAsignacion
            });
            string usuarioAsignado = (string)row.usuario_asignado;
            string cref = (string)row.cref_agrupada;
            await _hub.Clients.Group("USER_" + usuarioAsignado).SendAsync("onRevisionDesasignada", new
            {
                idAsignacion = idAsignacion,
                crefAgrupada = cref,
                supervisor = supervisor
            });
            return Ok(new
            {
                success = true
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error cancelando {id}", idAsignacion);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al cancelar"
            });
        }
    }

    // Se puede asignar una entrada a los revisores de la lista oficial
    // (ReviewerRoster) que sigan activos. Antes se tomaban del departamento 12,
    // que incluía a gente que no revisa y dejaba fuera a alarumbe y
    // eder.magana (están en el 7).
    //
    // La clave se devuelve tal como está guardada en usuarios: algunas traen
    // relleno de espacios y ese es el mismo valor que lleva el token. De él
    // dependen el grupo de SignalR "USER_<clave>" y la consulta de "mis
    // asignaciones"; recortarla aquí haría que el revisor no recibiera sus
    // avisos ni viera lo que se le asignó.
    [HttpGet("revisores-disponibles")]
    public async Task<IActionResult> GetRevisoresDisponibles()
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            return Ok(new
            {
                success = true,
                items = await conn.QueryAsync<object>("SELECT claveusuario AS usuario, TRIM(CONCAT_WS(' ', TRIM(apaterno), TRIM(amaterno), TRIM(nombre))) AS nombre, COALESCE(area, '') AS area FROM usuarios WHERE inactivo = 0 AND TRIM(claveusuario) IN @claves ORDER BY nombre", new { claves = ReviewerRoster.All.ToArray() })
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetRevisoresDisponibles");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al listar revisores"
            });
        }
    }

    [HttpPost("completar-por-entrada/{crefAgrupada}")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> CompletarPorEntrada(string crefAgrupada)
    {
        _ = 1;
        try
        {
            string usuario = base.User.Identity?.Name ?? "";
            using IDbConnection conn = _db.CreateConnection();
            return Ok(new
            {
                success = true,
                actualizadas = await conn.ExecuteAsync("UPDATE asignaciones_revision SET estatus = 2,   fecha_completada = CURRENT_TIMESTAMP WHERE cref_agrupada    = @cref AND usuario_asignado = @usuario AND estatus IN (0, 1)", new
                {
                    cref = crefAgrupada,
                    usuario = usuario
                })
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error completando asignaciones para {cref}", crefAgrupada);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al completar asignación"
            });
        }
    }

    [HttpGet("activas")]
    public async Task<IActionResult> TodasActivas()
    {
        _ = 1;
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            return Ok(new
            {
                success = true,
                items = await conn.QueryAsync<object>("SELECT id_asignacion AS idAsignacion, cref_agrupada AS crefAgrupada, usuario_asignado AS usuarioAsignado, prioridad, estatus FROM asignaciones_revision WHERE estatus IN (0, 1) ORDER BY fecha_asignacion DESC")
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en TodasActivas");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al listar asignaciones activas"
            });
        }
    }

    [HttpGet("info-entrada/{crefAgrupada}")]
    public async Task<IActionResult> InfoEntrada(string crefAgrupada)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            dynamic info = await conn.QueryFirstOrDefaultAsync<object>(@"
                SELECT
                    MAX(f.cclienteid) AS clienteId,
                    MAX(c.cnombre) AS nombreCliente,
                    COUNT(DISTINCT f.cnofact) AS totalFacturas,
                    COUNT(DISTINCT CASE WHEN COALESCE(f.status_rev, 0) = 1 THEN f.cnofact END) AS facturasCompletas,
                    COUNT(DISTINCT NULLIF(TRIM(t2.nrelacion), '')) AS totalRelaciones,
                    GROUP_CONCAT(DISTINCT NULLIF(TRIM(t2.nrelacion), '') ORDER BY NULLIF(TRIM(t2.nrelacion), '') SEPARATOR ',') AS relaciones,
                    GROUP_CONCAT(DISTINCT NULLIF(TRIM(f.usuedit), '') SEPARATOR ',') AS usuariosEditando,
                    CASE WHEN EXISTS (
                        SELECT 1
                        FROM traftiemposrevision tr
                        WHERE tr.creferencia = f.cref_agrupada
                          AND tr.estatus_final = 1
                    ) THEN 1 ELSE 0 END AS sesionRevisionTerminada
                FROM fact f
                LEFT JOIN clientes c ON c.cidcte = f.cclienteid
                LEFT JOIN traf2 t2 ON t2.CREF_ORIGEN = f.cref_agrupada
                    OR t2.creferencia = f.cref_agrupada
                WHERE f.cref_agrupada = @cref
                GROUP BY f.cref_agrupada", new
            {
                cref = crefAgrupada
            });
            if (info == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Entrada no encontrada"
                });
            }

            int totalFacturas = Convert.ToInt32(info.totalFacturas);
            int facturasCompletas = Convert.ToInt32(info.facturasCompletas);
            bool revisionTerminada = totalFacturas > 0
                && facturasCompletas >= totalFacturas
                || Convert.ToInt32(info.sesionRevisionTerminada) == 1;

            return Ok(new
            {
                success = true,
                clienteId = (object)info.clienteId,
                nombreCliente = (object)(info.nombreCliente ?? ""),
                totalFacturas,
                facturasCompletas,
                revisionTerminada,
                tieneRelacion = Convert.ToInt32(info.totalRelaciones) > 0,
                relaciones = SepararValores(info.relaciones),
                usuariosEditando = SepararValores(info.usuariosEditando)
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en InfoEntrada {cref}", crefAgrupada);
            return StatusCode(500, new
            {
                success = false,
                message = "Error interno"
            });
        }
    }

    private static List<string> SepararValores(object? value)
    {
        return (Convert.ToString(value) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Búsqueda parcial para el modal de "Asignar revisión". Los usuarios NO
    /// buscan la referencia completa (p. ej. "01C263581360000") -- están
    /// acostumbrados de otros sistemas a teclear solo el tramo después del
    /// año ("01C26"), en este ejemplo "358136". Como ese tramo ya es
    /// literalmente una subcadena de la referencia completa, un LIKE simple
    /// contra cref_agrupada resuelve ambos formatos (completo o recortado)
    /// sin tener que recalcular offsets de posición.
    /// </summary>
    [HttpGet("buscar-entrada")]
    public async Task<IActionResult> BuscarEntrada([FromQuery] string query)
    {
        try
        {
            string q = (query ?? "").Trim();
            if (q.Length < 3)
            {
                return Ok(new { success = true, items = Array.Empty<object>() });
            }

            using IDbConnection conn = _db.CreateConnection();
            var items = await conn.QueryAsync<object>(@"
                SELECT
                    f.cref_agrupada AS crefAgrupada,
                    MAX(f.cclienteid) AS clienteId,
                    MAX(cl.cnombre) AS nombreCliente,
                    MIN(f.cnofact) AS facturaTop,
                    COUNT(DISTINCT f.cnofact) AS totalFacturas
                FROM fact f
                LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid AND cl.inactivo = 0
                WHERE f.cref_agrupada LIKE @likeQuery
                GROUP BY f.cref_agrupada
                ORDER BY MAX(f.dfecha) DESC
                LIMIT 15",
                new { likeQuery = "%" + q.Replace("%", "\\%").Replace("_", "\\_") + "%" });

            return Ok(new { success = true, items });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en BuscarEntrada {query}", query);
            return StatusCode(500, new { success = false, message = "Error al buscar la entrada" });
        }
    }

    [HttpGet("sugerir-revisor/{crefAgrupada}")]
    public async Task<IActionResult> SugerirRevisor(string crefAgrupada)
    {
        _ = 7;
        try
        {
            string apiKey = _config["GeminiApiKey"]?.Trim();
            if (string.IsNullOrEmpty(apiKey))
            {
                return Ok(new
                {
                    success = false,
                    message = "Falta configurar GeminiApiKey en appsettings.json"
                });
            }
            using IDbConnection conn = _db.CreateConnection();
            dynamic entradaInfo = await conn.QueryFirstOrDefaultAsync<object>(" SELECT  f.cclienteid,  MAX(c.cnombre) AS nombreCliente,  COUNT(DISTINCT f.cnofact) AS totalFacturas,  COUNT(DISTINCT r.idrevdet) AS totalPartidas  FROM fact f  LEFT JOIN clientes c ON c.cidcte = f.cclienteid  LEFT JOIN revdet r ON r.idfactura = f.idfactura  WHERE f.cref_agrupada = @cref  GROUP BY f.cclienteid", new
            {
                cref = crefAgrupada
            });
            if (entradaInfo == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Entrada no encontrada"
                });
            }
            IEnumerable<object> historico = await conn.QueryAsync<object>(" SELECT  f.usurev AS revisor,  COUNT(*) AS cantidad,  AVG(TIMESTAMPDIFF(MINUTE, t.finiciorevision, t.ffinrevision)) AS tiempoPromedioMin  FROM fact f  LEFT JOIN traftiemposrevision t ON t.creferencia = f.cref_agrupada  AND t.usuario = f.usurev  AND " + RevisionTimeRules.ClosedSessionPredicate("t") + "  WHERE f.cclienteid = @clienteId  AND f.usurev IS NOT NULL AND f.usurev != ''  GROUP BY f.usurev  ORDER BY cantidad DESC  LIMIT 8", new
            {
                clienteId = (object)entradaInfo.cclienteid
            });
            string[] revisoresConfig = _config.GetSection("RevisoresHabilitados").Get<string[]>() ?? Array.Empty<string>();
            IEnumerable<object> usuarios = await conn.QueryAsync<object>(" SELECT u.claveusuario AS usuario, TRIM(CONCAT_WS(' ', TRIM(u.apaterno), TRIM(u.amaterno), TRIM(u.nombre))) AS nombre, u.area, COUNT(t.idtiemposrevision) AS entradasActivasAhora FROM usuarios u LEFT JOIN traftiemposrevision t ON t.usuario = u.claveusuario   AND (t.ffinrevision IS NULL OR t.ffinrevision < '2000-01-01') WHERE u.claveusuario IN @revisores GROUP BY u.claveusuario, u.apaterno, u.amaterno, u.nombre, u.area", new
            {
                revisores = revisoresConfig
            });
            var dataParaAnalisis = new
            {
                EntradaActual = new
                {
                    Referencia = crefAgrupada,
                    Cliente = (string)entradaInfo.nombreCliente,
                    TotalFacturas = (int)entradaInfo.totalFacturas,
                    TotalPartidas = (int)entradaInfo.totalPartidas
                },
                HistoricoRevisoresPorCliente = historico.Select((dynamic h) => new
                {
                    Revisor = (string)h.revisor,
                    VecesRevisado = (int)h.cantidad,
                    TiempoPromedioMin = ((h.tiempoPromedioMin != null) ? ((double?)h.tiempoPromedioMin) : ((double?)null))
                }),
                RevisoresDisponibles = usuarios.Select((dynamic val) => new
                {
                    Usuario = (string)val.usuario,
                    Nombre = (string)val.nombre,
                    Area = (string)val.area,
                    EntradasActivasAhora = (int)val.entradasActivasAhora
                })
            };
            string prompt = "Eres un experto en asignación de revisores. Analiza el JSON y sugiere un revisor de la lista 'RevisoresDisponibles'.\nCriterios: 1. Historial con el cliente. 2. Menos 'entradasActivasAhora'. 3. Menor 'TiempoPromedioMin'.\nDevuelve ÚNICAMENTE un JSON (sin Markdown):\n{ \"usuarioSugerido\": \"clave\", \"justificacion\": \"texto breve (máximo 15 palabras)\" }\nContexto JSON:\n" + JsonSerializer.Serialize(dataParaAnalisis, new JsonSerializerOptions
            {
                WriteIndented = false
            });
            string urlString = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=" + Uri.EscapeDataString(apiKey);
            if (!Uri.TryCreate(urlString, UriKind.Absolute, out Uri absoluteUri))
            {
                _logger.LogError("La URL generada para Gemini no es válida o está vacía: '{url}'", urlString);
                return StatusCode(500, new
                {
                    success = false,
                    message = "Error de configuración interna (URI de IA inválida)"
                });
            }
            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = prompt
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.2
                }
            };
            HttpClient client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30.0);
            HttpResponseMessage response = await client.PostAsJsonAsync(absoluteUri, requestBody);
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                _logger.LogError("Error Gemini HTTP {code}: {err}", (int)response.StatusCode, err);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogInformation("El modelo no fue encontrado. Consultando lista de modelos permitidos para esta API Key...");
                    string modelsUrl = "https://generativelanguage.googleapis.com/v1/models?key=" + Uri.EscapeDataString(apiKey);
                    HttpResponseMessage modelsResponse = await client.GetAsync(modelsUrl);
                    if (modelsResponse.IsSuccessStatusCode)
                    {
                        string modelsJson = await modelsResponse.Content.ReadAsStringAsync();
                        _logger.LogWarning("Lista de modelos devuelta por Google (busca uno que soporte generateContent): {modelsJson}", modelsJson);
                    }
                    else
                    {
                        _logger.LogError("Tampoco se pudo consultar la lista de modelos. HTTP {code}", (int)modelsResponse.StatusCode);
                    }
                }
                return Ok(new
                {
                    success = false,
                    message = "El servicio de IA no está disponible o el modelo configurado es incorrecto. Revisa los logs."
                });
            }
            string textResponse = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}";
            textResponse = textResponse.Replace("```json", "", StringComparison.OrdinalIgnoreCase).Replace("```", "").Trim();
            string usuarioSugerido = "";
            string justificacion = "Sin justificación disponible";
            try
            {
                using JsonDocument doc = JsonDocument.Parse(textResponse);
                usuarioSugerido = (doc.RootElement.TryGetProperty("usuarioSugerido", out var u) ? (u.GetString() ?? "") : "");
                justificacion = (doc.RootElement.TryGetProperty("justificacion", out var j) ? (j.GetString() ?? justificacion) : justificacion);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Gemini devolvió JSON inválido: {raw} — Error: {err}", textResponse, ex.Message);
                return Ok(new
                {
                    success = false,
                    message = "La IA devolvió una respuesta en formato inesperado"
                });
            }
            if (!usuarios.Any((dynamic val) => (string)val.usuario == usuarioSugerido))
            {
                _logger.LogWarning("Gemini sugirió usuario inválido: {u}", usuarioSugerido);
                return Ok(new
                {
                    success = false,
                    message = "La IA no pudo identificar un revisor válido"
                });
            }
            return Ok(new
            {
                success = true,
                usuarioSugerido = usuarioSugerido,
                justificacion = justificacion
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en SugerirRevisor {cref}", crefAgrupada);
            return StatusCode(500, new
            {
                success = false,
                message = "Error interno al sugerir revisor"
            });
        }
    }
}
