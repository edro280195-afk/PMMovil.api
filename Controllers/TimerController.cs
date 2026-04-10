using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Dapper;
using PMMovil.Api.Data;
using System.Data;
using Microsoft.AspNetCore.SignalR;


namespace PMMovil.Api.Controllers
{
    // DTO para el body de FinalizarTimer
    public class FinalizarTimerRequest
    {
        public string Creferencia { get; set; } = string.Empty;
        /// <summary>0 = pausa/abandono, 1 = completo, 2 = incompleto</summary>
        public int StatusFinal { get; set; } = 1;
        /// <summary>
        /// true  → el revisor terminó la revisión de forma definitiva (cierra el timer permanentemente).
        /// false → el revisor salió de la página pero la revisión puede continuar (pausa).
        /// </summary>
        public bool EsFinal { get; set; } = true;
    }

    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class TimerController : ControllerBase
    {
        private readonly BaseRepository _db;
        private readonly IHubContext<PMMovil.Api.Hubs.WarehouseHub> _hub;
        private readonly ILogger<TimerController> _logger;

        public TimerController(BaseRepository db, IHubContext<PMMovil.Api.Hubs.WarehouseHub> hub, ILogger<TimerController> logger)
        {
            _db = db;
            _hub = hub;
            _logger = logger;
        }


        /// <summary>
        /// Consulta el estado actual del cronómetro sin iniciar sesión.
        /// Útil para que la App sepa si ya hay tiempo acumulado antes de que el revisor presione Iniciar.
        /// </summary>
        [HttpGet("estado/{creferencia}")]
        public async Task<IActionResult> GetEstadoTimer(string creferencia)
        {
            try
            {
                using var conn = _db.CreateConnection();

                // ── Sesión abierta (de cualquier usuario) ─────────────────────────────────
                string sqlSesion = @"
                    SELECT DATE_FORMAT(finiciorevision, '%Y/%m/%d %H:%i:%s') AS InicioSesion,
                           usuario AS Usuario
                    FROM traftiemposrevision
                    WHERE creferencia = @cref
                      AND finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000
                      AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                    ORDER BY idtiemposrevision DESC LIMIT 1";

                var sesion = await conn.QueryFirstOrDefaultAsync<dynamic>(sqlSesion, new { cref = creferencia });

                // ── Tiempo acumulado de sesiones CERRADAS ─────────────────────────────────
                string sqlAcum = @"
                    SELECT COALESCE(SUM(TIMESTAMPDIFF(SECOND, finiciorevision, ffinrevision)), 0)
                    FROM traftiemposrevision
                    WHERE creferencia = @cref
                      AND finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000
                      AND ffinrevision   IS NOT NULL AND YEAR(ffinrevision)   >= 2000";

                long acumulados = await conn.ExecuteScalarAsync<long>(sqlAcum, new { cref = creferencia });

                bool activo = sesion != null && sesion.InicioSesion != null;
                string? inicio = activo ? (string?)sesion.InicioSesion : null;
                string? usuarioAsignado = activo ? (string?)sesion.Usuario : null;

                return Ok(new { 
                    success = true, 
                    activo, 
                    inicioSesionActual = inicio, 
                    segundosAcumulados = acumulados,
                    usuarioAsignado 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en GetEstadoTimer para {cref}", creferencia);
                return StatusCode(500, new { success = false, message = "Error al obtener estado del timer" });
            }
        }


        /// <summary>
        /// Inicia o reanuda el cronómetro de revisión para una referencia.
        ///
        /// Lógica de sesiones (el núcleo del seguimiento de tiempos):
        ///   1. Si existe una sesión abierta del MISMO usuario → se reutiliza (reconexión).
        ///   2. Si existe una sesión abierta de OTRO usuario   → se cierra y se crea una nueva.
        ///   3. Si no existe ninguna sesión abierta            → se crea una nueva.
        ///
        /// Siempre retorna el acumulado de sesiones cerradas + el inicio de la sesión actual,
        /// para que la App pueda mostrar el tiempo total correcto desde el primer momento.
        /// </summary>
        [HttpPost("iniciar/{creferencia}")]
        public async Task<IActionResult> IniciarTimer(string creferencia)
        {
            try
            {
                string usuario = User.Identity?.Name ?? "Desconocido";
                using var conn = _db.CreateConnection();
                conn.Open();

                using var transaction = conn.BeginTransaction();
                try
                {
                    // ── PASO 1: Buscar sesión abierta (bloqueante para evitar race conditions) ──
                    string sqlBuscar = @"
                        SELECT idtiemposrevision, COALESCE(usuario, '') AS usuario
                        FROM traftiemposrevision
                        WHERE creferencia = @cref
                          AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                        ORDER BY idtiemposrevision DESC LIMIT 1 FOR UPDATE";

                    var abierta = await conn.QueryFirstOrDefaultAsync<dynamic>(sqlBuscar,
                        new { cref = creferencia }, transaction);

                    if (abierta != null)
                    {
                        string usuarioAbierta = (string)abierta.usuario;
                        int idAbierta = (int)abierta.idtiemposrevision;

                        if (usuarioAbierta == usuario || string.IsNullOrEmpty(usuarioAbierta))
                        {
                            // ── CASO A: Misma persona o sesión sin dueño → reutilizar ─────────
                            string sqlReutilizar = @"
                                UPDATE traftiemposrevision
                                SET finiciorevision = IF(finiciorevision IS NULL OR YEAR(finiciorevision) < 2000, NOW(), finiciorevision),
                                    ftomadocumento  = IF(ftomadocumento  IS NULL OR YEAR(ftomadocumento)  < 2000, NOW(), ftomadocumento),
                                    usuario         = @usuario
                                WHERE idtiemposrevision = @id";
                            await conn.ExecuteAsync(sqlReutilizar,
                                new { usuario, id = idAbierta }, transaction);
                        }
                        else
                        {
                            // ── CASO B: Otro usuario tenía la sesión → cerrarla y crear nueva ──
                            string sqlCerrar = @"
                                UPDATE traftiemposrevision
                                SET ffinrevision = NOW(), estatus_final = 0
                                WHERE idtiemposrevision = @id";
                            await conn.ExecuteAsync(sqlCerrar, new { id = idAbierta }, transaction);

                            string sqlInsertar = @"
                                INSERT INTO traftiemposrevision (creferencia, finiciorevision, ftomadocumento, usuario, estatus_final)
                                VALUES (@cref, NOW(), NOW(), @usuario, 0)";
                            await conn.ExecuteAsync(sqlInsertar,
                                new { cref = creferencia, usuario }, transaction);
                        }
                    }
                    else
                    {
                        // ── CASO C: Sin sesión abierta → crear nueva ──────────────────────────
                        string sqlNueva = @"
                            INSERT INTO traftiemposrevision
                                (creferencia, finiciorevision, ftomadocumento, usuario, estatus_final)
                            VALUES (@cref, NOW(), NOW(), @usuario, 0)";
                        await conn.ExecuteAsync(sqlNueva,
                            new { cref = creferencia, usuario }, transaction);
                    }

                    transaction.Commit();

                    // ── PASO 2: Leer los valores que quedaron guardados ───────────────────────
                    string sqlInicio = @"
                        SELECT DATE_FORMAT(finiciorevision, '%Y/%m/%d %H:%i:%s')
                        FROM traftiemposrevision
                        WHERE creferencia = @cref
                          AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                        ORDER BY idtiemposrevision DESC LIMIT 1";
                    var fechaInicio = await conn.ExecuteScalarAsync<string>(sqlInicio, new { cref = creferencia });

                    string sqlAcum = @"
                        SELECT COALESCE(SUM(TIMESTAMPDIFF(SECOND, finiciorevision, ffinrevision)), 0)
                        FROM traftiemposrevision
                        WHERE creferencia = @cref
                          AND finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000
                          AND ffinrevision   IS NOT NULL AND YEAR(ffinrevision)   >= 2000";
                    long acumulados = await conn.ExecuteScalarAsync<long>(sqlAcum, new { cref = creferencia });

                    // ── PASO 3: Notificar al monitor que el revisor está activo ───────────────
                    // PARIDAD: Se envían 4 argumentos separados
                    await _hub.Clients.All.SendAsync("onActividadRecibida", creferencia, usuario, "", "");

                    return Ok(new
                    {
                        success          = true,
                        inicioSesionActual = fechaInicio,
                        segundosAcumulados = acumulados
                    });
                }
                catch (Exception)
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en IniciarTimer para {cref}", creferencia);
                return StatusCode(500, new { success = false, message = "Error al iniciar cronómetro" });
            }
        }


        /// <summary>
        /// Cierra la sesión de revisión del usuario actual.
        ///
        ///   EsFinal = true  → la revisión terminó (definitivo). Emite onRevisionCompletada.
        ///   EsFinal = false → el revisor salió de la página (pausa). Emite onActividadRecibida vacío.
        ///
        /// Si no hay sesión abierta para el usuario, retorna success=true de forma idempotente.
        /// </summary>
        [HttpPost("finalizar")]
        public async Task<IActionResult> FinalizarTimer([FromBody] FinalizarTimerRequest req)
        {
            try
            {
                string usuario = User.Identity?.Name ?? "Desconocido";
                string creferencia = req.Creferencia ?? "";

                if (string.IsNullOrWhiteSpace(creferencia))
                    return BadRequest(new { success = false, message = "creferencia es requerida." });

                using var conn = _db.CreateConnection();
                conn.Open();

                // ── Validación: comprobar que hay un inicio válido ─────────────────────────
                string sqlCheck = @"
                    SELECT finiciorevision
                    FROM traftiemposrevision
                    WHERE creferencia = @cref
                      AND usuario      = @usuario
                      AND finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000
                      AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                    ORDER BY idtiemposrevision DESC LIMIT 1";

                var inicioRevision = await conn.ExecuteScalarAsync<DateTime?>(sqlCheck,
                    new { cref = creferencia, usuario });

                if (!inicioRevision.HasValue)
                {
                    // Idempotente: no hay sesión abierta para este usuario → OK sin hacer nada
                    return Ok(new { success = true });
                }

                if (inicioRevision.Value > DateTime.Now)
                    return BadRequest(new { success = false, message = "La fecha de inicio no es válida (es futura)." });

                // ── Cerrar SOLO la sesión del usuario actual ──────────────────────────────
                string sqlCerrar = @"
                    UPDATE traftiemposrevision
                    SET ffinrevision  = NOW(),
                        estatus_final = @status,
                        usurevisa     = @usuario
                    WHERE creferencia = @cref
                      AND usuario      = @usuario
                      AND finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000
                      AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                    ORDER BY idtiemposrevision DESC LIMIT 1";

                int rows = await conn.ExecuteAsync(sqlCerrar,
                    new { cref = creferencia, usuario, status = req.StatusFinal });

                if (rows > 0)
                {
                    if (req.EsFinal && req.StatusFinal == 1)
                    {
                        // Revisión completada → evento especial para el monitor (Paridad opcional, se envían argumentos separados)
                        await _hub.Clients.All.SendAsync("onRevisionCompletada", creferencia, usuario);
                    }
                    // Siempre notificar que el revisor salió (creferencia vacía = salió)
                    // PARIDAD: 4 argumentos separados
                    await _hub.Clients.All.SendAsync("onActividadRecibida", "", usuario, "", "");
                }

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en FinalizarTimer para {cref}", req?.Creferencia);
                return StatusCode(500, new { success = false, message = "Error al finalizar cronómetro" });
            }
        }
    }
}
