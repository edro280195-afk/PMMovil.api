using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace PMMovil.Api.Hubs
{
    /// <summary>
    /// Modelo para rastrear la presencia detallada de revisores en memoria (Paridad con DesbloqueoHub.cs)
    /// </summary>
    public class PresenceState
    {
        public string Usuario { get; set; } = string.Empty;
        public string Entry { get; set; } = string.Empty;
        public string Invoice { get; set; } = string.Empty;
        public string Item { get; set; } = string.Empty;
        public DateTime LastSeen { get; set; }
    }

    /// <summary>
    /// Hub para comunicación en tiempo real entre la App de Bodega y el Monitor de Supervisores.
    /// (Paridad 1:1 con DesbloqueoHub.cs)
    /// </summary>
    public class WarehouseHub : Hub
    {
        private readonly PMMovil.Api.Data.BaseRepository _db;
        
        // Registro en memoria para rastrear presencia granular (Snapshot para supervisores)
        private static readonly ConcurrentDictionary<string, PresenceState> _registry = new();
        private static readonly ConcurrentDictionary<string, string> _connections = new();

        public WarehouseHub(PMMovil.Api.Data.BaseRepository db)
        {
            _db = db;
        }

        /// <summary>
        /// Obtiene la instantánea de presencia activa para sincronizar monitores recién conectados.
        /// </summary>
        public List<PresenceState> GetPresenciaActiva()
        {
            var exp = DateTime.Now.AddMinutes(-5);
            var keysToRemove = _registry.Where(x => x.Value.LastSeen < exp).Select(x => x.Key).ToList();
            foreach (var k in keysToRemove) _registry.TryRemove(k, out _);

            return _registry.Values.OrderBy(x => x.Usuario).ToList();
        }

        /// <summary>
        /// Notifica que un revisor ha tomado actividad detallada sobre una referencia/factura (R13).
        /// PARIDAD: RegistrarActividad(string cref, string usuario, string cnofact = "", string itemId = "")
        /// </summary>
        public async Task RegistrarActividad(string creferencia, string usuario, string cnofact = "", string item = "")
        {
            if (string.IsNullOrEmpty(usuario)) return;

            if (!string.IsNullOrEmpty(creferencia))
            {
                _registry[usuario] = new PresenceState
                {
                    Usuario = usuario,
                    Entry   = creferencia,
                    Invoice = cnofact,
                    Item    = item,
                    LastSeen = DateTime.Now
                };
            }
            else
            {
                _registry.TryRemove(usuario, out _);
            }

            // PARIDAD: onActividadRecibida(ref, user, factura, partida)
            await Clients.All.SendAsync("onActividadRecibida", creferencia, usuario, cnofact, item);
        }

        /// <summary>
        /// Une al usuario a su propio grupo privado para recibir notificaciones directas.
        /// </summary>
        public async Task UnirseASesion(string usuario)
        {
            if (!string.IsNullOrEmpty(usuario))
            {
                _connections[Context.ConnectionId] = usuario;
                await Groups.AddToGroupAsync(Context.ConnectionId, $"USER_{usuario}");
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (_connections.TryRemove(Context.ConnectionId, out var usuario))
            {
                _registry.TryRemove(usuario, out _);

                try {
                    using var conn = _db.CreateConnection();
                    string sql = @"
                        UPDATE traftiemposrevision
                        SET ffinrevision  = NOW(),
                            estatus_final = 0
                        WHERE usuario = @usuario
                          AND finiciorevision IS NOT NULL AND YEAR(finiciorevision) >= 2000
                          AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000)";
                    
                    await Dapper.SqlMapper.ExecuteAsync(conn, sql, new { usuario });
                } catch { }

                // Notificar que el usuario se ha desconectado
                await Clients.All.SendAsync("onUsuarioDesconectado", usuario);
                
                // PARIDAD:ref vacía = limpieza en monitor
                await Clients.All.SendAsync("onActividadRecibida", "", usuario, "", "");
            }
            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// PARIDAD: NuevaSolicitud(string cnofact, string usuario, string motivo, int idDesbloqueo)
        /// </summary>
        public async Task NuevaSolicitud(string cnofact, string usuario, string motivo, int idDesbloqueo)
        {
            await Clients.All.SendAsync("onNuevaSolicitud", cnofact, usuario, motivo, idDesbloqueo);
        }

        /// <summary>
        /// PARIDAD: FacturaAutorizada(string cnofact, string supervisor)
        /// Notifica a TODOS que una factura/entrada ha sido autorizada.
        /// </summary>
        public async Task FacturaAutorizada(string cnofact, string supervisor)
        {
            await Clients.All.SendAsync("onFacturaAutorizada", cnofact, supervisor);
        }

        /// <summary>
        /// PARIDAD: FacturaRechazada(string usuarioSolicita, string cnofact, string supervisor)
        /// Notifica solo al usuario que solicitó.
        /// </summary>
        public async Task FacturaRechazada(string usuarioSolicita, string cnofact, string supervisor)
        {
            await Clients.Group($"USER_{usuarioSolicita}").SendAsync("onFacturaRechazada", cnofact, supervisor);
        }

        /// <summary>
        /// R13 / Master Plan: Notifica al revisor que un supervisor lo está observando ("The Eye").
        /// </summary>
        public async Task NotificarObservacion(string creferencia, string supervisor, string revisorUsuario)
        {
            await Clients.Group($"USER_{revisorUsuario}").SendAsync("onSupervisorObservando", creferencia, supervisor);
        }

        /// <summary>
        /// Master Plan: Fuerza el cierre de sesión de un operario ("Kill Switch").
        /// </summary>
        public async Task ForzarCierreSesion(string usuario)
        {
            await Clients.Group($"USER_{usuario}").SendAsync("onCierreForzoso", "Tu sesión ha sido finalizada por un administrador.");
        }
    }
}
