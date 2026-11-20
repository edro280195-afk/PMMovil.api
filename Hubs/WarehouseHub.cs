using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using PMMovil.Api.Data;
using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Hubs;

[Authorize]
public class WarehouseHub : Hub
{
    public const string SupervisoresBloqueoGroup = "SUPERVISORES_BLOQUEO";
    public const string RevisionGroup = "REVISION_OPERADORES";
    public const string PreregistroOperationsGroup = "PREREGISTRO_OPERACIONES";
    public const string EntriesGroup = "ENTRADAS_PANEL";

    public static class RevisionEstados
    {
        public const string EnRevision = "EN_REVISION";
        public const string DocumentosTomados = "DOCUMENTOS_TOMADOS";
        public const string Pausada = "PAUSADA";
        public const string Finalizada = "FINALIZADA";
    }

    public sealed class RevisionEstadoEvent
    {
        public string Creferencia { get; init; } = string.Empty;
        public string Usuario { get; init; } = string.Empty;
        public string Estado { get; init; } = string.Empty;
        public string Fecha { get; init; } = string.Empty;
        public int EstatusFinal { get; init; }
    }

    public sealed class RevisionCompletadaEvent
    {
        public string Creferencia { get; init; } = string.Empty;
        public string Usuario { get; init; } = string.Empty;
    }

    /// <summary>
    /// Snapshot del dato ya persistido. Se manda por una sola forma para que
    /// Angular y Android apliquen el mismo cambio sin una segunda consulta.
    /// </summary>
    public sealed class RevisionActualizadaEvent
    {
        public string Entidad { get; init; } = string.Empty;
        public string Creferencia { get; init; } = string.Empty;
        public string Usuario { get; init; } = string.Empty;
        public string Fecha { get; init; } = string.Empty;
        public string? Cnofact { get; init; }
        public IReadOnlyList<string>? Cnofacts { get; init; }
        public int? IdRevdet { get; init; }
        public FacturaRevision? Factura { get; init; }
        public PartidaRevision? Partida { get; init; }
        public string? ObservacionRevision { get; init; }
        public string? OficinaOrigen { get; init; }
    }

    private readonly BaseRepository _db;

    private readonly RevisionActivityService _activityService;
    private readonly IModulePermissionService _permissionService;
    private readonly MonitorTvRealtimePublisher _monitorTvRealtimePublisher;

    private static readonly ConcurrentDictionary<string, PresenceState> _registry = new ConcurrentDictionary<string, PresenceState>();

    private static readonly ConcurrentDictionary<string, string> _connections = new ConcurrentDictionary<string, string>();

    public WarehouseHub(
        BaseRepository db,
        RevisionActivityService activityService,
        IModulePermissionService permissionService,
        MonitorTvRealtimePublisher monitorTvRealtimePublisher)
    {
        _db = db;
        _activityService = activityService;
        _permissionService = permissionService;
        _monitorTvRealtimePublisher = monitorTvRealtimePublisher;
    }

    private static void PurgeStaleEntries()
    {
        DateTime exp = DateTime.Now.AddMinutes(-5.0);
        foreach (string k in (from x in _registry
                              where x.Value.LastSeen < exp
                              select x.Key).ToList())
        {
            _registry.TryRemove(k, out PresenceState _);
        }
    }

    public List<PresenceState> GetPresenciaActiva()
        => GetActivePresenceSnapshot();

    public static List<PresenceState> GetActivePresenceSnapshot()
    {
        PurgeStaleEntries();
        return _registry.Values
            .Select(presence => new PresenceState
            {
                Usuario = presence.Usuario,
                Entry = presence.Entry,
                Invoice = presence.Invoice,
                Item = presence.Item,
                LastSeen = presence.LastSeen,
            })
            .OrderBy(presence => presence.Usuario)
            .ToList();
    }

    public Task RegistrarActividad(string creferencia, string usuario, string cnofact = "", string item = "")
        => RegistrarActividadInterna(creferencia, cnofact, item, preservarFacturaVacia: true);

    public Task RegisterCurrentReviewInvoice(string creferencia, string cnofact = "")
        => RegistrarActividadInterna(creferencia, cnofact, string.Empty, preservarFacturaVacia: false);

    private async Task RegistrarActividadInterna(
        string creferencia,
        string cnofact,
        string item,
        bool preservarFacturaVacia)
    {
        string? usuarioReal = Context.User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(usuarioReal)) return;

        PurgeStaleEntries();
        _registry.TryGetValue(usuarioReal, out PresenceState? previousPresence);

        bool mismaEntrada = previousPresence != null
            && string.Equals(previousPresence.Entry, creferencia, StringComparison.OrdinalIgnoreCase);
        string facturaActual = preservarFacturaVacia
            && string.IsNullOrWhiteSpace(cnofact)
            && mismaEntrada
                ? previousPresence!.Invoice
                : cnofact ?? string.Empty;
        bool entradaCambiada = !mismaEntrada;
        bool facturaCambiada = previousPresence == null
            || !string.Equals(previousPresence.Invoice, facturaActual, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(creferencia))
        {
            _registry[usuarioReal] = new PresenceState
            {
                Usuario = usuarioReal,
                Entry = creferencia,
                Invoice = facturaActual,
                Item = item,
                LastSeen = DateTime.Now
            };
        }
        else
        {
            _registry.TryRemove(usuarioReal, out PresenceState _);
        }

        // Esta llamada es un latido de presencia, no un movimiento de la
        // revisión. Se conserva como registro técnico porque la higiene
        // de sesiones lo usa para detectar actividad, pero la bitácora
        // visible filtra este detalle.
        await _activityService.RegistrarAsync(
            creferencia,
            usuarioReal,
            RevisionActivityTypes.CambioActividad,
            facturaActual,
            detalle: string.IsNullOrWhiteSpace(item) ? "Actividad en revisión" : item);
        await Clients.All.SendAsync("onActividadRecibida", creferencia, usuarioReal, facturaActual, item);

        if (!entradaCambiada && !facturaCambiada) return;

        if (previousPresence != null
            && !string.IsNullOrWhiteSpace(previousPresence.Entry)
            && entradaCambiada)
        {
            await _monitorTvRealtimePublisher.RevisionActualizadaAsync(
                previousPresence.Entry,
                usuarioReal,
                "FACTURA_ACTUAL",
                oficinaOrigen: null,
                cancellationToken: Context.ConnectionAborted);
        }

        if (!string.IsNullOrWhiteSpace(creferencia))
        {
            await _monitorTvRealtimePublisher.RevisionActualizadaAsync(
                creferencia,
                usuarioReal,
                "FACTURA_ACTUAL",
                oficinaOrigen: null,
                cancellationToken: Context.ConnectionAborted);
        }
    }

    public async Task RegistrarPresencia(string creferencia, string usuario)
    {
        string usuarioReal = base.Context.User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(usuarioReal) || string.IsNullOrWhiteSpace(creferencia))
        {
            return;
        }

        PurgeStaleEntries();
        DateTime lastSeen = DateTime.Now;
        PresenceState presencia = _registry.AddOrUpdate(
            usuarioReal,
            _ => new PresenceState
            {
                Usuario = usuarioReal,
                Entry = creferencia,
                LastSeen = lastSeen
            },
            (_, previousPresence) =>
            {
                bool mismaEntrada = string.Equals(
                    previousPresence.Entry,
                    creferencia,
                    StringComparison.OrdinalIgnoreCase);
                return new PresenceState
                {
                    Usuario = usuarioReal,
                    Entry = creferencia,
                    Invoice = mismaEntrada ? previousPresence.Invoice : string.Empty,
                    Item = mismaEntrada ? previousPresence.Item : string.Empty,
                    LastSeen = lastSeen
                };
            });

        await base.Clients.All.SendAsync("onPresenciaActualizada", new
        {
            usuario = usuarioReal,
            entry = creferencia,
            invoice = presencia.Invoice,
            item = presencia.Item,
            lastSeen
        });
    }

    public async Task UnirseASesion(string usuario, bool recibeBloqueos = false)
    {
        string usuarioReal = base.Context.User?.Identity?.Name ?? usuario;
        if (!string.IsNullOrEmpty(usuarioReal))
        {
            _connections[base.Context.ConnectionId] = usuarioReal;
            await base.Groups.AddToGroupAsync(base.Context.ConnectionId, "USER_" + usuarioReal);
            if (recibeBloqueos)
            {
                await base.Groups.AddToGroupAsync(base.Context.ConnectionId, SupervisoresBloqueoGroup);
            }
            else
            {
                await base.Groups.RemoveFromGroupAsync(base.Context.ConnectionId, SupervisoresBloqueoGroup);
            }
        }
    }

    /// <summary>Suscribe la conexión a cambios de revisión si tiene permiso de lectura.</summary>
    public async Task UnirseARevision()
    {
        string? usuario = Context.User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(usuario))
            throw new HubException("No se pudo identificar al usuario.");

        bool permitido = await _permissionService.HasPermissionAsync(
            usuario,
            MobileModuleIds.Review,
            "Lectura",
            Context.ConnectionAborted)
            || await _permissionService.HasPermissionAsync(
                usuario,
                MobileModuleIds.SystemsMonitor,
                "Lectura",
                Context.ConnectionAborted);

        if (!permitido)
            throw new HubException("No tienes permiso para recibir cambios de revisión.");

        await Groups.AddToGroupAsync(Context.ConnectionId, RevisionGroup, Context.ConnectionAborted);
    }

    /// <summary>
    /// Suscribe la conexion a eventos de pre-registro solo si el usuario tiene
    /// permiso de lectura de caseta o del modulo administrativo de
    /// pre-registros. El grupo evita enviar coordenadas y alertas operativas a
    /// cualquier otra conexion autenticada del hub.
    /// </summary>
    public async Task UnirseAPreregistro()
    {
        string? usuario = Context.User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(usuario))
            throw new HubException("No se pudo identificar al usuario.");

        bool permitido = await _permissionService.HasPermissionAsync(
            usuario,
            MobileModuleIds.Gate,
            "Lectura",
            Context.ConnectionAborted)
            || await _permissionService.HasPermissionAsync(
                usuario,
                MobileModuleIds.TransporterPreRegistration,
                "Lectura",
                Context.ConnectionAborted);

        if (!permitido)
            throw new HubException("No tienes permiso para recibir eventos de pre-registro.");

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            PreregistroOperationsGroup,
            Context.ConnectionAborted);
    }

    /// <summary>Suscribe la conexión a las entradas de bodega solo con permiso de lectura del panel de entradas.</summary>
    public async Task UnirseAEntradas()
    {
        string? usuario = Context.User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(usuario))
            throw new HubException("No se pudo identificar al usuario.");

        bool permitido = await _permissionService.HasPermissionAsync(
            usuario,
            MobileModuleIds.EntriesPanel,
            "Lectura",
            Context.ConnectionAborted);

        if (!permitido)
            throw new HubException("No tienes permiso para recibir cambios de entradas.");

        await Groups.AddToGroupAsync(Context.ConnectionId, EntriesGroup, Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_connections.TryRemove(base.Context.ConnectionId, out string usuario))
        {
            // Un mismo usuario puede tener la tableta y el monitor web
            // conectados al mismo tiempo. Perder una de esas conexiones no
            // significa que haya terminado la revisión: solo cerramos sus
            // sesiones cuando ya no queda ninguna conexión SignalR.
            bool quedanConexiones = _connections.Values.Any(
                conectado => string.Equals(conectado, usuario, StringComparison.OrdinalIgnoreCase));

            if (!quedanConexiones)
            {
                _registry.TryRemove(usuario, out PresenceState _);
                try
                {
                    using IDbConnection conn = _db.CreateConnection();
                    string sqlRefs = @"
                        SELECT DISTINCT creferencia
                        FROM traftiemposrevision
                        WHERE usuario = @usuario
                          AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)
                          AND (
                                (finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000)
                                OR (ftomadocumento IS NOT NULL AND YEAR(ftomadocumento) >= 2000)
                              )";
                    List<string> referencias = (await conn.QueryAsync<string>(sqlRefs, new { usuario })).ToList();
                    string sql = "UPDATE traftiemposrevision SET ffinrevision = NOW(), estatus_final = 0 WHERE usuario = @usuario AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000) AND ((finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000) OR (ftomadocumento IS NOT NULL AND YEAR(ftomadocumento) >= 2000))";
                    await conn.ExecuteAsync(sql, new { usuario });
                    foreach (string referencia in referencias)
                    {
                        await _activityService.RegistrarAsync(
                            referencia,
                            usuario,
                            RevisionActivityTypes.Pausada,
                            detalle: "Sesión cerrada por desconexión");
                        await base.Clients.Group(RevisionGroup).SendAsync(
                            "onRevisionEstadoCambiado",
                            new RevisionEstadoEvent
                            {
                                Creferencia = referencia,
                                Usuario = usuario,
                                Estado = RevisionEstados.Pausada,
                                Fecha = DateTime.Now.ToString("o"),
                                EstatusFinal = 0
                            });
                    }
                }
                catch
                {
                }
            }

            if (!quedanConexiones)
            {
                await base.Clients.All.SendAsync("onUsuarioDesconectado", usuario);
                await base.Clients.All.SendAsync("onActividadRecibida", "", usuario, "", "");
            }
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task NuevaSolicitud(string cnofact, string usuario, string motivo, int idDesbloqueo)
    {
        string usuarioReal = base.Context.User?.Identity?.Name ?? usuario;
        await base.Clients.Group(SupervisoresBloqueoGroup).SendAsync("onNuevaSolicitud", cnofact, usuarioReal, motivo, idDesbloqueo);
    }

    public async Task FacturaAutorizada(string cnofact, string supervisor)
    {
        await base.Clients.Group(RevisionGroup).SendAsync(
            "onDesbloqueoAprobado",
            new { cnofact, supervisor });
    }

    public async Task FacturaRechazada(string usuarioSolicita, string cnofact, string supervisor)
    {
        await base.Clients.Group("USER_" + usuarioSolicita).SendAsync(
            "onDesbloqueoRechazado",
            new { cnofact, supervisor });
    }

    public async Task NotificarObservacion(string creferencia, string supervisor, string revisorUsuario)
    {
        // Un solo objeto, no dos argumentos posicionales: Android espera
        // exactamente esta forma (SupervisorObservandoMessage) y con los
        // argumentos sueltos la deserializacion fallaba en silencio -- el
        // revisor nunca veia el aviso de que su supervisor estaba observando.
        await base.Clients.Group("USER_" + revisorUsuario).SendAsync("onSupervisorObservando", new
        {
            creferencia,
            supervisor
        });
    }

    public async Task ForzarCierreSesion(string usuario, string motivo = "")
    {
        string supervisor = base.Context.User?.Identity?.Name ?? "Supervisor";
        string motivoNormalizado = string.IsNullOrWhiteSpace(motivo)
            ? "Cierre solicitado por supervisión."
            : motivo.Trim();
        if (motivoNormalizado.Length > 500)
        {
            motivoNormalizado = motivoNormalizado[..500];
        }

        await base.Clients.All.SendAsync("onCierreForzoso", new
        {
            usuario,
            supervisor,
            motivo = motivoNormalizado,
            mensaje = $"Tu sesión de revisión fue cerrada por {supervisor}."
        });
    }

    public async Task InformarDecisionAsignacion(string crefAgrupada, string usuario, string decision)
    {
        await base.Clients.All.SendAsync("onDecisionAsignacion", new
        {
            crefAgrupada = crefAgrupada,
            usuario = usuario,
            decision = decision,
            ts = DateTime.Now.ToString("o")
        });
    }
}
