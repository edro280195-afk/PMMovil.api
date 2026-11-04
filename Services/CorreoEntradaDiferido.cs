using System.Text.Json;

namespace PMMovil.Api.Services;

/// <summary>
/// Un correo de entrada de caseta que se guardó a propósito para MÁS TARDE: la app guardó el folio con fotos nuevas y el
/// aviso debe salir cuando esas fotos lleguen, no antes (el correo lleva el enlace a las fotos).
/// </summary>
public sealed class PendienteCorreoEntrada
{
    public string CasetaId { get; set; } = string.Empty;

    /// <summary>true cuando el folio ya existía y el aviso es una ACTUALIZACIÓN: lo manda el servidor, no la tableta.</summary>
    public bool Actualizado { get; set; }

    public string Usuario { get; set; } = string.Empty;

    /// <summary>Posiciones de las fotos nuevas que la tableta va a mandar. Vacío = no se sabe (apps anteriores).</summary>
    public List<int> IndicesEsperados { get; set; } = [];

    public DateTime Registrado { get; set; }
    public DateTime UltimaActividad { get; set; }

    /// <summary>Desde cuándo el respaldo puede mandarlo aunque falten fotos.</summary>
    public DateTime Vence { get; set; }

    public int Intentos { get; set; }
    public string? UltimoError { get; set; }
}

/// <summary>
/// Por qué sale (o no) el correo diferido cuando llega un lote de fotos. Lógica pura para poder probarla sin servidor.
/// </summary>
public enum MotivoCorreoDiferido
{
    /// <summary>Todavía no toca: faltan fotos y la tableta no ha dicho que terminó.</summary>
    Ninguno,

    /// <summary>Ya están todas las fotos que la tableta anunció al guardar.</summary>
    FotosCompletas,

    /// <summary>La tableta mandó el último lote (así se avisaba antes de este respaldo).</summary>
    Finalizar,

    /// <summary>Nadie lo había registrado (app anterior) y la tableta mandó el último lote: se avisa como siempre.</summary>
    FinalizarSinRegistro,

    /// <summary>Pasó el tiempo sin que terminara la sincronización: sale con las fotos que haya.</summary>
    Respaldo
}

public static class CorreoEntradaDiferido
{
    /// <summary>
    /// Cuál es el motivo para mandar ahora, o <see cref="MotivoCorreoDiferido.Ninguno"/>. [yaAvisado] evita el segundo
    /// correo cuando el último lote llega DESPUÉS de que el respaldo ya avisó (la tableta estuvo sin red).
    /// </summary>
    public static MotivoCorreoDiferido Decidir(bool hayPendiente, bool finalizar, bool fotosCompletas, bool yaAvisado)
    {
        if (hayPendiente)
        {
            if (fotosCompletas) return MotivoCorreoDiferido.FotosCompletas;
            return finalizar ? MotivoCorreoDiferido.Finalizar : MotivoCorreoDiferido.Ninguno;
        }

        return finalizar && !yaAvisado ? MotivoCorreoDiferido.FinalizarSinRegistro : MotivoCorreoDiferido.Ninguno;
    }

    /// <summary>
    /// ¿Ya están todas las fotos anunciadas? Sin anuncio (lista vacía) nunca se completa por conteo: en ese caso decide
    /// el último lote o el respaldo.
    /// </summary>
    public static bool Completas(IReadOnlyCollection<int> esperados, IEnumerable<int> presentes)
    {
        if (esperados.Count == 0) return false;
        var tengo = presentes.ToHashSet();
        return esperados.All(tengo.Contains);
    }
}

public interface ICorreoEntradaDiferido
{
    /// <summary>Deja anotado que el aviso de [casetaId] debe salir cuando lleguen las fotos. Nunca lanza.</summary>
    void Registrar(string casetaId, bool actualizado, string usuario, IReadOnlyCollection<int> indicesEsperados);

    PendienteCorreoEntrada? Leer(string casetaId);

    /// <summary>Llegó un lote de fotos: aplaza el respaldo, porque la sincronización sigue avanzando.</summary>
    void RegistrarActividad(string casetaId);

    /// <summary>
    /// Se queda con el derecho de mandar el aviso. Solo UNO lo logra aunque lo pidan a la vez el último lote y el
    /// respaldo; el otro recibe null y no manda nada.
    /// </summary>
    PendienteCorreoEntrada? Reclamar(string casetaId);

    /// <summary>El aviso quedó en la cola de correo: se anota para que un último lote tardío no lo repita.</summary>
    void MarcarAvisado(PendienteCorreoEntrada pendiente, MotivoCorreoDiferido motivo);

    /// <summary>No se pudo armar o encolar: vuelve a la lista para que el respaldo lo reintente, o se aparta si ya se agotó.</summary>
    void Devolver(PendienteCorreoEntrada pendiente, string error);

    bool YaAvisado(string casetaId);

    /// <summary>Los que ya pueden salir aunque falten fotos.</summary>
    IReadOnlyList<PendienteCorreoEntrada> Vencidos();

    int Pendientes { get; }
}

/// <summary>
/// Almacén en disco de los avisos diferidos, junto a la cola de correo (misma carpeta <c>outbox</c>). Sobrevive a un
/// reinicio de IIS y no necesita SQL. Cada paso es un renombrado de archivo, que es atómico: así dos peticiones
/// simultáneas nunca mandan el mismo aviso dos veces.
/// </summary>
public sealed class AlmacenCorreoEntradaDiferido : ICorreoEntradaDiferido
{
    /// <summary>Cuánto se espera, sin actividad, antes de mandar con las fotos que haya.</summary>
    public static readonly TimeSpan GraciaPorOmision = TimeSpan.FromMinutes(5);

    /// <summary>Tope desde el guardado: aunque sigan llegando fotos, el aviso no se retrasa más que esto.</summary>
    public static readonly TimeSpan Tope = TimeSpan.FromMinutes(30);

    public const int MaxIntentos = 5;

    private static readonly TimeSpan EsperaReintento = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReclamoVencido = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetencionAvisados = TimeSpan.FromDays(3);
    private static readonly TimeSpan RetencionFallidos = TimeSpan.FromDays(60);

    private readonly string _pendientes;
    private readonly string _reclamados;
    private readonly string _avisados;
    private readonly string _fallidos;
    private readonly ILogger<AlmacenCorreoEntradaDiferido> _logger;
    private readonly Func<DateTime> _ahora;
    private readonly TimeSpan _gracia;
    private readonly object _candado = new();

    public AlmacenCorreoEntradaDiferido(
        string directorio,
        ILogger<AlmacenCorreoEntradaDiferido> logger,
        TimeSpan? gracia = null,
        Func<DateTime>? ahora = null)
    {
        _logger = logger;
        _ahora = ahora ?? (() => DateTime.Now);
        _gracia = gracia is { } g && g > TimeSpan.Zero ? g : GraciaPorOmision;
        _pendientes = Path.Combine(directorio, "pendientes");
        _reclamados = Path.Combine(directorio, "reclamados");
        _avisados = Path.Combine(directorio, "avisados");
        _fallidos = Path.Combine(directorio, "fallidos");
        foreach (string carpeta in new[] { _pendientes, _reclamados, _avisados, _fallidos })
        {
            Directory.CreateDirectory(carpeta);
        }

        RecuperarReclamosHuerfanos();
    }

    public int Pendientes => Directory.EnumerateFiles(_pendientes, "*.json").Count();

    public void Registrar(string casetaId, bool actualizado, string usuario, IReadOnlyCollection<int> indicesEsperados)
    {
        try
        {
            DateTime ahora = _ahora();
            var pendiente = new PendienteCorreoEntrada
            {
                CasetaId = casetaId.Trim(),
                Actualizado = actualizado,
                Usuario = usuario ?? string.Empty,
                IndicesEsperados = indicesEsperados.Where(i => i > 0).Distinct().OrderBy(i => i).ToList(),
                Registrado = ahora,
                UltimaActividad = ahora,
                Vence = ahora + _gracia
            };

            lock (_candado)
            {
                // Un guardado nuevo del mismo folio (otra edición) abre un aviso nuevo: el anterior ya no cuenta.
                TryDelete(RutaAvisado(pendiente.CasetaId));
                Escribir(RutaPendiente(pendiente.CasetaId), pendiente);
            }
        }
        catch (Exception ex)
        {
            // Si no se puede anotar, sigue funcionando el aviso por último lote, como antes de este respaldo.
            _logger.LogError(ex, "CorreoEntradaDiferido: no se pudo anotar el aviso de {CasetaId}; dependerá del último lote de fotos.", casetaId);
        }
    }

    public PendienteCorreoEntrada? Leer(string casetaId) => LeerArchivo(RutaPendiente(casetaId));

    public void RegistrarActividad(string casetaId)
    {
        lock (_candado)
        {
            string ruta = RutaPendiente(casetaId);
            PendienteCorreoEntrada? pendiente = LeerArchivo(ruta);
            if (pendiente == null) return;

            DateTime ahora = _ahora();
            pendiente.UltimaActividad = ahora;
            DateTime limite = pendiente.Registrado + Tope;
            DateTime siguiente = ahora + _gracia;
            pendiente.Vence = siguiente < limite ? siguiente : limite;
            try
            {
                Escribir(ruta, pendiente);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "CorreoEntradaDiferido: no se pudo aplazar el respaldo de {CasetaId}.", casetaId);
            }
        }
    }

    public PendienteCorreoEntrada? Reclamar(string casetaId)
    {
        string origen = RutaPendiente(casetaId);
        string destino = RutaReclamado(casetaId);
        try
        {
            File.Move(origen, destino, overwrite: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return null;
        }

        PendienteCorreoEntrada? pendiente = LeerArchivo(destino);
        if (pendiente == null)
        {
            _logger.LogError("CorreoEntradaDiferido: el archivo de {CasetaId} está dañado; se aparta en fallidos.", casetaId);
            Apartar(destino, _fallidos);
        }

        return pendiente;
    }

    public void MarcarAvisado(PendienteCorreoEntrada pendiente, MotivoCorreoDiferido motivo)
    {
        try
        {
            DateTime ahora = _ahora();
            File.WriteAllText(
                RutaAvisado(pendiente.CasetaId),
                JsonSerializer.Serialize(new { pendiente.CasetaId, Motivo = motivo.ToString(), Avisado = ahora }));
            TryDelete(RutaReclamado(pendiente.CasetaId));
            _logger.LogInformation(
                "CorreoEntradaDiferido: aviso de {CasetaId} encolado por {Motivo}, {Segundos:F1} s despues del guardado.",
                pendiente.CasetaId, motivo, (ahora - pendiente.Registrado).TotalSeconds);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "CorreoEntradaDiferido: no se pudo anotar como avisado {CasetaId}.", pendiente.CasetaId);
        }
    }

    public void Devolver(PendienteCorreoEntrada pendiente, string error)
    {
        pendiente.Intentos++;
        pendiente.UltimoError = error;
        try
        {
            lock (_candado)
            {
                if (pendiente.Intentos >= MaxIntentos)
                {
                    Escribir(RutaFallido(pendiente.CasetaId), pendiente);
                    TryDelete(RutaReclamado(pendiente.CasetaId));
                    _logger.LogCritical(
                        "CorreoEntradaDiferido: SE RINDIO con el aviso de {CasetaId} tras {Intentos} intentos ({Error}). Queda en {Carpeta}; hay que reenviarlo a mano con --reenviar-correo-caseta.",
                        pendiente.CasetaId, pendiente.Intentos, error, _fallidos);
                    return;
                }

                pendiente.Vence = _ahora() + EsperaReintento;
                Escribir(RutaPendiente(pendiente.CasetaId), pendiente);
                TryDelete(RutaReclamado(pendiente.CasetaId));
            }

            _logger.LogWarning(
                "CorreoEntradaDiferido: no salio el aviso de {CasetaId} (intento {Intento} de {Max}): {Error}. Se reintenta en {Espera}.",
                pendiente.CasetaId, pendiente.Intentos, MaxIntentos, error, EsperaReintento);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "CorreoEntradaDiferido: no se pudo devolver {CasetaId} a la lista.", pendiente.CasetaId);
        }
    }

    public bool YaAvisado(string casetaId) => File.Exists(RutaAvisado(casetaId));

    public IReadOnlyList<PendienteCorreoEntrada> Vencidos()
    {
        DateTime ahora = _ahora();
        var vencidos = new List<PendienteCorreoEntrada>();
        foreach (string archivo in Directory.EnumerateFiles(_pendientes, "*.json"))
        {
            PendienteCorreoEntrada? pendiente = LeerArchivo(archivo);
            if (pendiente == null)
            {
                _logger.LogError("CorreoEntradaDiferido: el archivo {Archivo} está dañado; se aparta en fallidos.", archivo);
                Apartar(archivo, _fallidos);
                continue;
            }

            if (pendiente.Vence <= ahora) vencidos.Add(pendiente);
        }

        Depurar();
        return vencidos;
    }

    /// <summary>Un reclamo que quedó a medias (el proceso murió al mandar) vuelve a la lista: mejor un aviso repetido que perdido.</summary>
    private void RecuperarReclamosHuerfanos()
    {
        try
        {
            DateTime limite = _ahora() - ReclamoVencido;
            foreach (string archivo in Directory.EnumerateFiles(_reclamados, "*.json"))
            {
                if (File.GetLastWriteTime(archivo) < limite)
                {
                    File.Move(archivo, Path.Combine(_pendientes, Path.GetFileName(archivo)), overwrite: true);
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "CorreoEntradaDiferido: no se pudieron recuperar reclamos pendientes.");
        }
    }

    private void Depurar()
    {
        Borrar(_avisados, RetencionAvisados);
        Borrar(_fallidos, RetencionFallidos);
        RecuperarReclamosHuerfanos();
    }

    private void Borrar(string carpeta, TimeSpan retencion)
    {
        try
        {
            DateTime limite = _ahora() - retencion;
            foreach (string archivo in Directory.EnumerateFiles(carpeta))
            {
                if (File.GetLastWriteTime(archivo) < limite) File.Delete(archivo);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "CorreoEntradaDiferido: no se pudo depurar {Carpeta}.", carpeta);
        }
    }

    // El folio lleva diagonal (E-020485/26): el nombre del archivo solo usa caracteres seguros, y el folio real vive dentro.
    private static string NombreArchivo(string casetaId)
    {
        var limpio = new string(casetaId.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        return limpio.ToUpperInvariant() + ".json";
    }

    private string RutaPendiente(string casetaId) => Path.Combine(_pendientes, NombreArchivo(CasetaFolio.Normalizar(casetaId)));
    private string RutaReclamado(string casetaId) => Path.Combine(_reclamados, NombreArchivo(CasetaFolio.Normalizar(casetaId)));
    private string RutaAvisado(string casetaId) => Path.Combine(_avisados, NombreArchivo(CasetaFolio.Normalizar(casetaId)));
    private string RutaFallido(string casetaId) => Path.Combine(_fallidos, NombreArchivo(CasetaFolio.Normalizar(casetaId)));

    private static void Escribir(string ruta, PendienteCorreoEntrada pendiente)
    {
        string temporal = ruta + ".tmp";
        File.WriteAllText(temporal, JsonSerializer.Serialize(pendiente));
        File.Move(temporal, ruta, overwrite: true);
    }

    private static PendienteCorreoEntrada? LeerArchivo(string ruta)
    {
        try
        {
            return File.Exists(ruta) ? JsonSerializer.Deserialize<PendienteCorreoEntrada>(File.ReadAllText(ruta)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static void Apartar(string archivo, string destino)
    {
        try
        {
            File.Move(archivo, Path.Combine(destino, Path.GetFileName(archivo)), overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    private static void TryDelete(string ruta)
    {
        try
        {
            File.Delete(ruta);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// El respaldo: cada medio minuto revisa los avisos diferidos cuyo tiempo se cumplió sin que la sincronización de fotos
/// terminara (la tableta sin red, una foto que no se pudo procesar, la app cerrada) y manda el correo con las fotos que
/// haya. Así el aviso de una entrada NUNCA depende de que la tableta termine bien.
/// </summary>
public sealed class RespaldoCorreoEntrada : BackgroundService
{
    private readonly ICorreoEntradaDiferido _almacen;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RespaldoCorreoEntrada> _logger;

    public RespaldoCorreoEntrada(
        ICorreoEntradaDiferido almacen,
        IServiceScopeFactory scopes,
        ILogger<RespaldoCorreoEntrada> logger)
    {
        _almacen = almacen;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RespaldoCorreoEntrada: activo. Avisos diferidos pendientes={Pendientes}.", _almacen.Pendientes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcesarVencidosAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RespaldoCorreoEntrada: fallo no controlado; se vuelve a intentar.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Manda los avisos vencidos. Devuelve cuántos salieron. Público para probarlo sin esperar al reloj.</summary>
    public async Task<int> ProcesarVencidosAsync(CancellationToken ct)
    {
        int enviados = 0;
        foreach (PendienteCorreoEntrada vencido in _almacen.Vencidos())
        {
            ct.ThrowIfCancellationRequested();

            PendienteCorreoEntrada? reclamado = _almacen.Reclamar(vencido.CasetaId);
            if (reclamado == null) continue; // otro lote o el último lote se le adelantó

            try
            {
                using IServiceScope scope = _scopes.CreateScope();
                var caseta = scope.ServiceProvider.GetRequiredService<CasetaService>();
                if (await caseta.EnviarCorreoEntradaDiferidoAsync(reclamado, ct))
                {
                    _almacen.MarcarAvisado(reclamado, MotivoCorreoDiferido.Respaldo);
                    enviados++;
                }
                else
                {
                    _almacen.Devolver(reclamado, "el correo no se pudo armar ni encolar (revisa el log)");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _almacen.Devolver(reclamado, "el servicio se detuvo a mitad del envío");
                throw;
            }
            catch (Exception ex)
            {
                _almacen.Devolver(reclamado, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        return enviados;
    }
}
