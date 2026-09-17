using System.Data;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Dapper;

using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.SignalR;

using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Logica del pre-registro de camiones sin caja: cuentas de linea
/// transportista, alta/consulta de pre-registros, ubicacion+ETA, validacion
/// y uso en garita, administracion de cuentas y auditoria.
///
/// Las tres tablas propias (preregistro_cuentas, caseta_preregistro,
/// preregistro_auditoria) viven en <c>pmmutileria</c> (<see cref="BaseRepository.CreatePmmutileriaConnection"/>),
/// no en <c>pmmtraf</c> -- son tablas nuevas que FoxPro no conoce, igual que
/// fcm_tokens/push_subscriptions. Dos excepciones deliberadas siguen en
/// pmmtraf porque son tablas legacy compartidas con el resto del sistema:
/// <c>consecutivo</c> (folio, ver <see cref="ReservarFolioAsync"/>) y
/// <c>transpor</c> (catálogo de líneas, ver <see cref="ObtenerLineasTransporteAsync"/>).
/// `caseta` (pmmtraf) solo se toca al promover un registro (ver
/// PromoverACasetaAsync en CasetaController), nunca aqui.
/// </summary>
public partial class PreregistroService
{
    private const int MaxIntentosFallidos = 5;
    private static readonly TimeSpan DuracionBloqueo = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan VigenciaQr = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetencionTotal = TimeSpan.FromHours(72);
    private const int MaxVigentesPorCuenta = 20;

    private readonly BaseRepository _db;
    private readonly IConfiguration _configuration;
    private readonly IRouteEtaService _routeEtaService;
    private readonly BodegaDestinoService _bodegaDestinoService;
    private readonly IHubContext<WarehouseHub> _hub;
    private readonly ISmtpService _smtpService;
    private readonly IRealtimeNotificationService _notifications;
    private readonly ILogger<PreregistroService> _logger;

    public PreregistroService(
        BaseRepository db,
        IConfiguration configuration,
        IRouteEtaService routeEtaService,
        BodegaDestinoService bodegaDestinoService,
        IHubContext<WarehouseHub> hub,
        ISmtpService smtpService,
        IRealtimeNotificationService notifications,
        ILogger<PreregistroService> logger)
    {
        _db = db;
        _configuration = configuration;
        _routeEtaService = routeEtaService;
        _bodegaDestinoService = bodegaDestinoService;
        _hub = hub;
        _smtpService = smtpService;
        _notifications = notifications;
        _logger = logger;
    }

    // ── Login del portal ──────────────────────────────────────────────────

    public async Task<PreregistroLoginResponse> LoginAsync(string usuario, string password, string ip, CancellationToken ct = default)
    {
        usuario = usuario.Trim();
        using IDbConnection conn = _db.CreatePmmutileriaConnection();

        var cuenta = await conn.QuerySingleOrDefaultAsync<CuentaRow>(new CommandDefinition(@"
            SELECT id, usuario, password_hash AS PasswordHash, cidtranspor AS CidTranspor,
                   nombre_linea AS NombreLinea, activo, intentos_fallidos AS IntentosFallidos,
                   fbloqueo AS FBloqueo
            FROM preregistro_cuentas
            WHERE usuario = @usuario
            LIMIT 1",
            new { usuario }, cancellationToken: ct));

        if (cuenta is null)
        {
            await RegistrarAuditoriaAsync(conn, PreregistroEvento.LoginFallido, usuario: usuario, ip: ip, detalle: "Usuario no existe", ct: ct);
            return new PreregistroLoginResponse { Success = false, Message = "Usuario o contrasena incorrectos." };
        }

        if (cuenta.FBloqueo.HasValue && cuenta.FBloqueo.Value > DateTime.Now)
        {
            await RegistrarAuditoriaAsync(conn, PreregistroEvento.LoginBloqueado, idCuenta: cuenta.Id, usuario: usuario, ip: ip, ct: ct);
            return new PreregistroLoginResponse { Success = false, Message = "Cuenta bloqueada temporalmente por intentos fallidos." };
        }

        if (!cuenta.Activo)
        {
            await RegistrarAuditoriaAsync(conn, PreregistroEvento.LoginFallido, idCuenta: cuenta.Id, usuario: usuario, ip: ip, detalle: "Cuenta inactiva", ct: ct);
            return new PreregistroLoginResponse { Success = false, Message = "Usuario o contrasena incorrectos." };
        }

        bool passwordOk;
        try
        {
            passwordOk = BCrypt.Net.BCrypt.Verify(password, cuenta.PasswordHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verificando password de la cuenta {Usuario}", usuario);
            passwordOk = false;
        }

        if (!passwordOk)
        {
            int intentos = cuenta.IntentosFallidos + 1;
            DateTime? bloqueo = intentos >= MaxIntentosFallidos ? DateTime.Now.Add(DuracionBloqueo) : null;
            await conn.ExecuteAsync(new CommandDefinition(@"
                UPDATE preregistro_cuentas
                SET intentos_fallidos = @intentos, fbloqueo = @bloqueo
                WHERE id = @id",
                new { intentos, bloqueo, id = cuenta.Id }, cancellationToken: ct));

            await RegistrarAuditoriaAsync(conn, PreregistroEvento.LoginFallido, idCuenta: cuenta.Id, usuario: usuario, ip: ip,
                detalle: bloqueo.HasValue ? "Bloqueada por intentos fallidos" : $"Intento {intentos}/{MaxIntentosFallidos}", ct: ct);

            return new PreregistroLoginResponse
            {
                Success = false,
                Message = bloqueo.HasValue
                    ? "Cuenta bloqueada por intentos fallidos. Intenta de nuevo mas tarde."
                    : "Usuario o contrasena incorrectos."
            };
        }

        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE preregistro_cuentas
            SET intentos_fallidos = 0, fbloqueo = NULL, fultimo_acceso = @ahora
            WHERE id = @id",
            new { ahora = DateTime.Now, id = cuenta.Id }, cancellationToken: ct));

        await RegistrarAuditoriaAsync(conn, PreregistroEvento.LoginOk, idCuenta: cuenta.Id, usuario: usuario, ip: ip, ct: ct);

        var (token, expira) = GenerarJwtPortal(cuenta);
        return new PreregistroLoginResponse
        {
            Success = true,
            Message = "Autenticacion exitosa.",
            Token = token,
            NombreLinea = cuenta.NombreLinea,
            ExpiraUtc = expira
        };
    }

    // ── Crear pre-registro ───────────────────────────────────────────────

    /// <summary>
    /// Reserva (e incrementa) el siguiente folio 'CASETAPR' de <c>consecutivo</c>
    /// -- tabla legacy compartida en <c>pmmtraf</c>, la única razón por la que
    /// <see cref="CrearAsync"/> no puede hacer todo en una sola transaccion de
    /// pmmutileria. Transaccion propia y corta; null si no se pudo generar.
    /// </summary>
    private async Task<string?> ReservarFolioAsync(CancellationToken ct)
    {
        using IDbConnection conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            string? folio = await conn.ExecuteScalarAsync<string>(new CommandDefinition(@"
                SELECT CONCAT(prefijo,'-0',numero,'/',RIGHT(YEAR(CURDATE()), 2))
                FROM consecutivo
                WHERE formulario = 'CASETAPR'
                FOR UPDATE",
                transaction: tx, cancellationToken: ct));

            if (string.IsNullOrWhiteSpace(folio))
            {
                // Si aún no está sembrado el registro en consecutivo, se auto-inicializa
                await conn.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO consecutivo (formulario, prefijo, numero)
                    SELECT 'CASETAPR', 'PR', 1
                    WHERE NOT EXISTS (SELECT 1 FROM consecutivo WHERE formulario = 'CASETAPR')",
                    transaction: tx, cancellationToken: ct));

                folio = await conn.ExecuteScalarAsync<string>(new CommandDefinition(@"
                    SELECT CONCAT(prefijo,'-0',numero,'/',RIGHT(YEAR(CURDATE()), 2))
                    FROM consecutivo
                    WHERE formulario = 'CASETAPR'
                    FOR UPDATE",
                    transaction: tx, cancellationToken: ct));
            }

            if (string.IsNullOrWhiteSpace(folio))
            {
                tx.Rollback();
                return null;
            }

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE consecutivo SET numero = numero + 1 WHERE formulario = 'CASETAPR'",
                transaction: tx, cancellationToken: ct));

            tx.Commit();
            return folio;
        }
        catch
        {
            try { tx.Rollback(); } catch { /* ya se habia hecho commit/rollback */ }
            throw;
        }
    }

    public async Task<PreregistroCrearResponse> CrearAsync(int idCuenta, string cidTranspor, string nombreLinea, PreregistroCrearRequest req, string ip, CancellationToken ct = default)
    {
        string nunidad = req.Nunidad.Trim().ToUpperInvariant();
        string placas = req.Placas.Trim().ToUpperInvariant();
        string operador = req.Operador.Trim();

        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            int vigentes = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM caseta_preregistro WHERE id_cuenta = @idCuenta AND estatus = @vigente",
                new { idCuenta, vigente = PreregistroEstatus.Vigente }, tx, cancellationToken: ct));

            if (vigentes >= MaxVigentesPorCuenta)
            {
                tx.Rollback();
                return new PreregistroCrearResponse { Success = false, Message = $"Ya tienes {MaxVigentesPorCuenta} pre-registros vigentes. Cancela alguno antes de crear otro." };
            }

            // Un camion, un QR vivo: cancela cualquier VIGENTE con las mismas placas o numero de unidad.
            await conn.ExecuteAsync(new CommandDefinition(@"
                UPDATE caseta_preregistro
                SET estatus = @cancelado
                WHERE estatus = @vigente AND (placas = @placas OR nunidad = @nunidad)",
                new { cancelado = PreregistroEstatus.Cancelado, vigente = PreregistroEstatus.Vigente, placas, nunidad },
                tx, cancellationToken: ct));

            // El folio sale de `consecutivo` -- tabla legacy compartida con el
            // resto del sistema (FoxPro incluido), vive en pmmtraf, no en
            // pmmutileria donde vive esta transaccion. Se reserva en su propia
            // conexion/transaccion corta e independiente (ver ReservarFolioAsync);
            // si algo falla despues de reservarlo, el numero se pierde (hueco),
            // igual que ya le pasa a cualquier otro formulario que usa este
            // mismo consecutivo.
            string? folio = await ReservarFolioAsync(ct);
            if (string.IsNullOrWhiteSpace(folio))
            {
                tx.Rollback();
                return new PreregistroCrearResponse { Success = false, Message = "No se pudo generar el folio. Contacta a sistemas." };
            }

            string token = GenerarToken();
            DateTime ahora = DateTime.Now;
            DateTime fexpira = ahora.Add(VigenciaQr);
            DateTime fpurga = ahora.Add(RetencionTotal);

            string? idBodega = string.IsNullOrWhiteSpace(req.IdBodega) ? null : req.IdBodega.Trim();
            PreregistroBodegaItem? destino = idBodega is null
                ? null
                : await _bodegaDestinoService.ObtenerAsync(idBodega, ct);

            if (idBodega is not null && destino is null)
            {
                tx.Rollback();
                return new PreregistroCrearResponse
                {
                    Success = false,
                    Message = "La bodega seleccionada no está configurada. Contacta a sistemas."
                };
            }

            RouteEtaResult? eta = await CalcularEtaSiHayUbicacionAsync(req.Lat, req.Lon, idBodega, ct);

            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO caseta_preregistro
                    (folio, token, id_cuenta, nunidad, idtransporu, transporu, placas, operador,
                     idbodega, destino_nombre, destino_direccion, destino_lat, destino_lon,
                     destino_geocerca_lat, destino_geocerca_lon, destino_geocerca_metros,
                     destino_google_place_id, destino_referencia_entrada,
                     lat, lon, fubicacion, eta_minutos, eta_km, feta,
                     estatus, fcrea, fexpira, fpurga, ip_origen)
                VALUES
                    (@folio, @token, @idCuenta, @nunidad, @cidTranspor, @nombreLinea, @placas, @operador,
                     @idBodega, @destinoNombre, @destinoDireccion, @destinoLat, @destinoLon,
                     @destinoGeocercaLat, @destinoGeocercaLon, @destinoGeocercaMetros,
                     @destinoGooglePlaceId, @destinoReferenciaEntrada,
                     @lat, @lon, @fubicacion, @etaMinutos, @etaKm, @feta,
                     @vigente, @ahora, @fexpira, @fpurga, @ip)",
                new
                {
                    folio,
                    token,
                    idCuenta,
                    nunidad,
                    cidTranspor,
                    nombreLinea,
                    placas,
                    operador,
                    idBodega,
                    destinoNombre = destino?.Nombre,
                    destinoDireccion = destino?.Direccion,
                    destinoLat = destino?.Lat,
                    destinoLon = destino?.Lon,
                    destinoGeocercaLat = destino?.GeocercaLat,
                    destinoGeocercaLon = destino?.GeocercaLon,
                    destinoGeocercaMetros = destino?.GeocercaMetros,
                    destinoGooglePlaceId = destino?.GooglePlaceId,
                    destinoReferenciaEntrada = destino?.ReferenciaEntrada,
                    lat = req.Lat,
                    lon = req.Lon,
                    fubicacion = req.Lat.HasValue && req.Lon.HasValue ? ahora : (DateTime?)null,
                    etaMinutos = eta?.Minutos,
                    etaKm = eta?.Km,
                    feta = eta is not null ? ahora : (DateTime?)null,
                    vigente = PreregistroEstatus.Vigente,
                    ahora,
                    fexpira,
                    fpurga,
                    ip
                }, tx, cancellationToken: ct));

            await RegistrarAuditoriaAsync(conn, PreregistroEvento.CreaPrereg, idCuenta: idCuenta, folio: folio, ip: ip, detalle: $"nunidad={nunidad} placas={placas}", tx: tx, ct: ct);

            tx.Commit();

            await NotificarCreacionAsync(new PreregistroCreadoNotification
            {
                Folio = folio,
                Nunidad = nunidad,
                Placas = placas,
                Operador = operador,
                NombreLinea = nombreLinea,
                IdBodega = req.IdBodega ?? string.Empty,
                FechaCrea = ahora,
                FechaExpira = fexpira,
                EtaMinutos = eta?.Minutos,
                EtaKm = eta?.Km
            }, CancellationToken.None);

            return new PreregistroCrearResponse
            {
                Success = true,
                Message = "Pre-registro creado.",
                Folio = folio,
                QrPayload = $"PMMPR|{folio}|{token}",
                FechaExpira = fexpira,
                EtaMinutos = eta?.Minutos,
                EtaKm = eta?.Km,
                IdBodega = destino?.IdBodega,
                NombreBodega = destino?.Nombre,
                DireccionBodega = destino?.Direccion,
                ReferenciaEntrada = destino?.ReferenciaEntrada,
                LatBodega = destino?.Lat,
                LonBodega = destino?.Lon,
                GooglePlaceId = destino?.GooglePlaceId
            };
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { /* ya se habia hecho commit/rollback */ }
            _logger.LogError(ex, "Error creando pre-registro para cuenta {IdCuenta}", idCuenta);
            return new PreregistroCrearResponse { Success = false, Message = "Error inesperado al crear el pre-registro." };
        }
    }

    public async Task<List<PreregistroItem>> MisRegistrosAsync(int idCuenta, CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        var rows = await conn.QueryAsync<PreregistroItem>(new CommandDefinition(@"
            SELECT folio AS Folio, nunidad AS Nunidad, placas AS Placas, operador AS Operador,
                   transporu AS NombreLinea,
                   idbodega AS IdBodega, destino_nombre AS NombreBodega,
                   destino_direccion AS DireccionBodega,
                   destino_referencia_entrada AS ReferenciaEntrada,
                   destino_lat AS LatBodega, destino_lon AS LonBodega,
                   destino_google_place_id AS GooglePlaceId,
                   CASE WHEN estatus = @vigente AND fexpira < NOW() THEN @expirado ELSE estatus END AS Estatus,
                   fcrea AS FechaCrea, fexpira AS FechaExpira,
                   eta_minutos AS EtaMinutos, eta_km AS EtaKm, fubicacion AS FechaUbicacion,
                   casetaid AS CasetaId, fuso AS FechaUso,
                   CASE WHEN estatus = @vigente AND fexpira >= NOW()
                        THEN CONCAT('PMMPR|', folio, '|', token)
                        ELSE NULL END AS QrPayload
            FROM caseta_preregistro
            WHERE id_cuenta = @idCuenta
              AND fpurga >= NOW()
            ORDER BY fcrea DESC
            LIMIT 50",
            new { idCuenta, vigente = PreregistroEstatus.Vigente, expirado = PreregistroEstatus.Expirado },
            cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<List<PreregistroAdminItem>> ListarRegistrosAdminAsync(string? filtro = null, CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        string termino = filtro?.Trim() ?? string.Empty;
        var rows = await conn.QueryAsync<PreregistroAdminItem>(new CommandDefinition(@"
            SELECT folio AS Folio, nunidad AS Nunidad, placas AS Placas, operador AS Operador,
                   transporu AS NombreLinea, idbodega AS IdBodega,
                   CASE WHEN estatus = @vigente AND fexpira < NOW() THEN @expirado ELSE estatus END AS Estatus,
                   fcrea AS FechaCrea, fexpira AS FechaExpira, eta_minutos AS EtaMinutos, eta_km AS EtaKm,
                   fubicacion AS FechaUbicacion, fuso AS FechaUso, casetaid AS CasetaId, usuario_uso AS UsuarioUso
            FROM caseta_preregistro
            WHERE fpurga >= NOW()
              AND (@termino = '' OR folio LIKE CONCAT('%', @termino, '%')
                   OR nunidad LIKE CONCAT('%', @termino, '%')
                   OR placas LIKE CONCAT('%', @termino, '%')
                   OR operador LIKE CONCAT('%', @termino, '%')
                   OR transporu LIKE CONCAT('%', @termino, '%'))
            ORDER BY fcrea DESC
            LIMIT 200",
            new { termino, vigente = PreregistroEstatus.Vigente, expirado = PreregistroEstatus.Expirado },
            cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<bool> CancelarAsync(int idCuenta, string folio, CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        int rows = await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE caseta_preregistro
            SET estatus = @cancelado
            WHERE folio = @folio AND id_cuenta = @idCuenta AND estatus = @vigente",
            new { cancelado = PreregistroEstatus.Cancelado, folio, idCuenta, vigente = PreregistroEstatus.Vigente },
            cancellationToken: ct));
        return rows > 0;
    }

    // ── Ubicacion + ETA ───────────────────────────────────────────────────

    /// <summary>
    /// Radio de la geocerca de la planta, en metros. Configurable en
    /// <c>GoogleRoutes:GeocercaMetros</c>; 800 m por defecto -- suficiente para
    /// cubrir la fila afuera del porton sin dispararse con el trafico de la
    /// avenida.
    /// </summary>
    private int GeocercaMetros =>
        _configuration.GetValue<int?>("GoogleRoutes:GeocercaMetros") ?? 800;

    /// <summary>El mismo radio, para que el mapa dibuje exactamente la geocerca
    /// con la que el API decide el arribo -- si difieren, el circulo miente.</summary>
    public int GeocercaMetrosPublico => GeocercaMetros;

    /// <summary>
    /// Registra una senal de ubicacion del chofer.
    ///
    /// Hace DOS escrituras a proposito:
    ///  1) INSERT en caseta_preregistro_ubicacion -- la miga, que se conserva.
    ///  2) UPDATE de lat/lon/eta en caseta_preregistro -- cache de "ultima
    ///     posicion" para que Caseta/preregistro/en-camino (que ya consume el
    ///     Android) siga contestando con una sola consulta.
    ///
    /// Antes solo existia el UPDATE, asi que cada senal borraba la anterior y
    /// no habia recorrido que reconstruir. Ver la migracion
    /// 20260907_caseta_preregistro_ubicacion.sql.
    /// </summary>
    public async Task<PreregistroUbicacionResponse> ActualizarUbicacionAsync(
        int idCuenta, string folio, PreregistroUbicacionRequest req, CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();

        var row = await conn.QuerySingleOrDefaultAsync<EstadoUbicacionRow>(new CommandDefinition(
            @"SELECT idbodega AS IdBodega, estatus AS Estatus, fpurga AS FPurga,
                      destino_lat AS DestinoLat, destino_lon AS DestinoLon,
                      destino_geocerca_lat AS DestinoGeocercaLat,
                      destino_geocerca_lon AS DestinoGeocercaLon,
                      destino_geocerca_metros AS DestinoGeocercaMetros
              FROM caseta_preregistro
              WHERE folio = @folio AND id_cuenta = @idCuenta LIMIT 1",
            new { folio, idCuenta }, cancellationToken: ct));

        if (row is null)
            return new PreregistroUbicacionResponse { Success = false, Message = "Pre-registro no encontrado." };

        if (row.Estatus != PreregistroEstatus.Vigente)
            return new PreregistroUbicacionResponse { Success = false, Message = "Este pre-registro ya no esta vigente." };

        double lat = req.Lat;
        double lon = req.Lon;

        RouteEtaResult? eta = row.DestinoLat.HasValue && row.DestinoLon.HasValue
            ? await _routeEtaService.ComputeEtaAsync(lat, lon, row.DestinoLat.Value, row.DestinoLon.Value, ct)
            : await CalcularEtaSiHayUbicacionAsync(lat, lon, row.IdBodega, ct);
        DateTime ahora = DateTime.Now;

        // La fecha del dispositivo manda, salvo que venga en el futuro o
        // absurdamente vieja: un reloj mal puesto no debe ensuciar el recorrido.
        DateTime fechaSenal = req.Fecha is DateTime f && f <= ahora.AddMinutes(5) && f > ahora.AddDays(-2)
            ? f
            : ahora;

        // Distancia en linea recta: se calcula aqui y no con Google porque se
        // necesita en CADA senal y una llamada de red por punto seria absurda.
        double? distanciaKm = null;
        bool dentro = false;
        var bodegaActual = row.DestinoGeocercaLat.HasValue && row.DestinoGeocercaLon.HasValue
            ? null
            : await _bodegaDestinoService.ObtenerAsync(row.IdBodega, ct);
        double? destinoGeocercaLat = row.DestinoGeocercaLat ?? bodegaActual?.GeocercaLat;
        double? destinoGeocercaLon = row.DestinoGeocercaLon ?? bodegaActual?.GeocercaLon;
        int destinoGeocercaMetros = row.DestinoGeocercaMetros
            ?? bodegaActual?.GeocercaMetros
            ?? GeocercaMetros;

        if (destinoGeocercaLat.HasValue && destinoGeocercaLon.HasValue)
        {
            distanciaKm = DistanciaHaversineKm(lat, lon, destinoGeocercaLat.Value, destinoGeocercaLon.Value);
            dentro = distanciaKm * 1000 <= destinoGeocercaMetros;
        }

        // Arribo = la PRIMERA senal dentro de la geocerca. Si ya habia una
        // anterior dentro, esta ya no es novedad y no vuelve a avisar.
        bool arriboDetectado = false;
        if (dentro)
        {
            int previasDentro = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM caseta_preregistro_ubicacion WHERE folio = @folio AND dentro_geocerca = 1",
                new { folio }, cancellationToken: ct));
            arriboDetectado = previasDentro == 0;
        }

        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO caseta_preregistro_ubicacion
                (folio, lat, lon, fecha, precision_m, velocidad, origen,
                 eta_minutos, eta_km, distancia_km, dentro_geocerca, fpurga)
            VALUES
                (@folio, @lat, @lon, @fecha, @precision, @velocidad, @origen,
                 @etaMinutos, @etaKm, @distanciaKm, @dentro, @fpurga)",
            new
            {
                folio,
                lat,
                lon,
                fecha = fechaSenal,
                precision = req.PrecisionM,
                velocidad = req.Velocidad,
                origen = string.IsNullOrWhiteSpace(req.Origen) ? "PORTAL" : req.Origen.Trim().ToUpperInvariant(),
                etaMinutos = eta?.Minutos,
                etaKm = eta?.Km,
                distanciaKm,
                dentro = dentro ? 1 : 0,
                fpurga = row.FPurga
            }, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE caseta_preregistro
            SET lat = @lat, lon = @lon, fubicacion = @fecha,
                eta_minutos = @etaMinutos, eta_km = @etaKm, feta = @feta
            WHERE folio = @folio AND id_cuenta = @idCuenta",
            new
            {
                lat,
                lon,
                fecha = fechaSenal,
                etaMinutos = eta?.Minutos,
                etaKm = eta?.Km,
                feta = eta is not null ? ahora : (DateTime?)null,
                folio,
                idCuenta
            }, cancellationToken: ct));

        if (arriboDetectado)
        {
            await RegistrarAuditoriaAsync(conn, PreregistroEvento.ArriboGeocerca, folio: folio,
                detalle: $"a {distanciaKm * 1000:F0} m de la planta {row.IdBodega}", ct: ct);
            await NotificarArriboAsync(folio, row.IdBodega ?? string.Empty, ct);
            await NotificarArriboPushAsync(folio, row.IdBodega ?? string.Empty);
        }

        return new PreregistroUbicacionResponse
        {
            Success = true,
            Message = eta is null ? "Ubicacion guardada. No se pudo calcular el ETA." : "Ubicacion y ETA actualizados.",
            EtaMinutos = eta?.Minutos,
            EtaKm = eta?.Km,
            DistanciaKm = distanciaKm,
            DentroGeocerca = dentro,
            ArriboDetectado = arriboDetectado
        };
    }

    /// <summary>
    /// Push opcional a los usuarios de garita configurados. SignalR sigue siendo
    /// el canal instantáneo de la pantalla; el push cubre el teléfono bloqueado
    /// o la pestaña que estaba en segundo plano.
    /// </summary>
    private async Task NotificarArriboPushAsync(string folio, string idBodega)
    {
        var usuarios = (_configuration["Garita:UsuariosNotificacion"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(usuario => usuario.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var usuario in usuarios)
        {
            try
            {
                await _notifications.SendToUserOrInboxAsync(usuario, "Camión en puerta", $"El pre-registro {folio} llegó a la geocerca de la planta {idBodega}.", new Dictionary<string, string> { ["type"] = "preregistro-arribo", ["folio"] = folio, ["idBodega"] = idBodega });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo enviar push de arribo para {Folio} a {Usuario}.", folio, usuario);
            }
        }
    }

    /// <summary>Recorrido completo de un folio, de la senal mas reciente hacia atras.</summary>
    public async Task<List<PreregistroUbicacionItem>> ObtenerRecorridoAsync(
        string folio, int limite = 500, CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        var rows = await conn.QueryAsync<PreregistroUbicacionItem>(new CommandDefinition(@"
            SELECT fecha AS Fecha, lat AS Lat, lon AS Lon, precision_m AS PrecisionM,
                   velocidad AS Velocidad, origen AS Origen, eta_minutos AS EtaMinutos,
                   distancia_km AS DistanciaKm, dentro_geocerca AS DentroGeocerca
            FROM caseta_preregistro_ubicacion
            WHERE folio = @folio AND fpurga >= NOW()
            ORDER BY fecha DESC, id DESC
            LIMIT @limite",
            new { folio, limite = Math.Clamp(limite, 1, 2000) }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>
    /// Distancia en linea recta entre dos coordenadas, en kilometros. Se usa
    /// para la geocerca y para el "faltan X km" -- nunca para el ETA, que
    /// necesita ruta real y trafico (eso lo da Google Routes).
    /// </summary>
    private static double DistanciaHaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double radioTierraKm = 6371.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return radioTierraKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Avisa a la garita por SignalR que un pre-registro llego al porton.</summary>
    private async Task NotificarArriboAsync(string folio, string idBodega, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync("onPreregistroArribo", new
            {
                Type = "preregistro-arribo",
                Folio = folio,
                IdBodega = idBodega,
                Fecha = DateTime.Now.ToString("s")
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo notificar el arribo del pre-registro {Folio}.", folio);
        }
    }

    private async Task<RouteEtaResult?> CalcularEtaSiHayUbicacionAsync(double? lat, double? lon, string? idBodega, CancellationToken ct)
    {
        if (!lat.HasValue || !lon.HasValue || string.IsNullOrWhiteSpace(idBodega))
            return null;

        var destino = await _routeEtaService.ObtenerCoordenadasBodegaAsync(idBodega, ct);
        if (destino is null)
        {
            _logger.LogInformation("Sin coordenadas configuradas para la bodega {IdBodega}; se omite el ETA.", idBodega);
            return null;
        }

        return await _routeEtaService.ComputeEtaAsync(lat.Value, lon.Value, destino.Value.Lat, destino.Value.Lon, ct);
    }

    // ── Garita ───────────────────────────────────────────────────────────

    public async Task<PreregistroValidarResponse> ValidarAsync(string codigo, CancellationToken ct = default)
    {
        var partes = (codigo ?? string.Empty).Split('|');
        if (partes.Length != 3 || partes[0] != "PMMPR")
        {
            return new PreregistroValidarResponse { Success = false, Estatus = PreregistroEstatus.NoEncontrado, Message = "Codigo invalido." };
        }

        string folio = partes[1].Trim();
        string token = partes[2].Trim();

        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        var row = await conn.QuerySingleOrDefaultAsync<PreregistroRow>(new CommandDefinition(@"
            SELECT folio AS Folio, token AS Token, nunidad AS Nunidad, placas AS Placas, operador AS Operador,
                   idtransporu AS IdTransporu, transporu AS NombreLinea, idbodega AS IdBodega,
                   estatus AS Estatus, fexpira AS FechaExpira, fuso AS FechaUso, usuario_uso AS UsuarioUso,
                   casetaid AS CasetaId
            FROM caseta_preregistro
            WHERE folio = @folio
            LIMIT 1",
            new { folio }, cancellationToken: ct));

        if (row is null || row.Token != token)
        {
            await RegistrarAuditoriaAsync(conn, PreregistroEvento.ValidaQr, folio: folio, detalle: "Codigo/token no coincide", ct: ct);
            return new PreregistroValidarResponse { Success = false, Estatus = PreregistroEstatus.NoEncontrado, Message = "No existe este pre-registro.", Folio = folio };
        }

        string estatusEfectivo = row.Estatus;
        if (estatusEfectivo == PreregistroEstatus.Vigente && row.FechaExpira < DateTime.Now)
        {
            estatusEfectivo = PreregistroEstatus.Expirado;
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE caseta_preregistro SET estatus = @expirado WHERE folio = @folio AND estatus = @vigente",
                new { expirado = PreregistroEstatus.Expirado, folio, vigente = PreregistroEstatus.Vigente }, cancellationToken: ct));
        }

        await RegistrarAuditoriaAsync(conn, PreregistroEvento.ValidaQr, folio: folio, detalle: $"estatus={estatusEfectivo}", ct: ct);

        return new PreregistroValidarResponse
        {
            Success = estatusEfectivo == PreregistroEstatus.Vigente,
            Estatus = estatusEfectivo,
            Message = MensajeEstatus(estatusEfectivo),
            Folio = row.Folio,
            Nunidad = row.Nunidad,
            Placas = row.Placas,
            Operador = row.Operador,
            IdTransporu = row.IdTransporu ?? string.Empty,
            NombreLinea = row.NombreLinea ?? string.Empty,
            IdBodega = row.IdBodega ?? string.Empty,
            FechaExpira = row.FechaExpira,
            FechaUso = row.FechaUso,
            UsuarioUso = row.UsuarioUso,
            CasetaId = row.CasetaId
        };
    }

    /// <summary>
    /// Marca el pre-registro como USADO. Se llama despues de guardar la entrada
    /// en `caseta` (nunca antes) para que un escaneo accidental no queme el
    /// codigo del chofer.
    /// </summary>
    public async Task<PreregistroUsarResponse> UsarAsync(string folio, PreregistroUsarRequest req, string usuarioGuardia, CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();

        int rows = await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE caseta_preregistro
            SET estatus = @usado, fuso = @ahora, usuario_uso = @usuario, casetaid = @casetaId
            WHERE folio = @folio AND estatus = @vigente",
            new
            {
                usado = PreregistroEstatus.Usado,
                ahora = DateTime.Now,
                usuario = usuarioGuardia,
                casetaId = req.CasetaId,
                folio,
                vigente = PreregistroEstatus.Vigente
            }, cancellationToken: ct));

        if (rows == 0)
            return new PreregistroUsarResponse { Success = false, Message = "El pre-registro ya no estaba vigente." };

        await RegistrarAuditoriaAsync(conn, PreregistroEvento.UsaQr, usuario: usuarioGuardia, folio: folio, detalle: $"casetaid={req.CasetaId}", ct: ct);

        if (req.PlacasNoCoinciden)
        {
            await RegistrarAuditoriaAsync(conn, PreregistroEvento.PlacasNoCoinciden, usuario: usuarioGuardia, folio: folio,
                detalle: "Guardia reporto discrepancia; entrada registrada de todas formas por instruccion operativa.", ct: ct);
        }

        return new PreregistroUsarResponse { Success = true, Message = "Pre-registro marcado como usado." };
    }

    public async Task<List<PreregistroEnCaminoItem>> EnCaminoAsync(string? idBodega, CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        var rows = await conn.QueryAsync<PreregistroEnCaminoItem>(new CommandDefinition(@"
            SELECT folio AS Folio, nunidad AS Nunidad, placas AS Placas, transporu AS NombreLinea,
                   operador AS Operador, idbodega AS IdBodega, eta_minutos AS EtaMinutos, eta_km AS EtaKm, fubicacion AS FechaUbicacion,
                   lat AS Lat, lon AS Lon,
                   (SELECT MIN(u.fecha) FROM caseta_preregistro_ubicacion u
                    WHERE u.folio = caseta_preregistro.folio AND u.dentro_geocerca = 1 AND u.fpurga >= NOW()) AS FechaArriboGeocerca,
                   COALESCE((SELECT u.dentro_geocerca FROM caseta_preregistro_ubicacion u
                    WHERE u.folio = caseta_preregistro.folio AND u.fpurga >= NOW()
                    ORDER BY u.fecha DESC, u.id DESC LIMIT 1), 0) AS DentroGeocerca,
                   (SELECT u.eta_km FROM caseta_preregistro_ubicacion u
                    WHERE u.folio = caseta_preregistro.folio AND u.eta_km > 0 AND u.fpurga >= NOW()
                    ORDER BY u.fecha, u.id LIMIT 1) AS DistanciaInicialKm
            FROM caseta_preregistro
            WHERE estatus = @vigente AND fexpira >= NOW()
              AND (@idBodega IS NULL OR @idBodega = '' OR idbodega = @idBodega)
            ORDER BY (eta_minutos IS NULL), eta_minutos ASC",
            new { vigente = PreregistroEstatus.Vigente, idBodega }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>
    /// Coordenadas configuradas para una bodega, para que el mapa de "Camiones
    /// en camino" pueda centrarse y pintar el destino.
    /// </summary>
    public Task<(double Lat, double Lon)?> ObtenerCoordenadasBodegaAsync(
        string idBodega,
        CancellationToken ct = default) =>
        _routeEtaService.ObtenerCoordenadasBodegaAsync(idBodega, ct);

    public Task<List<PreregistroBodegaItem>> ListarBodegasAsync(
        bool soloActivas = true,
        CancellationToken ct = default) =>
        _bodegaDestinoService.ListarAsync(soloActivas, ct);

    public async Task<PreregistroBodegaItem> GuardarBodegaAsync(
        PreregistroBodegaGuardarRequest request,
        string usuario,
        CancellationToken ct = default)
    {
        var bodega = await _bodegaDestinoService.GuardarAsync(request, usuario, ct);
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        await RegistrarAuditoriaAsync(
            conn,
            "ACTUALIZA_BODEGA",
            usuario: usuario,
            detalle: $"id={bodega.IdBodega}; lat={bodega.Lat.ToString(CultureInfo.InvariantCulture)}; lon={bodega.Lon.ToString(CultureInfo.InvariantCulture)}",
            ct: ct);
        return bodega;
    }

    // ── Administracion de cuentas ─────────────────────────────────────────

    /// <summary>
    /// Catálogo de líneas transportistas (tabla <c>transpor</c>) para el combo
    /// de "Nueva cuenta" del panel admin. Gateado por el mismo módulo 163 --
    /// a propósito NO reusa <c>Caseta/entrada/catalogos</c> (módulo 12/Gate):
    /// un usuario de tráfico/sistemas con acceso a pre-registro no
    /// necesariamente tiene acceso a Caseta.
    /// </summary>
    public async Task<List<CasetaEntradaCatalogItem>> ObtenerLineasTransporteAsync(CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<CasetaEntradaCatalogItem>(new CommandDefinition(@"
            SELECT cidtranspor AS Id, cnombre AS Nombre
            FROM transpor
            WHERE Inactivo = 0
            ORDER BY cnombre",
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<List<PreregistroCuentaListItem>> ListarCuentasAsync(CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        var rows = await conn.QueryAsync<PreregistroCuentaListItem>(new CommandDefinition(@"
            SELECT c.id AS Id, c.usuario AS Usuario, c.cidtranspor AS CidTranspor, c.nombre_linea AS NombreLinea,
                   c.contacto AS Contacto, c.activo AS Activo, c.fultimo_acceso AS FUltimoAcceso, c.fbloqueo AS FBloqueo,
                   (SELECT COUNT(*) FROM caseta_preregistro p
                    WHERE p.id_cuenta = c.id AND p.estatus = @vigente AND p.fexpira >= NOW()) AS Vigentes
            FROM preregistro_cuentas c
            ORDER BY c.nombre_linea",
            new { vigente = PreregistroEstatus.Vigente }, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<PreregistroCuentaCrearResponse> CrearCuentaAsync(PreregistroCuentaCrearRequest req, string usuarioTrafico, CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();

        bool existe = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT COUNT(*) > 0 FROM preregistro_cuentas WHERE usuario = @usuario",
            new { usuario = req.Usuario.Trim() }, cancellationToken: ct));

        if (existe)
            return new PreregistroCuentaCrearResponse { Success = false, Message = "Ya existe una cuenta con ese usuario." };

        string passwordInicial = GenerarPasswordTemporal();
        string hash = BCrypt.Net.BCrypt.HashPassword(passwordInicial);
        DateTime ahora = DateTime.Now;

        int id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
            INSERT INTO preregistro_cuentas
                (usuario, password_hash, cidtranspor, nombre_linea, contacto, activo, fcrea, usucrea)
            VALUES
                (@usuario, @hash, @cidTranspor, @nombreLinea, @contacto, 1, @ahora, @usucrea);
            SELECT LAST_INSERT_ID();",
            new
            {
                usuario = req.Usuario.Trim(),
                hash,
                cidTranspor = req.CidTranspor.Trim(),
                nombreLinea = req.NombreLinea.Trim(),
                contacto = req.Contacto,
                ahora,
                usucrea = usuarioTrafico
            }, cancellationToken: ct));

        await RegistrarAuditoriaAsync(conn, PreregistroEvento.AltaCuenta, idCuenta: id, usuario: usuarioTrafico, detalle: $"usuario={req.Usuario}", ct: ct);

        return new PreregistroCuentaCrearResponse { Success = true, Message = "Cuenta creada.", Id = id, PasswordInicial = passwordInicial };
    }

    public async Task<PreregistroResetPasswordResponse> ResetPasswordAsync(int id, string usuarioTrafico, CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();

        string passwordNueva = GenerarPasswordTemporal();
        string hash = BCrypt.Net.BCrypt.HashPassword(passwordNueva);

        int rows = await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE preregistro_cuentas
            SET password_hash = @hash, intentos_fallidos = 0, fbloqueo = NULL, fmodi = @ahora, usumodi = @usumodi
            WHERE id = @id",
            new { hash, ahora = DateTime.Now, usumodi = usuarioTrafico, id }, cancellationToken: ct));

        if (rows == 0)
            return new PreregistroResetPasswordResponse { Success = false, Message = "Cuenta no encontrada." };

        await RegistrarAuditoriaAsync(conn, PreregistroEvento.ResetPass, idCuenta: id, usuario: usuarioTrafico, ct: ct);

        return new PreregistroResetPasswordResponse { Success = true, Message = "Contrasena restablecida.", PasswordNueva = passwordNueva };
    }

    public async Task<bool> CambiarEstadoCuentaAsync(int id, bool activo, string usuarioTrafico, CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();

        int rows = await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE preregistro_cuentas
            SET activo = @activo, fmodi = @ahora, usumodi = @usumodi
            WHERE id = @id",
            new { activo, ahora = DateTime.Now, usumodi = usuarioTrafico, id }, cancellationToken: ct));

        if (rows > 0)
        {
            await RegistrarAuditoriaAsync(conn, activo ? PreregistroEvento.ReactivaCuenta : PreregistroEvento.BajaCuenta, idCuenta: id, usuario: usuarioTrafico, ct: ct);
        }

        return rows > 0;
    }

    public async Task<List<PreregistroAuditoriaItem>> ObtenerAuditoriaAsync(PreregistroAuditoriaFiltro filtro, CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        var rows = await conn.QueryAsync<PreregistroAuditoriaItem>(new CommandDefinition(@"
            SELECT fecha AS Fecha, evento AS Evento, usuario AS Usuario, folio AS Folio, ip AS Ip, detalle AS Detalle
            FROM preregistro_auditoria
            WHERE (@idCuenta IS NULL OR id_cuenta = @idCuenta)
              AND (@evento IS NULL OR @evento = '' OR evento = @evento)
              AND (@desde IS NULL OR fecha >= @desde)
              AND (@hasta IS NULL OR fecha <= @hasta)
            ORDER BY fecha DESC
            LIMIT 500",
            new { filtro.IdCuenta, filtro.Evento, filtro.Desde, filtro.Hasta }, cancellationToken: ct));

        return rows.ToList();
    }

    // ── Purga (usada por PreregistroPurgeWorker) ─────────────────────────

    public async Task<int> PurgarVencidosAsync(CancellationToken ct = default)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();

        // Los VIGENTE que ya expiraron pasan a EXPIRADO antes de purgar, para
        // que la auditoria (que no se purga) tenga trazabilidad del cambio.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE caseta_preregistro SET estatus = @expirado WHERE estatus = @vigente AND fexpira < NOW()",
            new { expirado = PreregistroEstatus.Expirado, vigente = PreregistroEstatus.Vigente }, cancellationToken: ct));

        // Las migas del recorrido llevan su propia fpurga denormalizada, asi que
        // se borran por indice sin JOIN -- y ANTES que su pre-registro, para que
        // no quede rastro huerfano si el segundo DELETE falla.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM caseta_preregistro_ubicacion WHERE fpurga < NOW()",
            cancellationToken: ct));

        int borrados = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM caseta_preregistro WHERE fpurga < NOW()",
            cancellationToken: ct));

        return borrados;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string MensajeEstatus(string estatus) => estatus switch
    {
        PreregistroEstatus.Vigente => "Pre-registro valido.",
        PreregistroEstatus.Usado => "Este codigo ya se uso.",
        PreregistroEstatus.Expirado => "Este codigo ya vencio.",
        PreregistroEstatus.Cancelado => "Este pre-registro fue cancelado.",
        _ => "No existe este pre-registro."
    };

    private async Task NotificarCreacionAsync(PreregistroCreadoNotification notification, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync("onPreregistroCreado", notification, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo notificar por SignalR el pre-registro {Folio}.", notification.Folio);
        }

        try
        {
            string[] destinatarios = _configuration
                .GetSection("Preregistro:NotificationEmails")
                .Get<string[]>()
                ?? _configuration.GetSection("RevisionReportEmails:FixedRecipients").Get<string[]>()
                ?? Array.Empty<string>();

            if (destinatarios.Length == 0)
            {
                _logger.LogWarning("No hay destinatarios configurados para avisos de pre-registro.");
                return;
            }

            string asunto = $"Nuevo pre-registro de camión: {notification.Folio} | Unidad: {notification.Nunidad} | {notification.NombreLinea}";
            string cuerpo = BuildPreregistroCreadoEmailBody(notification);

            await _smtpService.EnviarAsync(destinatarios, asunto, cuerpo, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo enviar el correo del pre-registro {Folio}.", notification.Folio);
        }
    }

    /// <summary>
    /// Aviso de "nuevo pre-registro" a tráfico/operación, mismo sistema visual
    /// que los correos de Entrada Caseta (navy + acento lima PMM) -- antes era
    /// una tabla plana sin marca.
    /// </summary>
    private static string BuildPreregistroCreadoEmailBody(PreregistroCreadoNotification n)
    {
        string etaTexto = n.EtaMinutos.HasValue
            ? $"{(n.EtaMinutos.Value < 60 ? $"{n.EtaMinutos} min" : $"{n.EtaMinutos / 60} h {n.EtaMinutos % 60} min")}" +
              (n.EtaKm.HasValue ? $" &middot; {n.EtaKm.Value.ToString("0.0")} km" : "")
            : "Aún sin ubicación reportada";

        return $@"<!DOCTYPE html PUBLIC '-//W3C//DTD XHTML 1.0 Transitional//EN' 'http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd'>
<html xmlns='http://www.w3.org/1999/xhtml'>
<head>
  <meta http-equiv='Content-Type' content='text/html; charset=utf-8' />
  <meta name='viewport' content='width=device-width, initial-scale=1.0' />
  <title>Nuevo pre-registro</title>
</head>
<body style='margin:0;padding:0;background-color:#f4f6f9;font-family:Arial,Helvetica,sans-serif;'>
  <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='background-color:#f4f6f9;table-layout:fixed;'>
    <tr>
      <td align='center' style='padding:20px 10px;'>
        <table role='presentation' align='center' width='650' cellpadding='0' cellspacing='0' border='0' style='width:100%;max-width:650px;background-color:#ffffff;border:1px solid #dcdfe6;'>

          <tr>
            <td style='background-color:#C4D600;height:6px;font-size:1px;line-height:1px;'>&nbsp;</td>
          </tr>

          <tr>
            <td style='background-color:#003d8c;padding:25px 30px;text-align:center;'>
              <div style='font-family:Arial,sans-serif;font-size:11px;text-transform:uppercase;letter-spacing:2px;color:#C4D600;font-weight:bold;margin-bottom:6px;'>
                PORTAL DE TRANSPORTISTAS &middot; GRUPO PMM
              </div>
              <h1 style='font-family:Arial,sans-serif;font-size:22px;font-weight:bold;color:#ffffff;margin:0;padding:0;'>
                Nuevo pre-registro de camión
              </h1>
            </td>
          </tr>

          <tr>
            <td style='padding:25px 30px;background-color:#ffffff;'>
              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='border-collapse:collapse;margin-bottom:20px;'>
                <tr>
                  <td width='160' style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;border-top:1px solid #e2e8f0;'>Folio</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;border-bottom:1px solid #e2e8f0;border-top:1px solid #e2e8f0;'>
                    <span style='background-color:#003d8c;color:#ffffff;font-size:12px;font-weight:bold;padding:4px 10px;display:inline-block;'>{Html(n.Folio)}</span>
                  </td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Línea transportista</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{Html(n.NombreLinea)}</td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Bodega</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{Html(n.IdBodega)}</td>
                </tr>
                <tr>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;color:#003d8c;background-color:#f0f4f9;border-bottom:1px solid #e2e8f0;'>Vigencia del QR</td>
                  <td style='padding:10px 14px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;'>{n.FechaExpira:dd/MM/yyyy HH:mm}</td>
                </tr>
              </table>

              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:25px;margin-bottom:10px;'>
                <tr>
                  <td style='background-color:#003d8c;padding:8px 14px;color:#ffffff;font-family:Arial,sans-serif;font-size:12px;font-weight:bold;text-transform:uppercase;letter-spacing:0.5px;border-left:5px solid #C4D600;'>
                    Datos del Tractocamión
                  </td>
                </tr>
              </table>

              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='border-collapse:collapse;border:1px solid #cbd5e1;'>
                <thead>
                  <tr style='background-color:#003d8c;color:#ffffff;'>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;border-right:1px solid #002d68;'>Unidad</th>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;border-right:1px solid #002d68;'>Placas</th>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;border-right:1px solid #002d68;'>Operador</th>
                    <th style='padding:10px 10px;font-family:Arial,sans-serif;font-size:11px;font-weight:bold;text-transform:uppercase;text-align:left;color:#ffffff;'>ETA estimado</th>
                  </tr>
                </thead>
                <tbody>
                  <tr style='background-color:#fcfcfc;'>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#003d8c;font-weight:bold;border-bottom:1px solid #e2e8f0;border-right:1px solid #e2e8f0;background-color:#f0f4f9;'>{Html(n.Nunidad)}</td>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;border-right:1px solid #e2e8f0;'>{Html(n.Placas)}</td>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;border-bottom:1px solid #e2e8f0;border-right:1px solid #e2e8f0;'>{Html(n.Operador)}</td>
                    <td style='padding:12px 10px;font-family:Arial,sans-serif;font-size:13px;color:#1e293b;font-weight:bold;border-bottom:1px solid #e2e8f0;'>{etaTexto}</td>
                  </tr>
                </tbody>
              </table>

              <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:20px;background-color:#f8fafc;border-left:5px solid #003d8c;border-top:1px solid #e2e8f0;border-right:1px solid #e2e8f0;border-bottom:1px solid #e2e8f0;'>
                <tr>
                  <td style='padding:14px 18px;'>
                    <div style='font-family:Arial,sans-serif;font-size:13px;color:#334155;line-height:1.5;'>
                      El registro ya está disponible en Centro de Caseta (app móvil) y en el panel administrativo. El chofer debe presentar el código QR al llegar a la caseta.
                    </div>
                  </td>
                </tr>
              </table>

            </td>
          </tr>

          <tr>
            <td style='background-color:#003d8c;padding:18px 30px;text-align:center;border-top:3px solid #C4D600;'>
              <p style='font-family:Arial,sans-serif;font-size:11px;color:#ffffff;line-height:1.5;margin:0;'>
                Este aviso fue generado automáticamente desde el <strong>Portal de Transportistas PMM</strong>.<br />
                <span style='color:#C4D600;'>Pedro Martínez Méndez y Cía S.C. &amp; Imex Forwarding Agency Inc.</span>
              </p>
            </td>
          </tr>

        </table>
      </td>
    </tr>
  </table>
</body>
</html>";
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string GenerarToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes); // 32 caracteres, coincide con CHAR(32)
    }

    private static string GenerarPasswordTemporal()
    {
        // Legible para dictarla por telefono: sin caracteres ambiguos (0/O, 1/I/l).
        const string alfabeto = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[10];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = alfabeto[bytes[i] % alfabeto.Length];
        return new string(chars);
    }

    private (string Token, DateTime ExpiraUtc) GenerarJwtPortal(CuentaRow cuenta)
    {
        var jwtSettings = _configuration.GetSection("PreregistroJwt");
        string secret = jwtSettings["Secret"] ?? throw new InvalidOperationException(
            "PreregistroJwt:Secret no configurado. Ver appsettings.Local.json.");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, cuenta.Usuario),
            new(ClaimTypes.NameIdentifier, cuenta.Id.ToString()),
            new("scope", "preregistro"),
            new("id_cuenta", cuenta.Id.ToString()),
            new("cidtranspor", cuenta.CidTranspor),
            new("nombre_linea", cuenta.NombreLinea),
            new("jti", Guid.NewGuid().ToString())
        };

        int expiryHours = jwtSettings.GetValue<int?>("ExpiryInHours") ?? 12;
        DateTime expira = DateTime.UtcNow.AddHours(expiryHours);

        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"],
            audience: jwtSettings["Audience"],
            claims: claims,
            expires: expira,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expira);
    }

    /// <summary>
    /// Registra un evento de auditoria. Todos los datos salvo <paramref name="conn"/>
    /// y <paramref name="evento"/> son opcionales y SIEMPRE se pasan por nombre en
    /// las llamadas (usuario/folio/ip/detalle son todos string y es facil
    /// transponerlos por accidente si se pasan posicionalmente).
    /// </summary>
    private static async Task RegistrarAuditoriaAsync(
        IDbConnection conn,
        string evento,
        int? idCuenta = null,
        string? usuario = null,
        string? folio = null,
        string? ip = null,
        string? detalle = null,
        IDbTransaction? tx = null,
        CancellationToken ct = default)
    {
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO preregistro_auditoria (fecha, evento, id_cuenta, usuario, folio, ip, detalle)
                VALUES (@fecha, @evento, @idCuenta, @usuario, @folio, @ip, @detalle)",
                new { fecha = DateTime.Now, evento, idCuenta, usuario, folio, ip, detalle }, tx, cancellationToken: ct));
        }
        catch
        {
            // La auditoria nunca debe tumbar la operacion principal.
        }
    }

    private class CuentaRow
    {
        public int Id { get; set; }
        public string Usuario { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string CidTranspor { get; set; } = string.Empty;
        public string NombreLinea { get; set; } = string.Empty;
        public bool Activo { get; set; }
        public int IntentosFallidos { get; set; }
        public DateTime? FBloqueo { get; set; }
    }

    private class EstadoPreregistroRow
    {
        public string? IdBodega { get; set; }
        public string Estatus { get; set; } = string.Empty;
    }

    /// <summary>Igual que EstadoPreregistroRow mas la fecha de purga, que se
    /// copia denormalizada en cada miga del recorrido.</summary>
    private class EstadoUbicacionRow
    {
        public string? IdBodega { get; set; }
        public string Estatus { get; set; } = string.Empty;
        public DateTime FPurga { get; set; }
        public double? DestinoLat { get; set; }
        public double? DestinoLon { get; set; }
        public double? DestinoGeocercaLat { get; set; }
        public double? DestinoGeocercaLon { get; set; }
        public int? DestinoGeocercaMetros { get; set; }
    }

    private class PreregistroRow
    {
        public string Folio { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string Nunidad { get; set; } = string.Empty;
        public string Placas { get; set; } = string.Empty;
        public string Operador { get; set; } = string.Empty;
        public string? IdTransporu { get; set; }
        public string? NombreLinea { get; set; }
        public string? IdBodega { get; set; }
        public string Estatus { get; set; } = string.Empty;
        public DateTime FechaExpira { get; set; }
        public DateTime? FechaUso { get; set; }
        public string? UsuarioUso { get; set; }
        public string? CasetaId { get; set; }
    }
}
