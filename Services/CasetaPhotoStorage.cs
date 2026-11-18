using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PMMovil.Api.Services;

public sealed record CasetaPhotoStage(string FileName, string StagedPath);

/// <summary>Tamaño en que se sirve una foto de caseta.</summary>
public enum CasetaFotoTamano
{
    Original,

    /// <summary>Para verla en pantalla: lado mayor de <see cref="FotoVariantes.VistaLado"/> px.</summary>
    Vista,

    /// <summary>Para cuadrículas y tiras: lado mayor de <see cref="FotoVariantes.MiniLado"/> px.</summary>
    Mini
}

/// <summary>Archivo listo para servirse. <see cref="ModificadaUtc"/> identifica la versión de la foto.</summary>
public sealed record CasetaFotoArchivo(string Ruta, DateTime ModificadaUtc, bool EsVariante);

/// <summary>Lo que hizo una pasada de limpieza del almacén caliente.</summary>
public sealed record CasetaLimpiezaResultado(
    int Desalojadas,
    int SinCopiaEn23,
    int VistasBorradas,
    int MiniaturasBorradas,
    int MarcasBorradas)
{
    public static readonly CasetaLimpiezaResultado Nada = new(0, 0, 0, 0, 0);

    public bool HuboCambios => Desalojadas + SinCopiaEn23 + VistasBorradas + MiniaturasBorradas + MarcasBorradas > 0;
}

/// <summary>
/// Dónde viven las fotos de caseta (entrada 106 y salida 101) y cómo se mueven.
///
/// El API corre en Azure y el destino definitivo de las fotos es el servidor de archivos de la oficina (el "23",
/// <c>\\192.168.10.23\...\Fotos\Caseta</c>), al otro lado del túnel: cada foto que se lee o escribe allá cruza el
/// internet de la oficina. Por eso hay tres lugares:
///
/// <list type="number">
/// <item><b>Pendientes</b> (cola local): lo que llegó y todavía no está en el 23. Se copia al 23 de inmediato, en segundo
/// plano y varias a la vez.</item>
/// <item><b>Caliente</b> (disco del servidor del API): la copia que ya se confirmó en el 23 se queda aquí
/// <see cref="_horasCaliente"/> horas desde el último movimiento de su registro (alta, baja, reemplazo o consulta), porque
/// casi todo lo que se corrige de una caseta se corrige el mismo día. Aquí viven también la miniatura (se queda
/// <see cref="_diasMiniatura"/> días: pesa ~14 KB) y la vista para pantalla.</item>
/// <item><b>El 23</b>: siempre tiene todo. Una consulta a un registro viejo lo lee de ahí una vez y lo vuelve a calentar.</item>
/// </list>
///
/// Reglas que no se rompen: nada se quita de lo caliente si no está confirmado en el 23; una baja quita lo caliente en el
/// acto; una versión nueva invalida su vista y su miniatura. Todo lector de fotos de caseta pasa por aquí
/// (<see cref="FindReadablePhotoPath(string)"/> y <see cref="ResolverAsync"/>), así nadie lee del 23 por su cuenta una
/// foto que aún se está copiando.
/// </summary>
public class CasetaPhotoStorage
{
    private const string TempSufijo = ".tmp";
    private const string ApartadoSufijo = ".publicando";
    private static readonly TimeSpan ActividadMinima = TimeSpan.FromHours(1);

    private readonly IConfiguration _config;
    private readonly ILogger<CasetaPhotoStorage> _logger;
    private readonly IFotoMejoraCola? _mejora;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    // Señal para que el trabajador publique ya, sin esperar su intervalo.
    private readonly SemaphoreSlim _hayTrabajo = new(0, 1);

    // Tope de CPU para generar miniaturas y vistas (decodificar una foto de 5 MP cuesta ~100-300 ms).
    private readonly SemaphoreSlim _cpuVariantes = new(2, 2);
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _variantesEnCurso = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _calentando = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _avisadasSinCopia = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _hotHabilitado;
    private readonly string _hotRoot;
    private readonly TimeSpan _horasCaliente;
    private readonly TimeSpan _diasMiniatura;
    private readonly int _paralelo;
    private readonly Lazy<bool> _hotDisponible;

    public CasetaPhotoStorage(IConfiguration config, ILogger<CasetaPhotoStorage> logger, IFotoMejoraCola? mejora = null)
    {
        _config = config;
        _logger = logger;
        _mejora = mejora;

        _hotHabilitado = config.GetValue<bool?>("CasetaPhotoSettings:HotCacheEnabled") ?? true;
        _hotRoot = config["CasetaPhotoSettings:HotPath"] is { Length: > 0 } hotPath
            ? hotPath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PMMovil",
                "FotosCaliente",
                "Caseta");
        _horasCaliente = TimeSpan.FromHours(Math.Clamp(config.GetValue<int?>("CasetaPhotoSettings:HotHours") ?? 48, 1, 24 * 30));
        _diasMiniatura = TimeSpan.FromDays(Math.Clamp(config.GetValue<int?>("CasetaPhotoSettings:ThumbnailDays") ?? 365, 1, 3650));
        _paralelo = Math.Clamp(config.GetValue<int?>("CasetaPhotoSettings:PublishParallelism") ?? 3, 1, 8);
        _hotDisponible = new Lazy<bool>(ProbarCaliente, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>True si el almacén caliente está encendido y su carpeta se puede escribir.</summary>
    public bool HotCacheActivo => _hotHabilitado && _hotDisponible.Value;

    /// <summary>Miniatura y vista solo existen con almacén caliente y en Windows (System.Drawing).</summary>
    public bool VariantesDisponibles => HotCacheActivo && OperatingSystem.IsWindows();

    public string GetFileName(string casetaId, int index)
    {
        return $"{casetaId.Replace('/', '-')}-{index:D4}.jpg";
    }

    public string? FindReadablePhotoPath(string casetaId, int index, string? extension = null)
    {
        Encontrada? foto = LocalizarOriginal(GetCandidateFileNames(casetaId, index, extension));
        return foto == null ? null : AlEncontrar(foto);
    }

    public IReadOnlyList<string> GetCandidateFileNames(string casetaId, int index, string? extension = null)
    {
        string normalized = casetaId.Trim().Replace('/', '-');
        string compact = Regex.Replace(casetaId.Trim(), @"[^\w-]", "");
        string preferredExt = NormalizeExtension(extension);
        string[] extensions = new[] { preferredExt, "jpg", "jpeg", "png", "webp" }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var names = new List<string>();
        foreach (string ext in extensions)
        {
            names.Add($"{normalized}-{index:D4}.{ext}");
            names.Add($"{normalized}-106{index:D2}.{ext}");
            names.Add($"{compact}-106{index:D2}.{ext}");
            names.Add($"{compact}-{index:D4}.{ext}");
            names.Add($"{normalized}-{index:D2}.{ext}");
        }

        return names
            .Where(x => !string.IsNullOrWhiteSpace(Path.GetFileName(x)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<CasetaPhotoStage> StageAsync(byte[] data, string fileName, CancellationToken ct)
    {
        string safeFileName = Path.GetFileName(fileName);
        string staging = GetStagingDirectory();
        Directory.CreateDirectory(staging);

        string stagedFile = Path.Combine(staging, $"{Guid.NewGuid():N}-{safeFileName}");
        string tempFile = stagedFile + ".tmp";

        await File.WriteAllBytesAsync(tempFile, data, ct);
        File.Move(tempFile, stagedFile, overwrite: true);

        return new CasetaPhotoStage(safeFileName, stagedFile);
    }

    public void ActivateStagedPhotos(IEnumerable<CasetaPhotoStage> photos)
    {
        string pending = GetPendingDirectory();
        Directory.CreateDirectory(pending);
        bool alguna = false;

        foreach (CasetaPhotoStage photo in photos)
        {
            // Una foto nueva con el mismo índice reemplaza una baja pendiente de ese nombre.
            TryDelete(Path.Combine(GetDeleteDirectory(), photo.FileName + ".delete"));

            string target = Path.Combine(pending, photo.FileName);
            // El trabajador puede estar leyendo la versión anterior (la abre permitiendo borrarla): se reintenta un
            // momento en vez de fallar una petición que ya guardó en la base.
            ConReintentos(() =>
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(photo.StagedPath, target, overwrite: true);
            });

            // La versión nueva deja viejas su vista y su miniatura; el original caliente anterior lo tapa la pendiente
            // (se lee primero) y lo pisa la publicación.
            InvalidarVariantes(photo.FileName);
            TocarActividad(photo.FileName, forzar: true);
            alguna = true;
        }

        if (alguna)
        {
            AvisarTrabajo();
        }
    }

    public void CleanupStagedPhotos(IEnumerable<CasetaPhotoStage> photos)
    {
        foreach (CasetaPhotoStage photo in photos)
        {
            TryDelete(photo.StagedPath);
        }
    }

    public void QueueDelete(string fileName)
    {
        string safeFileName = Path.GetFileName(fileName);
        string deleteQueue = GetDeleteDirectory();
        Directory.CreateDirectory(deleteQueue);

        // Primero la marca: desde aquí ningún lector ni calentado la vuelve a servir, aunque el 23 aún la tenga.
        string marker = Path.Combine(deleteQueue, safeFileName + ".delete");
        File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));

        TryDelete(Path.Combine(GetPendingDirectory(), safeFileName));
        BorrarCaliente(safeFileName);
        TocarActividad(safeFileName, forzar: true);
        AvisarTrabajo();
    }

    public string? FindReadablePhotoPath(string fileName)
    {
        string safeFileName = Path.GetFileName(fileName);
        Encontrada? foto = LocalizarLocal(safeFileName) ?? LocalizarRemota(safeFileName);
        return foto == null ? null : AlEncontrar(foto);
    }

    public IEnumerable<string> EnumerateCasetaPhotos(string casetaId)
    {
        string prefix = casetaId.Replace("/", "-") + "-*.*";
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var directories = new List<string> { GetPendingDirectory() };
        if (HotCacheActivo)
        {
            directories.Add(GetHotFotosDirectory());
        }

        directories.Add(GetFallbackDirectory());
        directories.Add(GetPrimaryDirectory());

        foreach (string directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(directory, prefix).OrderBy(x => x))
            {
                string fileName = Path.GetFileName(file);
                if (EsTemporal(fileName) || ExisteBaja(fileName))
                {
                    continue;
                }

                files.TryAdd(fileName, file);
            }
        }

        return files
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Value);
    }

    /// <summary>
    /// Archivo a servir para una foto en el tamaño pedido. La miniatura y la vista se generan al vuelo la primera vez
    /// (de lo local si existe, del 23 si no) y se guardan en lo caliente. Si no se pueden generar se sirve el original:
    /// la foto siempre se ve.
    /// </summary>
    public async Task<CasetaFotoArchivo?> ResolverAsync(
        string casetaId,
        int index,
        string? extension,
        CasetaFotoTamano tamano,
        CancellationToken ct = default)
    {
        IReadOnlyList<string> nombres = GetCandidateFileNames(casetaId, index, extension);

        if (tamano == CasetaFotoTamano.Original || !VariantesDisponibles)
        {
            Encontrada? original = LocalizarOriginal(nombres);
            return original == null ? null : ComoArchivo(AlEncontrar(original), esVariante: false);
        }

        // 1) La foto está aquí (pendiente, caliente o respaldo): su variante debe ser de esa misma versión.
        foreach (string nombre in nombres)
        {
            Encontrada? local = LocalizarLocal(nombre);
            if (local == null)
            {
                continue;
            }

            if (local.Donde == Ubicacion.Caliente)
            {
                TocarActividad(nombre, forzar: false);
            }

            return await VarianteDeAsync(local, tamano, calentarOriginal: false, ct)
                ?? ComoArchivo(local.Ruta, esVariante: false);
        }

        // 2) Nada local: una variante que ya exista sirve sin ir al 23 (las altas y bajas del API la invalidan).
        foreach (string nombre in nombres)
        {
            if (ExisteBaja(nombre))
            {
                continue;
            }

            string variante = RutaCaliente(nombre, tamano);
            if (File.Exists(variante))
            {
                return ComoArchivo(variante, esVariante: true);
            }
        }

        // 3) Solo está en el 23: se lee una vez, se calienta y se generan sus variantes.
        foreach (string nombre in nombres)
        {
            Encontrada? remota = LocalizarRemota(nombre);
            if (remota == null)
            {
                continue;
            }

            CasetaFotoArchivo? variante = await VarianteDeAsync(
                remota,
                tamano,
                calentarOriginal: remota.Donde == Ubicacion.Primaria,
                ct);
            return variante ?? ComoArchivo(AlEncontrar(remota), esVariante: false);
        }

        return null;
    }

    /// <summary>Ruta dentro del almacén caliente del original, la vista o la miniatura de una foto.</summary>
    public string RutaCaliente(string fileName, CasetaFotoTamano tamano)
    {
        string directorio = tamano switch
        {
            CasetaFotoTamano.Vista => GetHotVistaDirectory(),
            CasetaFotoTamano.Mini => GetHotMiniDirectory(),
            _ => GetHotFotosDirectory()
        };
        return Path.Combine(directorio, Path.GetFileName(fileName));
    }

    /// <summary>Espera hasta que llegue trabajo nuevo (alta o baja) o pase <paramref name="maximo"/>.</summary>
    public async Task EsperarTrabajoAsync(TimeSpan maximo, CancellationToken ct)
    {
        await _hayTrabajo.WaitAsync(maximo, ct);
    }

    public async Task PublishPendingPhotosAsync(CancellationToken ct)
    {
        if (!await _syncLock.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            ProcessDeleteQueue();

            string pending = GetPendingDirectory();
            if (!Directory.Exists(pending))
            {
                return;
            }

            RescatarApartadas(pending);

            string[] files = Directory.GetFiles(pending, "*.jpg").OrderBy(x => x).ToArray();
            if (files.Length == 0)
            {
                return;
            }

            // Cada copia al 23 cuesta varios segundos fijos (se midió ~10 s por foto el 18-nov-2026 sin importar el
            // tamaño): varias a la vez para que una caseta de 10 fotos no tarde minutos en verse en FoxPro.
            await Parallel.ForEachAsync(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = _paralelo, CancellationToken = ct },
                async (file, token) => await PublishOneAsync(file, token));
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Quita de lo caliente lo que ya cumplió su tiempo. Un original solo se quita si el 23 lo tiene; si no, se queda y
    /// se avisa en el log. La vista sigue el reloj de su registro y la miniatura se queda <see cref="_diasMiniatura"/>.
    /// </summary>
    public Task<CasetaLimpiezaResultado> LimpiarCalienteAsync(DateTime ahoraUtc, CancellationToken ct = default)
    {
        return HotCacheActivo
            ? Task.Run(() => LimpiarCaliente(ahoraUtc, ct), ct)
            : Task.FromResult(CasetaLimpiezaResultado.Nada);
    }

    private CasetaLimpiezaResultado LimpiarCaliente(DateTime ahoraUtc, CancellationToken ct)
    {
        DateTime limite = ahoraUtc - _horasCaliente;
        string fotosDir = GetHotFotosDirectory();
        string vistaDir = GetHotVistaDirectory();
        string miniDir = GetHotMiniDirectory();
        string actividadDir = GetActividadDirectory();
        string pendingDir = GetPendingDirectory();

        var actividad = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (string marca in EnumerarArchivos(actividadDir))
        {
            actividad[Path.GetFileName(marca)] = File.GetLastWriteTimeUtc(marca);
        }

        DateTime UltimoMovimiento(string nombre, DateTime modificada)
        {
            return actividad.TryGetValue(ClaveRegistro(nombre), out DateTime marca) && marca > modificada ? marca : modificada;
        }

        int desalojadas = 0, sinCopia = 0, vistas = 0, minis = 0, marcas = 0;

        foreach (string ruta in EnumerarArchivos(fotosDir))
        {
            ct.ThrowIfCancellationRequested();
            string nombre = Path.GetFileName(ruta);
            var info = new FileInfo(ruta);
            if (EsTemporal(nombre))
            {
                if (ahoraUtc - info.LastWriteTimeUtc > TimeSpan.FromDays(1)) TryDelete(ruta);
                continue;
            }

            if (UltimoMovimiento(nombre, info.LastWriteTimeUtc) > limite)
            {
                continue;
            }

            // Hay una versión más nueva esperando su copia: la caliente se queda hasta que esa se publique.
            if (File.Exists(Path.Combine(pendingDir, nombre)))
            {
                continue;
            }

            if (!ConfirmadaEn23(nombre, info))
            {
                sinCopia++;
                if (_avisadasSinCopia.TryAdd(nombre, 0))
                {
                    _logger.LogWarning(
                        "Foto de caseta {FileName} cumplió su tiempo en el almacén caliente pero no está confirmada en el 23: se conserva.",
                        nombre);
                }

                continue;
            }

            if (TryDelete(ruta))
            {
                desalojadas++;
                _avisadasSinCopia.TryRemove(nombre, out _);
            }
        }

        foreach (string ruta in EnumerarArchivos(vistaDir))
        {
            ct.ThrowIfCancellationRequested();
            string nombre = Path.GetFileName(ruta);
            if (File.Exists(Path.Combine(fotosDir, nombre)))
            {
                continue;
            }

            if (UltimoMovimiento(nombre, File.GetLastWriteTimeUtc(ruta)) <= limite && TryDelete(ruta))
            {
                vistas++;
            }
        }

        foreach (string ruta in EnumerarArchivos(miniDir))
        {
            ct.ThrowIfCancellationRequested();
            string nombre = Path.GetFileName(ruta);
            if (File.Exists(Path.Combine(fotosDir, nombre)))
            {
                continue;
            }

            if (ahoraUtc - File.GetLastWriteTimeUtc(ruta) > _diasMiniatura && TryDelete(ruta))
            {
                minis++;
            }
        }

        var registrosCalientes = new HashSet<string>(
            EnumerarArchivos(fotosDir).Concat(EnumerarArchivos(vistaDir)).Select(r => ClaveRegistro(Path.GetFileName(r))),
            StringComparer.OrdinalIgnoreCase);
        foreach (string marca in EnumerarArchivos(actividadDir))
        {
            string clave = Path.GetFileName(marca);
            if (!registrosCalientes.Contains(clave) && File.GetLastWriteTimeUtc(marca) <= limite && TryDelete(marca))
            {
                marcas++;
            }
        }

        return new CasetaLimpiezaResultado(desalojadas, sinCopia, vistas, minis, marcas);
    }

    private async Task PublishOneAsync(string pendingFile, CancellationToken ct)
    {
        string fileName = Path.GetFileName(pendingFile);
        string primaryDirectory = GetPrimaryDirectory();
        string targetFile = Path.Combine(primaryDirectory, fileName);
        string tempTarget = targetFile + ".uploading";
        var reloj = Stopwatch.StartNew();
        long msVariantes = 0, msCopia = 0, msRenombre = 0;

        try
        {
            var origen = new FileInfo(pendingFile);
            if (!origen.Exists)
            {
                return;
            }

            long largo = origen.Length;
            DateTime firma = origen.LastWriteTimeUtc;

            // Primero miniatura y vista: los bytes están aquí, así existen antes de que acabe la copia lenta al 23.
            if (VariantesDisponibles)
            {
                await GenerarVariantesAsync(fileName, pendingFile, firma, calentarOriginal: false, ct);
                msVariantes = reloj.ElapsedMilliseconds;
            }

            Directory.CreateDirectory(primaryDirectory);
            TryDelete(tempTarget);
            await CopiarAsync(pendingFile, tempTarget, firma, ct);
            msCopia = reloj.ElapsedMilliseconds - msVariantes;

            File.Move(tempTarget, targetFile, overwrite: true);
            msRenombre = reloj.ElapsedMilliseconds - msVariantes - msCopia;

            long largoEn23 = new FileInfo(targetFile).Length;
            if (largoEn23 != largo)
            {
                _logger.LogWarning(
                    "La copia de {FileName} en el 23 pesa {Largo23} bytes y la local {LargoLocal}: se reintentará.",
                    fileName,
                    largoEn23,
                    largo);
                return;
            }

            RetirarDePendientes(pendingFile, fileName, largo, firma);
            TryDelete(Path.Combine(GetFallbackDirectory(), fileName));
            _logger.LogInformation(
                "Foto de caseta sincronizada: {FileName} ({Kb} KB) en {Ms} ms (variantes {MsVariantes} ms, copia al 23 {MsCopia} ms, renombre {MsRenombre} ms)",
                fileName,
                largo / 1024,
                reloj.ElapsedMilliseconds,
                msVariantes,
                msCopia,
                msRenombre);

            // Ya está en su lugar definitivo: se avisa para mejorarla en segundo plano (no espera ni puede fallar).
            _mejora?.Encolar(targetFile, "caseta");
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempTarget);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempTarget);
            _logger.LogWarning(ex, "No se pudo sincronizar foto de caseta {FileName}. Se reintentara.", fileName);
        }
    }

    /// <summary>
    /// Saca de pendientes la versión que acaba de confirmarse en el 23 y la deja caliente (o la borra si lo caliente está
    /// apagado). Se aparta con un renombre y se revisa que sea la misma versión que se copió: si mientras tanto llegó una
    /// nueva, esa vuelve a pendientes para el siguiente ciclo en vez de perderse.
    /// </summary>
    private void RetirarDePendientes(string pendingFile, string fileName, long largo, DateTime firma)
    {
        string apartado = $"{pendingFile}.{Guid.NewGuid():N}{ApartadoSufijo}";
        try
        {
            File.Move(pendingFile, apartado);
        }
        catch (FileNotFoundException)
        {
            return;
        }

        var info = new FileInfo(apartado);
        if (info.Length != largo || info.LastWriteTimeUtc != firma)
        {
            try
            {
                File.Move(apartado, pendingFile, overwrite: false);
            }
            catch (IOException) when (File.Exists(pendingFile))
            {
                // Ya hay otra todavía más nueva esperando: esta quedó atrás.
                TryDelete(apartado);
            }

            return;
        }

        if (HotCacheActivo)
        {
            try
            {
                File.Move(apartado, RutaCaliente(fileName, CasetaFotoTamano.Original), overwrite: true);
                TocarActividad(fileName, forzar: false);
                return;
            }
            catch (Exception ex)
            {
                // Está confirmada en el 23: perder la copia caliente solo cuesta leerla de allá.
                _logger.LogDebug(ex, "No se pudo dejar caliente {FileName}.", fileName);
            }
        }

        TryDelete(apartado);
    }

    /// <summary>
    /// Una pendiente apartada que quedó a medias (falló o se cayó el proceso entre el renombre y su destino) vuelve a la
    /// cola si no hay una versión más nueva; si la hay, sobra. Corre al inicio de cada ciclo, con el candado tomado: los
    /// apartados del ciclo anterior ya terminaron, así que cualquiera que quede es un resto.
    /// </summary>
    private void RescatarApartadas(string pending)
    {
        foreach (string apartado in Directory.GetFiles(pending, "*" + ApartadoSufijo))
        {
            try
            {
                // nombre.jpg.<guid>.publicando -> nombre.jpg
                string sinSufijo = apartado[..^ApartadoSufijo.Length];
                string original = sinSufijo[..sinSufijo.LastIndexOf('.')];
                if (File.Exists(original))
                {
                    TryDelete(apartado);
                }
                else
                {
                    File.Move(apartado, original, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo rescatar la foto apartada {Archivo}.", Path.GetFileName(apartado));
            }
        }
    }

    private void ProcessDeleteQueue()
    {
        string deleteQueue = GetDeleteDirectory();
        if (!Directory.Exists(deleteQueue))
        {
            return;
        }

        foreach (string marker in Directory.GetFiles(deleteQueue, "*.delete").OrderBy(x => x))
        {
            string fileName = Path.GetFileNameWithoutExtension(marker);
            try
            {
                TryDelete(Path.Combine(GetPendingDirectory(), fileName));
                TryDelete(Path.Combine(GetFallbackDirectory(), fileName));
                TryDelete(Path.Combine(GetPrimaryDirectory(), fileName));
                BorrarCaliente(fileName);
                File.Delete(marker);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo procesar baja de foto de caseta {FileName}.", fileName);
            }
        }
    }

    // ── Búsqueda ────────────────────────────────────────────────────────────────────────────────────────────────

    private enum Ubicacion
    {
        Pendiente,
        Caliente,
        Respaldo,
        Primaria,
        MiniaturaAnterior
    }

    private sealed record Encontrada(string NombreArchivo, string Ruta, Ubicacion Donde);

    /// <summary>Primero todo lo local (barato) para cualquier nombre posible; solo después se pregunta al 23.</summary>
    private Encontrada? LocalizarOriginal(IReadOnlyList<string> nombres)
    {
        foreach (string nombre in nombres)
        {
            Encontrada? local = LocalizarLocal(nombre);
            if (local != null) return local;
        }

        foreach (string nombre in nombres)
        {
            Encontrada? remota = LocalizarRemota(nombre);
            if (remota != null) return remota;
        }

        return null;
    }

    private Encontrada? LocalizarLocal(string fileName)
    {
        string nombre = Path.GetFileName(fileName);
        if (ExisteBaja(nombre))
        {
            return null;
        }

        string pending = Path.Combine(GetPendingDirectory(), nombre);
        if (File.Exists(pending)) return new Encontrada(nombre, pending, Ubicacion.Pendiente);

        if (HotCacheActivo)
        {
            string caliente = RutaCaliente(nombre, CasetaFotoTamano.Original);
            if (File.Exists(caliente)) return new Encontrada(nombre, caliente, Ubicacion.Caliente);
        }

        string fallback = Path.Combine(GetFallbackDirectory(), nombre);
        return File.Exists(fallback) ? new Encontrada(nombre, fallback, Ubicacion.Respaldo) : null;
    }

    private Encontrada? LocalizarRemota(string fileName)
    {
        string nombre = Path.GetFileName(fileName);
        if (ExisteBaja(nombre))
        {
            return null;
        }

        string primary = Path.Combine(GetPrimaryDirectory(), nombre);
        if (File.Exists(primary)) return new Encontrada(nombre, primary, Ubicacion.Primaria);

        string primaryThumb = Path.Combine(GetPrimaryDirectory(), "thumbnails", nombre);
        if (File.Exists(primaryThumb)) return new Encontrada(nombre, primaryThumb, Ubicacion.MiniaturaAnterior);

        string fallbackThumb = Path.Combine(GetFallbackDirectory(), "thumbnails", nombre);
        return File.Exists(fallbackThumb) ? new Encontrada(nombre, fallbackThumb, Ubicacion.MiniaturaAnterior) : null;
    }

    /// <summary>Lo que pasa cada vez que alguien lee una foto: lo caliente renueva su reloj y lo del 23 se calienta.</summary>
    private string AlEncontrar(Encontrada foto)
    {
        if (foto.Donde == Ubicacion.Caliente)
        {
            TocarActividad(foto.NombreArchivo, forzar: false);
        }
        else if (foto.Donde == Ubicacion.Primaria)
        {
            ProgramarCalentado(foto.NombreArchivo, foto.Ruta);
        }

        return foto.Ruta;
    }

    private static CasetaFotoArchivo ComoArchivo(string ruta, bool esVariante)
    {
        return new CasetaFotoArchivo(ruta, File.GetLastWriteTimeUtc(ruta), esVariante);
    }

    // ── Miniatura y vista ───────────────────────────────────────────────────────────────────────────────────────

    private async Task<CasetaFotoArchivo?> VarianteDeAsync(
        Encontrada origen,
        CasetaFotoTamano tamano,
        bool calentarOriginal,
        CancellationToken ct)
    {
        DateTime firma;
        try
        {
            firma = File.GetLastWriteTimeUtc(origen.Ruta);
        }
        catch (Exception)
        {
            return null;
        }

        string destino = RutaCaliente(origen.NombreArchivo, tamano);
        if (VarianteVigente(destino, firma) || await GenerarVariantesAsync(origen.NombreArchivo, origen.Ruta, firma, calentarOriginal, ct))
        {
            return VarianteVigente(destino, firma) ? new CasetaFotoArchivo(destino, firma, EsVariante: true) : null;
        }

        return null;
    }

    /// <summary>
    /// Genera miniatura y vista de una versión (la identifica <paramref name="firma"/>, su fecha de modificación). Dos
    /// peticiones a la vez por la misma foto comparten el mismo trabajo: el 23 se lee una sola vez.
    /// </summary>
    private async Task<bool> GenerarVariantesAsync(
        string nombre,
        string rutaOrigen,
        DateTime firma,
        bool calentarOriginal,
        CancellationToken ct)
    {
        if (VarianteVigente(RutaCaliente(nombre, CasetaFotoTamano.Mini), firma)
            && VarianteVigente(RutaCaliente(nombre, CasetaFotoTamano.Vista), firma))
        {
            return true;
        }

        string clave = $"{nombre}|{firma.Ticks}";
        Lazy<Task<bool>>? nuevo = null;
        nuevo = new Lazy<Task<bool>>(() =>
        {
            Task<bool> tarea = GenerarVariantesCoreAsync(nombre, rutaOrigen, firma, calentarOriginal);
            tarea.ContinueWith(
                _ => _variantesEnCurso.TryRemove(KeyValuePair.Create(clave, nuevo!)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return tarea;
        });

        Lazy<Task<bool>> enCurso = _variantesEnCurso.GetOrAdd(clave, nuevo);
        // Sin el token de esta petición dentro del trabajo compartido: si una se cancela, las demás siguen esperando.
        return await enCurso.Value.WaitAsync(ct);
    }

    private async Task<bool> GenerarVariantesCoreAsync(string nombre, string rutaOrigen, DateTime firma, bool calentarOriginal)
    {
        try
        {
            // La única lectura del original (lenta si viene del 23). Fuera del tope de CPU para no bloquear a las demás.
            byte[] bytes = await File.ReadAllBytesAsync(rutaOrigen);
            if (calentarOriginal)
            {
                GuardarOriginalCaliente(nombre, rutaOrigen, bytes, firma);
            }

            FotoVariantesResultado? variantes;
            await _cpuVariantes.WaitAsync();
            try
            {
                variantes = FotoVariantes.Generar(bytes);
            }
            finally
            {
                _cpuVariantes.Release();
            }

            if (variantes == null)
            {
                _logger.LogWarning("No se pudo leer la foto de caseta {FileName} para hacer su miniatura; se servirá el original.", nombre);
                return false;
            }

            // Si se borró mientras tanto, no se dejan variantes huérfanas.
            if (ExisteBaja(nombre))
            {
                return false;
            }

            EscribirVariante(RutaCaliente(nombre, CasetaFotoTamano.Vista), variantes.Vista, firma);
            EscribirVariante(RutaCaliente(nombre, CasetaFotoTamano.Mini), variantes.Mini, firma);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudieron preparar miniatura y vista de la foto de caseta {FileName}.", nombre);
            return false;
        }
    }

    /// <summary>Una variante vale si es de la misma versión que su original: ambas llevan la misma fecha de modificación.</summary>
    private static bool VarianteVigente(string ruta, DateTime firma)
    {
        try
        {
            return File.Exists(ruta) && File.GetLastWriteTimeUtc(ruta) == firma;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void EscribirVariante(string ruta, byte[] bytes, DateTime firma)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        string temporal = $"{ruta}.{Guid.NewGuid():N}{TempSufijo}";
        File.WriteAllBytes(temporal, bytes);
        File.SetLastWriteTimeUtc(temporal, firma);
        File.Move(temporal, ruta, overwrite: true);
    }

    private void InvalidarVariantes(string fileName)
    {
        TryDelete(RutaCaliente(fileName, CasetaFotoTamano.Vista));
        TryDelete(RutaCaliente(fileName, CasetaFotoTamano.Mini));
    }

    private void BorrarCaliente(string fileName)
    {
        TryDelete(RutaCaliente(fileName, CasetaFotoTamano.Original));
        InvalidarVariantes(fileName);
    }

    // ── Calentar lo que solo está en el 23 ──────────────────────────────────────────────────────────────────────

    /// <summary>Copia en segundo plano una foto que se leyó del 23 para que la siguiente lectura sea local.</summary>
    private void ProgramarCalentado(string nombre, string rutaEn23)
    {
        if (!HotCacheActivo || ExisteBaja(nombre) || File.Exists(RutaCaliente(nombre, CasetaFotoTamano.Original)))
        {
            return;
        }

        if (!_calentando.TryAdd(nombre, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(rutaEn23))
                {
                    return;
                }

                DateTime firma = File.GetLastWriteTimeUtc(rutaEn23);
                string destino = RutaCaliente(nombre, CasetaFotoTamano.Original);
                string temporal = $"{destino}.{Guid.NewGuid():N}{TempSufijo}";
                await CopiarAsync(rutaEn23, temporal, firma, CancellationToken.None);
                if (!InstalarCaliente(nombre, rutaEn23, temporal, destino, firma))
                {
                    return;
                }

                if (VariantesDisponibles)
                {
                    await GenerarVariantesAsync(nombre, destino, firma, calentarOriginal: false, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No se pudo calentar la foto de caseta {FileName}.", nombre);
            }
            finally
            {
                _calentando.TryRemove(nombre, out _);
            }
        });
    }

    private void GuardarOriginalCaliente(string nombre, string rutaEn23, byte[] bytes, DateTime firma)
    {
        if (!HotCacheActivo || ExisteBaja(nombre))
        {
            return;
        }

        string destino = RutaCaliente(nombre, CasetaFotoTamano.Original);
        if (File.Exists(destino))
        {
            return;
        }

        try
        {
            string temporal = $"{destino}.{Guid.NewGuid():N}{TempSufijo}";
            File.WriteAllBytes(temporal, bytes);
            File.SetLastWriteTimeUtc(temporal, firma);
            InstalarCaliente(nombre, rutaEn23, temporal, destino, firma);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo calentar la foto de caseta {FileName}.", nombre);
        }
    }

    /// <summary>
    /// Deja la copia en su lugar solo si la foto sigue viva y sin cambios en el 23 (una baja o un reemplazo mientras se
    /// leía no deben resucitar una versión vieja) y si nadie dejó ya otra versión caliente.
    /// </summary>
    private bool InstalarCaliente(string nombre, string rutaEn23, string temporal, string destino, DateTime firma)
    {
        if (ExisteBaja(nombre) || !File.Exists(rutaEn23) || File.GetLastWriteTimeUtc(rutaEn23) != firma)
        {
            TryDelete(temporal);
            return false;
        }

        try
        {
            File.Move(temporal, destino, overwrite: false);
        }
        catch (IOException)
        {
            TryDelete(temporal);
            return false;
        }

        TocarActividad(nombre, forzar: true);
        return true;
    }

    // ── Reloj de cada registro ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Marca que el registro de esta foto se movió (alta, baja, reemplazo o consulta). Una consulta solo renueva la marca
    /// si tiene más de una hora, para no escribir en disco con cada miniatura que se pinta.
    /// </summary>
    private void TocarActividad(string fileName, bool forzar)
    {
        if (!HotCacheActivo)
        {
            return;
        }

        try
        {
            string marca = Path.Combine(GetActividadDirectory(), ClaveRegistro(fileName));
            DateTime ahora = DateTime.UtcNow;
            if (File.Exists(marca))
            {
                if (!forzar && ahora - File.GetLastWriteTimeUtc(marca) < ActividadMinima)
                {
                    return;
                }

                File.SetLastWriteTimeUtc(marca, ahora);
            }
            else
            {
                File.WriteAllBytes(marca, Array.Empty<byte>());
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo marcar actividad de {FileName}.", fileName);
        }
    }

    /// <summary>"E-021107-26-0003.jpg" → "E-021107-26"; "01I26106450-0001.jpg" → "01I26106450".</summary>
    internal static string ClaveRegistro(string fileName)
    {
        string sinExtension = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName));
        int guion = sinExtension.LastIndexOf('-');
        return guion > 0 ? sinExtension[..guion] : sinExtension;
    }

    /// <summary>
    /// El 23 la tiene: existe y pesa lo mismo. Si pesa distinto pero es más nueva que la caliente, es que la mejora de
    /// fotos la reemplazó allá (la caliente sería la versión sin mejorar) y también cuenta como confirmada.
    /// </summary>
    private bool ConfirmadaEn23(string nombre, FileInfo caliente)
    {
        try
        {
            var en23 = new FileInfo(Path.Combine(GetPrimaryDirectory(), nombre));
            return en23.Exists
                && en23.Length > 0
                && (en23.Length == caliente.Length || en23.LastWriteTimeUtc > caliente.LastWriteTimeUtc);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── Utilidades ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Copia conservando la fecha de modificación: es la que identifica la versión en todos los lugares.</summary>
    private static async Task CopiarAsync(string origen, string destino, DateTime firmaUtc, CancellationToken ct)
    {
        const int Bufer = 1 << 20;
        await using (var fuente = new FileStream(
            origen,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            Bufer,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var copia = new FileStream(
            destino,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            Bufer,
            FileOptions.Asynchronous))
        {
            await fuente.CopyToAsync(copia, Bufer, ct);
        }

        File.SetLastWriteTimeUtc(destino, firmaUtc);
    }

    private bool ExisteBaja(string fileName)
    {
        return File.Exists(Path.Combine(GetDeleteDirectory(), Path.GetFileName(fileName) + ".delete"));
    }

    private static bool EsTemporal(string fileName)
    {
        return fileName.EndsWith(TempSufijo, StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(ApartadoSufijo, StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".uploading", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerarArchivos(string directorio)
    {
        return Directory.Exists(directorio) ? Directory.GetFiles(directorio) : Array.Empty<string>();
    }

    private void AvisarTrabajo()
    {
        try
        {
            if (_hayTrabajo.CurrentCount == 0)
            {
                _hayTrabajo.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static void ConReintentos(Action accion)
    {
        for (int intento = 1; ; intento++)
        {
            try
            {
                accion();
                return;
            }
            catch (IOException) when (intento < 4)
            {
                Thread.Sleep(150 * intento);
            }
        }
    }

    private bool ProbarCaliente()
    {
        try
        {
            foreach (string directorio in new[] { GetHotFotosDirectory(), GetHotVistaDirectory(), GetHotMiniDirectory(), GetActividadDirectory() })
            {
                Directory.CreateDirectory(directorio);
            }

            string prueba = Path.Combine(_hotRoot, $".prueba-{Guid.NewGuid():N}");
            File.WriteAllText(prueba, "ok");
            File.Delete(prueba);
            _logger.LogInformation(
                "Almacén caliente de fotos de Caseta activo en {Ruta}: {Horas} h desde el último movimiento, miniaturas {Dias} días, copia al 23 de {Paralelo} en {Paralelo}.",
                _hotRoot,
                _horasCaliente.TotalHours,
                _diasMiniatura.TotalDays,
                _paralelo,
                _paralelo);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Almacén caliente de fotos de Caseta APAGADO: no se puede escribir en {Ruta}. Las fotos se sirven como antes, del 23. Configura CasetaPhotoSettings:HotPath con una carpeta escribible.",
                _hotRoot);
            return false;
        }
    }

    private string GetHotFotosDirectory() => Path.Combine(_hotRoot, "fotos");

    private string GetHotVistaDirectory() => Path.Combine(_hotRoot, "vista");

    private string GetHotMiniDirectory() => Path.Combine(_hotRoot, "mini");

    private string GetActividadDirectory() => Path.Combine(_hotRoot, "actividad");

    private string GetPrimaryDirectory()
    {
        string? explicitPath = _config["CasetaPhotoSettings:BasePath"] ?? _config["PhotoSettings:CasetaBasePath"];
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        string? photoBase = _config["PhotoSettings:BasePath"];
        if (!string.IsNullOrWhiteSpace(photoBase))
        {
            string normalized = photoBase.TrimEnd('\\', '/');
            return normalized.EndsWith("Caseta", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : Path.Combine(normalized, "Caseta");
        }

        return @"\\192.168.10.23\Programas\Trafico\fotos\Caseta";
    }

    private string GetFallbackDirectory()
    {
        return _config["CasetaPhotoSettings:FallbackPath"]
            ?? @"C:\inetpub\wwwroot\PMMCaseta\Content\TempFotos";
    }

    private string GetQueueRoot()
    {
        return _config["CasetaPhotoSettings:QueuePath"]
            ?? Path.Combine(GetFallbackDirectory(), "CasetaQueue");
    }

    private string GetPendingDirectory()
    {
        return Path.Combine(GetQueueRoot(), "pending");
    }

    private string GetStagingDirectory()
    {
        return Path.Combine(GetQueueRoot(), "staging");
    }

    private string GetDeleteDirectory()
    {
        return Path.Combine(GetQueueRoot(), "delete");
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static string NormalizeExtension(string? extension)
    {
        string clean = (extension ?? "jpg").Trim().TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(clean) ? "jpg" : clean;
    }
}
