using System;
using System.Data;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
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
public class TimerController : ControllerBase
{
    private readonly BaseRepository _db;

    private readonly IHubContext<WarehouseHub> _hub;

    private readonly IRevisionReportService _reportService;
    private readonly IConfiguration _config;
    private readonly ILogger<TimerController> _logger;
    private readonly RevisionActivityService _activityService;
    private readonly RevisionNormalCompletionService _normalCompletionService;
    private readonly RevisionService _revisionService;
    private readonly MonitorTvRealtimePublisher _monitorTvRealtimePublisher;

    public TimerController(
        BaseRepository db,
        IHubContext<WarehouseHub> hub,
        IRevisionReportService reportService,
        IConfiguration config,
        ILogger<TimerController> logger,
        RevisionActivityService activityService,
        RevisionNormalCompletionService normalCompletionService,
        RevisionService revisionService,
        MonitorTvRealtimePublisher monitorTvRealtimePublisher)
    {
        _db = db;
        _hub = hub;
        _reportService = reportService;
        _config = config;
        _logger = logger;
        _activityService = activityService;
        _normalCompletionService = normalCompletionService;
        _revisionService = revisionService;
        _monitorTvRealtimePublisher = monitorTvRealtimePublisher;
    }

    [HttpGet("estado/{creferencia}")]
    public async Task<IActionResult> GetEstadoTimer(string creferencia)
    {
        _ = 1;
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            string sqlSesion = "SELECT DATE_FORMAT(finiciorevision, '%Y/%m/%d %H:%i:%s') AS InicioSesion, usuario AS Usuario FROM traftiemposrevision WHERE creferencia = @cref AND finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000 AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000) ORDER BY idtiemposrevision DESC LIMIT 1";
            dynamic sesion = await conn.QueryFirstOrDefaultAsync<object>(sqlSesion, new
            {
                cref = creferencia
            });
            string sqlAcum = $@"SELECT COALESCE(SUM(TIMESTAMPDIFF(SECOND, finiciorevision, ffinrevision)), 0)
                FROM traftiemposrevision
                WHERE creferencia = @cref
                  AND {RevisionTimeRules.ClosedSessionPredicate()}";
            long acumulados = await conn.ExecuteScalarAsync<long>(sqlAcum, new
            {
                cref = creferencia
            });
            string sqlFinished = "SELECT COUNT(*) FROM fact WHERE cref_agrupada = @cref AND (status_rev IS NULL OR status_rev != 1)";
            int pendientes = await conn.ExecuteScalarAsync<int>(sqlFinished, new { cref = creferencia });

            string sqlTotal = "SELECT COUNT(*) FROM fact WHERE cref_agrupada = @cref";
            int total = await conn.ExecuteScalarAsync<int>(sqlTotal, new { cref = creferencia });
            // OJO: debe mirar solo la sesión MAS RECIENTE, no "alguna vez existió
            // una cerrada" -- si no, una entrada finalizada una vez queda
            // marcada como finalizada para siempre, incluso tras reabrirla con
            // una fila nueva (ffinrevision NULL). Mismo patrón MAX(id) que ya
            // usa RevisionService para no romper esa lectura.
            string sqlClosedSession = @"
                SELECT COUNT(*) FROM (
                    SELECT ffinrevision, estatus_final
                    FROM traftiemposrevision
                    WHERE creferencia = @cref
                    ORDER BY idtiemposrevision DESC
                    LIMIT 1
                ) ultima
                WHERE ultima.ffinrevision IS NOT NULL AND ultima.estatus_final IN (1, 2)";
            bool sesionFinalizada = await conn.ExecuteScalarAsync<int>(sqlClosedSession, new { cref = creferencia }) > 0;

            bool finRevision = (total > 0 && pendientes == 0) || sesionFinalizada;

            bool activo = sesion != null && sesion.InicioSesion != null;
            string? inicio = (activo ? ((string)sesion.InicioSesion) : null);
            string? usuarioAsignado = (activo ? ((string)sesion.Usuario) : null);

            return Ok(new
            {
                success = true,
                activo = activo && !finRevision,
                inicioSesionActual = inicio,
                segundosAcumulados = acumulados,
                usuarioAsignado = usuarioAsignado,
                finRevision = finRevision
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetEstadoTimer para {cref}", creferencia);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al obtener estado del timer"
            });
        }
    }

    [HttpPost("iniciar/{creferencia}")]
    public async Task<IActionResult> IniciarTimer(string creferencia)
    {
        _ = 7;
        try
        {
            // Una referencia vacía insertaría una fila en blanco en traftiemposrevision (sus columnas tienen DEFAULT
            // '' / '0000-00-00') que las consultas de pendientes cuentan como una sesión abierta.
            if (string.IsNullOrWhiteSpace(creferencia))
            {
                return BadRequest(new { success = false, message = "Falta la referencia de la entrada." });
            }

            string usuario = base.User.Identity?.Name ?? "Desconocido";
            using IDbConnection conn = _db.CreateConnection();

            string sqlFinished = "SELECT COUNT(*) FROM fact WHERE cref_agrupada = @cref AND (status_rev IS NULL OR status_rev != 1)";
            int pendientes = await conn.ExecuteScalarAsync<int>(sqlFinished, new { cref = creferencia });
            string sqlTotal = "SELECT COUNT(*) FROM fact WHERE cref_agrupada = @cref";
            int total = await conn.ExecuteScalarAsync<int>(sqlTotal, new { cref = creferencia });

            // OJO: debe mirar solo la sesión MAS RECIENTE, no "alguna vez existió
            // una cerrada" -- si no, una entrada finalizada una vez queda
            // marcada como finalizada para siempre, incluso tras reabrirla con
            // una fila nueva (ffinrevision NULL). Mismo patrón MAX(id) que ya
            // usa RevisionService para no romper esa lectura.
            string sqlClosedSession = @"
                SELECT COUNT(*) FROM (
                    SELECT ffinrevision, estatus_final
                    FROM traftiemposrevision
                    WHERE creferencia = @cref
                    ORDER BY idtiemposrevision DESC
                    LIMIT 1
                ) ultima
                WHERE ultima.ffinrevision IS NOT NULL AND ultima.estatus_final IN (1, 2)";
            bool sesionFinalizada = await conn.ExecuteScalarAsync<int>(sqlClosedSession, new { cref = creferencia }) > 0;

            if ((total > 0 && pendientes == 0) || sesionFinalizada)
            {
                return BadRequest(new { success = false, message = "La revisión ya está finalizada y no puede iniciarse." });
            }

            conn.Open();
            using IDbTransaction transaction = conn.BeginTransaction();
            _ = 7;
            try
            {
                // Reutiliza también una fila que solo tenga toma de documento.
                // La lista y el detalle pueden registrar la toma casi al mismo
                // tiempo; ambas operaciones deben converger en una sola sesión.
                string sqlBuscar = @"
                    SELECT idtiemposrevision, COALESCE(usuario, '') AS usuario
                    FROM traftiemposrevision
                    WHERE creferencia = @cref
                      AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                      AND (
                            (finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000)
                         OR (ftomadocumento IS NOT NULL AND YEAR(ftomadocumento) >= 2000)
                      )
                    ORDER BY CASE
                               WHEN finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000
                               THEN finiciorevision
                               ELSE ftomadocumento
                             END DESC,
                             idtiemposrevision DESC
                    LIMIT 1
                    FOR UPDATE";
                dynamic abierta = await conn.QueryFirstOrDefaultAsync<object>(sqlBuscar, new
                {
                    cref = creferencia
                }, transaction);
                if (abierta != null)
                {
                    string usuarioAbierta = (string)abierta.usuario;
                    int idAbierta = (int)abierta.idtiemposrevision;
                    if (usuarioAbierta == usuario || string.IsNullOrEmpty(usuarioAbierta))
                    {
                        string sqlReutilizar = "UPDATE traftiemposrevision SET finiciorevision = IF(finiciorevision IS NULL OR YEAR(finiciorevision) < 2000, NOW(), finiciorevision),ftomadocumento  = IF(ftomadocumento  IS NULL OR YEAR(ftomadocumento)  < 2000, NOW(), ftomadocumento),usuario = @usuario WHERE idtiemposrevision = @id";
                        await conn.ExecuteAsync(sqlReutilizar, new
                        {
                            usuario = usuario,
                            id = idAbierta
                        }, transaction);
                    }
                    else
                    {
                        // Nunca cerrar ni sustituir silenciosamente la sesión
                        // de otra persona. El cliente recibe 409 y conserva el
                        // modo espectador hasta que el dueño libere la entrada.
                        throw new RevisionEnUsoException(usuarioAbierta);
                    }
                }
                else
                {
                    string sqlNueva = "INSERT INTO traftiemposrevision (creferencia, finiciorevision, ftomadocumento, usuario, estatus_final) VALUES (@cref, NOW(), NOW(), @usuario, 0)";
                    await conn.ExecuteAsync(sqlNueva, new
                    {
                        cref = creferencia,
                        usuario = usuario
                    }, transaction);
                }
                transaction.Commit();
                string sqlInicio = @"
                    SELECT DATE_FORMAT(finiciorevision, '%Y/%m/%d %H:%i:%s')
                    FROM traftiemposrevision
                    WHERE creferencia = @cref
                      AND finiciorevision IS NOT NULL
                      AND YEAR(finiciorevision) >= 2000
                      AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                    ORDER BY idtiemposrevision DESC
                    LIMIT 1";
                string? fechaInicio = await conn.ExecuteScalarAsync<string>(sqlInicio, new
                {
                    cref = creferencia
                });
                string sqlAcum = $@"SELECT COALESCE(SUM(TIMESTAMPDIFF(SECOND, finiciorevision, ffinrevision)), 0)
                    FROM traftiemposrevision
                    WHERE creferencia = @cref
                      AND {RevisionTimeRules.ClosedSessionPredicate()}";
                long acumulados = await conn.ExecuteScalarAsync<long>(sqlAcum, new
                {
                    cref = creferencia
                });
                await _activityService.RegistrarAsync(
                    creferencia,
                    usuario,
                    RevisionActivityTypes.InicioRevision,
                    detalle: "Revisión iniciada");
                await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync("onRevisionEstadoCambiado", new
                {
                    creferencia,
                    usuario,
                    estado = "EN_REVISION",
                    fecha = DateTime.Now.ToString("o"),
                    estatusFinal = 0
                });
                await _monitorTvRealtimePublisher.RevisionEstadoCambiadoAsync(
                    creferencia, usuario, "EN_REVISION", estatusFinal: 0);
                await _hub.Clients.All.SendAsync("onActividadRecibida", creferencia, usuario, "", "");
                return Ok(new
                {
                    success = true,
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
        catch (RevisionEnUsoException exception)
        {
            return Conflict(new
            {
                success = false,
                message = exception.Message
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en IniciarTimer para {cref}", creferencia);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al iniciar cronómetro"
            });
        }
    }

    [HttpPost("finalizar")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> FinalizarTimer([FromBody] FinalizarTimerRequest req)
    {
        _ = 3;
        try
        {
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            string creferencia = req.Creferencia ?? "";
            if (string.IsNullOrWhiteSpace(creferencia))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "creferencia es requerida."
                });
            }
            using IDbConnection conn = _db.CreateConnection();
            conn.Open();
            string sqlCheck = "SELECT idtiemposrevision, finiciorevision, ftomadocumento FROM traftiemposrevision WHERE creferencia = @cref AND usuario = @usuario AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000) AND ((finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000) OR (ftomadocumento IS NOT NULL AND YEAR(ftomadocumento) >= 2000)) ORDER BY idtiemposrevision DESC LIMIT 1";
            dynamic? sesionAbierta = await conn.QueryFirstOrDefaultAsync<object>(sqlCheck, new
            {
                cref = creferencia,
                usuario = usuario
            });
            bool haySesionAbierta = sesionAbierta != null;
            if (!haySesionAbierta && !req.EsFinal)
            {
                // Pausar sin sesión abierta: la desconexión ya la cerró, no hay
                // nada más que hacer.
                return Ok(new
                {
                    success = true
                });
            }

            // Terminar SIN sesión abierta (caso 01C263614350000, 10-nov): el hub
            // cierra las sesiones de un usuario cuando se le cae la conexión
            // (WarehouseHub.OnDisconnectedAsync, estatus_final = 0). Si después
            // el revisor toca "Terminar", aquí ya no hay fila abierta. Antes se
            // respondía success=true sin hacer nada -- la app celebraba, pero
            // traf.status se quedaba en 1, traf3 sin tiempos y sin correo.
            // Ahora el cierre se completa igual, tomando como sesiones del
            // usuario las que ya quedaron cerradas sin finalizar (estatus_final
            // distinto de 1 y 2). Los tiempos reales NO se tocan: ffinrevision
            // conserva la hora de la desconexión.
            RevisionNormalCompletionState completionState =
                await _normalCompletionService.ObtenerEstadoAsync(conn, creferencia);

            DateTime? inicioRevision = null;
            if (sesionAbierta != null)
            {
                inicioRevision = sesionAbierta.finiciorevision as DateTime?;
            }
            if (inicioRevision.HasValue && inicioRevision.Value > DateTime.Now)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "La fecha de inicio no es válida (es futura)."
                });
            }
            // Cierra todas las filas abiertas del usuario en esta entrada,
            // no solo la última. Así los duplicados creados por versiones
            // anteriores no vuelven a aparecer como sesiones fantasma.
            string sqlCerrar = haySesionAbierta
                ? @"
                UPDATE traftiemposrevision
                SET ffinrevision = NOW(), estatus_final = @status, usurevisa = @usuario
                WHERE creferencia = @cref
                  AND usuario = @usuario
                  AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                  AND (
                        (finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000)
                     OR (ftomadocumento IS NOT NULL AND YEAR(ftomadocumento) >= 2000)
                  )"
                : @"
                UPDATE traftiemposrevision
                SET estatus_final = @status, usurevisa = @usuario
                WHERE creferencia = @cref
                  AND usuario = @usuario
                  AND ffinrevision IS NOT NULL AND YEAR(ffinrevision) >= 2000
                  AND COALESCE(estatus_final, 0) NOT IN (1, 2)
                  AND (
                        (finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000)
                     OR (ftomadocumento IS NOT NULL AND YEAR(ftomadocumento) >= 2000)
                  )";
            int statusFinal = req.EsFinal
                ? completionState.EstatusFinal
                : req.StatusFinal;

            using IDbTransaction transaction = conn.BeginTransaction();
            try
            {
                // Candado sobre la entrada: dos "Terminar" a la vez (doble toque, dos dispositivos) se serializan, y el
                // segundo ya ve la entrada marcada, así que no vuelve a mandar el correo.
                bool entradaYaMarcada = req.EsFinal
                    && await _normalCompletionService.ObtenerStatusEntradaAsync(conn, creferencia, transaction) >= 2;

                int rowsCerradas = await conn.ExecuteAsync(sqlCerrar, new
                {
                    cref = creferencia,
                    status = statusFinal,
                    usuario
                }, transaction);

                if (rowsCerradas > 0 && !haySesionAbierta)
                {
                    _logger.LogWarning(
                        "FinalizarTimer {Cref}: {Usuario} terminó una entrada cuya sesión ya estaba cerrada (desconexión); se completó el cierre sobre {Filas} fila(s) ya cerradas.",
                        creferencia,
                        usuario,
                        rowsCerradas);
                }

                // El cierre ante el sistema de escritorio (traf.status = 2, traf3.finicia/ffin_revision) depende del
                // ESTADO de la entrada, no de que esta llamada haya cambiado una fila de sesión: si la entrada está
                // completa y no está marcada, se marca aunque las sesiones ya estuvieran cerradas (caso: un cierre
                // anterior que se hizo con la entrada "incompleta" y luego se completó, o una sesión que la
                // desconexión ya había cerrado).
                bool converge = req.EsFinal
                    && completionState.PuedeEnviarCorreo
                    && rowsCerradas == 0
                    && !entradaYaMarcada;

                if (req.EsFinal && (rowsCerradas > 0 || converge))
                {
                    // Se acabó de trabajar en esta entrada (bien o con
                    // pendientes) -- ya no debe seguir apareciendo en
                    // "Asignaciones activas" del Monitor. Antes nada
                    // marcaba esto, así que la tarjeta se quedaba pegada
                    // ahí para siempre aunque la revisión ya hubiera
                    // terminado.
                    await conn.ExecuteAsync(
                        "UPDATE asignaciones_revision SET estatus = 2, fecha_completada = NOW() WHERE cref_agrupada = @cref AND usuario_asignado = @usuario AND estatus IN (0, 1)",
                        new { cref = creferencia, usuario },
                        transaction);
                }

                if (converge)
                {
                    // La última sesión del usuario quedó con el estatus de aquel cierre (0 o 2); se alinea con el real.
                    await conn.ExecuteAsync(@"
                        UPDATE traftiemposrevision
                        SET estatus_final = @status, usurevisa = @usuario
                        WHERE idtiemposrevision = (
                            SELECT ultimo FROM (
                                SELECT MAX(idtiemposrevision) AS ultimo
                                FROM traftiemposrevision
                                WHERE creferencia = @cref
                                  AND usuario = @usuario
                                  AND ffinrevision IS NOT NULL AND YEAR(ffinrevision) >= 2000
                            ) u)
                          AND COALESCE(estatus_final, 0) <> @status",
                        new { cref = creferencia, status = statusFinal, usuario },
                        transaction);
                }

                if (req.EsFinal && completionState.PuedeEnviarCorreo)
                {
                    LegacyRevisionSyncResult legacySync =
                        await _normalCompletionService.SincronizarSistemaLegadoAsync(
                            conn,
                            creferencia,
                            transaction);
                    _logger.LogInformation(
                        "Cierre normal {Cref} sincronizado con legado: traf={TrafRows}, traf3={Traf3Rows}, convergencia={Converge}.",
                        creferencia,
                        legacySync.TrafRows,
                        legacySync.Traf3Rows,
                        converge);
                }
                else if (req.EsFinal)
                {
                    _logger.LogWarning(
                        "FinalizarTimer {Cref}: {Usuario} terminó pero la entrada NO quedó marcada como revisada: {Motivo}",
                        creferencia,
                        usuario,
                        completionState.MotivoIncompleto);
                }

                transaction.Commit();

                if (rowsCerradas > 0 || converge)
                {
                    if (req.EsFinal)
                    {
                        // La revisión vuelve a quedar cerrada: cualquier permiso de
                        // desbloqueo que siguiera vigente de una autorización previa
                        // debe caducar aquí. Si no, sigue "abierto" en el Monitor y
                        // el cliente lo sigue tratando como aprobado (ver
                        // DetalleRevisionViewModel.isBloqueada).
                        await _revisionService.RevocarDesbloqueosDeEntradaAsync(creferencia);
                    }

                    long? activityEventId = await _activityService.RegistrarAsync(
                        creferencia,
                        usuario,
                        req.EsFinal ? RevisionActivityTypes.Finalizada : RevisionActivityTypes.Pausada,
                        detalle: !req.EsFinal
                            ? "Revisión pausada"
                            : converge
                                ? "Revisión finalizada (se completó el cierre de una revisión que ya estaba cerrada)"
                                : haySesionAbierta
                                    ? "Revisión finalizada"
                                    : "Revisión finalizada (la sesión ya se había cerrado por desconexión)");
                    await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync("onRevisionEstadoCambiado", new
                    {
                        creferencia,
                        usuario,
                        estado = req.EsFinal ? "FINALIZADA" : "PAUSADA",
                        fecha = DateTime.Now.ToString("o"),
                        estatusFinal = statusFinal
                    });
                    await _monitorTvRealtimePublisher.RevisionEstadoCambiadoAsync(
                        creferencia,
                        usuario,
                        req.EsFinal ? "FINALIZADA" : "PAUSADA",
                        statusFinal,
                        eventId: activityEventId);
                    if (req.EsFinal && completionState.PuedeEnviarCorreo)
                    {
                        await _hub.Clients.Group(WarehouseHub.RevisionGroup).SendAsync(
                            "onRevisionCompletada",
                            new WarehouseHub.RevisionCompletadaEvent
                            {
                                Creferencia = creferencia,
                                Usuario = usuario
                            });
                        await _monitorTvRealtimePublisher.RevisionCompletadaAsync(creferencia, usuario, eventId: activityEventId);
                    }
                    if (req.EsFinal && completionState.PuedeEnviarCorreo)
                    {
                        // También se manda con discrepancia; el reporte muestra
                        // ese estado con su propio banner. Se espera la tarea para
                        // no usar servicios scoped después de que termine la petición.
                        string baseUrl = $"{Request.Scheme}://{Request.Host}";
                        _ = await _reportService.EnviarReporteRevisionNormalAsync(creferencia, baseUrl);
                    }
                    await _hub.Clients.All.SendAsync("onActividadRecibida", "", usuario, "", "");
                }
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            return Ok(new
            {
                success = true,
                estatusFinal = req.EsFinal ? completionState.EstatusFinal : req.StatusFinal,
                revisionCompleta = req.EsFinal && completionState.PuedeEnviarCorreo,
                // La sesión se cierra siempre, pero la entrada solo queda marcada como revisada físicamente cuando está
                // completa. La app ya no puede tratar "se cerró" y "quedó revisada" como lo mismo: estos campos le
                // dicen cuál de las dos pasó y por qué (antes respondía solo success=true y la app celebraba).
                facturasIncompletas = completionState.FacturasIncompletas,
                facturasConPartidasPendientes = completionState.FacturasConPartidasIncompletas,
                message = req.EsFinal ? completionState.MotivoIncompleto : null
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en FinalizarTimer para {cref}", req?.Creferencia);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al finalizar cronómetro"
            });
        }
    }
}
