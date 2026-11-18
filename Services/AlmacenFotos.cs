using System.Collections.Concurrent;
using System.Diagnostics;

namespace PMMovil.Api.Services;

/// <summary>Foto recibida y escrita en la preparación local, lista para activarse en cuanto la base de datos confirme.</summary>
public sealed record FotoPreparada(string FileName, string StagedPath);

/// <summary>Tamaño en que se sirve una foto.</summary>
public enum FotoTamano
{
    Original,

    /// <summary>Para verla en pantalla: lado mayor de <see cref="FotoVariantes.VistaLado"/> px.</summary>
    Vista,

    /// <summary>Para cuadrículas y tiras: lado mayor de <see cref="FotoVariantes.MiniLado"/> px.</summary>
    Mini
}

/// <summary>Archivo listo para servirse. <see cref="ModificadaUtc"/> identifica la versión de la foto.</summary>
public sealed record FotoArchivo(string Ruta, DateTime ModificadaUtc, bool EsVariante);

/// <summary>Lo que hizo una pasada de limpieza del almacén caliente.</summary>
public sealed record LimpiezaFotosResultado(
    int Desalojadas,
    int SinCopiaEn23,
    int VistasBorradas,
    int MiniaturasBorradas,
    int MarcasBorradas)
{
    public static readonly LimpiezaFotosResultado Nada = new(0, 0, 0, 0, 0);

    public bool HuboCambios => Desalojadas + SinCopiaEn23 + VistasBorradas + MiniaturasBorradas + MarcasBorradas > 0;
}

/// <summary>Cómo se arma un almacén de fotos: dónde vive cada cosa y cuánto se queda lo caliente.</summary>
public sealed record AlmacenFotosOpciones
{
    /// <summary>Para el registro del API: "Caseta", "Entrada y Revisión".</summary>
    public required string Nombre { get; init; }

    /// <summary>Sección de appsettings de donde salió (se menciona en los avisos del registro).</summary>
    public required string SeccionConfig { get; init; }

    public required string RaizCaliente { get; init; }

    /// <summary>Raíz de la cola local: <c>staging</c>, <c>pending</c> y <c>delete</c>.</summary>
    public required string RaizCola { get; init; }

    /// <summary>Carpeta local antigua donde también pueden estar fotos (solo Caseta: la de la web anterior).</summary>
    public string? CarpetaRespaldo { get; init; }

    /// <summary>Carpetas con miniaturas de una versión anterior, para fotos que ya no tienen original (solo Caseta).</summary>
    public IReadOnlyList<string> CarpetasMiniaturaAnterior { get; init; } = Array.Empty<string>();

    public bool CalienteHabilitado { get; init; } = true;

    public TimeSpan HorasCaliente { get; init; } = TimeSpan.FromHours(48);

    public TimeSpan DiasMiniatura { get; init; } = TimeSpan.FromDays(365);

    public int Paralelo { get; init; } = 3;

    /// <summary>
    /// Una baja borra también la copia del 23 en el acto (como siempre lo hicieron Entrada y Revisión) y la marca de baja
    /// solo se queda mientras una copia de esa misma versión siga en camino al 23. Así otra foto que alguien deje después
    /// en esa misma ruta del 23 (FoxPro o la captura de etiquetas, que reusan el consecutivo) no se borra por error.
    /// </summary>
    public bool BorrarEn23AlMomento { get; init; }

    /// <summary>
    /// Lo que comparten todos los almacenes en su sección: <c>HotCacheEnabled</c>, <c>HotPath</c>, <c>HotHours</c>,
    /// <c>ThumbnailDays</c> y <c>PublishParallelism</c>. El resto lo pone cada almacén.
    /// </summary>
    public static AlmacenFotosOpciones Leer(
        IConfiguration config,
        string seccion,
        string nombre,
        string carpetaCaliente,
        string raizCola,
        bool habilitado = true)
    {
        return new AlmacenFotosOpciones
        {
            Nombre = nombre,
            SeccionConfig = seccion,
            RaizCola = raizCola,
            CalienteHabilitado = habilitado && (config.GetValue<bool?>($"{seccion}:HotCacheEnabled") ?? true),
            RaizCaliente = config[$"{seccion}:HotPath"] is { Length: > 0 } hotPath
                ? hotPath
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "PMMovil",
                    "FotosCaliente",
                    carpetaCaliente),
            HorasCaliente = TimeSpan.FromHours(Math.Clamp(config.GetValue<int?>($"{seccion}:HotHours") ?? 48, 1, 24 * 30)),
            DiasMiniatura = TimeSpan.FromDays(Math.Clamp(config.GetValue<int?>($"{seccion}:ThumbnailDays") ?? 365, 1, 3650)),
            Paralelo = Math.Clamp(config.GetValue<int?>($"{seccion}:PublishParallelism") ?? 3, 1, 8)
        };
    }
}

/// <summary>
/// Dónde viven las fotos que suben las tabletas y cómo se mueven. Lo usan Caseta (<see cref="CasetaPhotoStorage"/>) y las
/// fotos que viven en la carpeta del cliente: Entrada Bodega y Revisión (<see cref="ClientePhotoStorage"/>).
///
/// El API corre en Azure y el destino definitivo de las fotos es el servidor de archivos de la oficina (el "23",
/// <c>\\192.168.10.23\...\Fotos</c>), al otro lado del túnel: cada foto que se lee o escribe allá cruza el internet de la
/// oficina. Por eso hay tres lugares:
///
/// <list type="number">
/// <item><b>Pendientes</b> (cola local): lo que llegó y todavía no está en el 23. Se copia al 23 de inmediato, en segundo
/// plano y varias a la vez.</item>
/// <item><b>Caliente</b> (disco del servidor del API): la copia que ya se confirmó en el 23 se queda aquí
/// <see cref="AlmacenFotosOpciones.HorasCaliente"/> horas desde el último movimiento de su registro (alta, baja, reemplazo
/// o consulta), porque casi todo lo que se corrige se corrige el mismo día. Aquí viven también la miniatura (se queda
/// <see cref="AlmacenFotosOpciones.DiasMiniatura"/> días: pesa ~14 KB) y la vista para pantalla.</item>
/// <item><b>El 23</b>: siempre tiene todo. Una consulta a un registro viejo lo lee de ahí una vez y lo vuelve a calentar.</item>
/// </list>
///
/// Reglas que no se rompen: nada se quita de lo caliente si no está confirmado en el 23; una baja quita lo caliente en el
/// acto; una versión nueva invalida su vista y su miniatura. Todo lector pasa por aquí (<see cref="BuscarOriginal"/>,
/// <see cref="FindReadablePhotoPath(string)"/> y <see cref="ResolverAsync(IReadOnlyList{string}, FotoTamano, CancellationToken)"/>),
/// así nadie lee del 23 por su cuenta una foto que aún se está copiando.
///
/// Cada foto se identifica con un nombre plano (sin carpetas) que es el mismo en la cola y en lo caliente; cada almacén
/// dice dónde vive ese nombre en el 23 (<see cref="RutaEn23"/>).
/// </summary>
public abstract class AlmacenFotos
{
    private const string TempSufijo = ".tmp";
    private const string ApartadoSufijo = ".publicando";
    private const string FirmaPrefijo = "v1|";
    private static readonly TimeSpan ActividadMinima = TimeSpan.FromHours(1);

    private readonly AlmacenFotosOpciones _opciones;
    private readonly ILogger _logger;
    private readonly IFotoMejoraCola? _mejora;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    // Señal para que el trabajador publique ya, sin esperar su intervalo.
    private readonly SemaphoreSlim _hayTrabajo = new(0, 1);

    // Tope de CPU para generar miniaturas y vistas (decodificar una foto de 5 MP cuesta ~100-300 ms).
    private readonly SemaphoreSlim _cpuVariantes = new(2, 2);
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _variantesEnCurso = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _calentando = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _avisadasSinCopia = new(StringComparer.OrdinalIgnoreCase);

    // Fotos que en este momento se están copiando al 23 (para saber si una baja todavía puede alcanzarlas).
    private readonly ConcurrentDictionary<string, byte> _publicando = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lazy<bool> _hotDisponible;

    protected AlmacenFotos(AlmacenFotosOpciones opciones, ILogger logger, IFotoMejoraCola? mejora)
    {
        _opciones = opciones;
        _logger = logger;
        _mejora = mejora;
        _hotDisponible = new Lazy<bool>(ProbarCaliente, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>True si el almacén caliente está encendido y su carpeta se puede escribir.</summary>
    public bool HotCacheActivo => _opciones.CalienteHabilitado && CalientePermitido && _hotDisponible.Value;

    /// <summary>Un almacén puede apagar lo caliente por su cuenta (Entrada y Revisión cuando no tienen cola local).</summary>
    protected virtual bool CalientePermitido => true;

    private volatile bool _trabajadorActivo;

    /// <summary>
    /// Lo llama su trabajador al arrancar: desde ahí hay quien copie la cola al 23. Sin trabajador (DisableBackgroundWorkers
    /// o los modos de línea de comandos, que no arrancan el host) lo que entra a la cola no saldría nunca.
    /// </summary>
    public void MarcarTrabajadorActivo() => _trabajadorActivo = true;

    protected bool TrabajadorActivo => _trabajadorActivo;

    /// <summary>Miniatura y vista solo existen con almacén caliente y en Windows (System.Drawing).</summary>
    public bool VariantesDisponibles => HotCacheActivo && OperatingSystem.IsWindows();

    /// <summary>Ruta completa en el 23 de la foto con este nombre plano.</summary>
    protected abstract string RutaEn23(string nombre);

    /// <summary>Carpeta del 23 que debe verse siempre: si no se ve, el 23 no está contestando (no es que falte la foto).</summary>
    protected abstract string Raiz23 { get; }

    /// <summary>Etiqueta para la mejora de fotos (solo aparece en su registro).</summary>
    protected virtual string OrigenMejora(string nombre) => _opciones.Nombre;

    public async Task<FotoPreparada> StageAsync(byte[] data, string fileName, CancellationToken ct)
    {
        string safeFileName = Path.GetFileName(fileName);
        string staging = GetStagingDirectory();
        Directory.CreateDirectory(staging);

        string stagedFile = Path.Combine(staging, $"{Guid.NewGuid():N}-{safeFileName}");
        string tempFile = stagedFile + TempSufijo;

        await File.WriteAllBytesAsync(tempFile, data, ct);
        File.Move(tempFile, stagedFile, overwrite: true);

        return new FotoPreparada(safeFileName, stagedFile);
    }

    public void ActivateStagedPhotos(IEnumerable<FotoPreparada> photos)
    {
        string pending = GetPendingDirectory();
        Directory.CreateDirectory(pending);
        bool alguna = false;

        foreach (FotoPreparada photo in photos)
        {
            string nombre = Path.GetFileName(photo.FileName);

            // Una foto nueva con el mismo nombre reemplaza una baja pendiente de ese nombre.
            TryDelete(RutaMarca(nombre));

            string target = Path.Combine(pending, nombre);
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
            InvalidarVariantes(nombre);
            TocarActividad(nombre, forzar: true);
            alguna = true;
        }

        if (alguna)
        {
            AvisarTrabajo();
        }
    }

    public void CleanupStagedPhotos(IEnumerable<FotoPreparada> photos)
    {
        foreach (FotoPreparada photo in photos)
        {
            TryDelete(photo.StagedPath);
        }
    }

    public void QueueDelete(string fileName)
    {
        string nombre = Path.GetFileName(fileName);
        Directory.CreateDirectory(GetDeleteDirectory());

        // Primero la marca: desde aquí ningún lector ni calentado la vuelve a servir, aunque el 23 aún la tenga. Si se
        // borra en el acto, la marca lleva la versión local que se está borrando: así solo alcanza a una copia de ESA versión
        // que siga en camino al 23, nunca a otra foto que alguien deje después en la misma ruta.
        string marca = RutaMarca(nombre);
        File.WriteAllText(marca, _opciones.BorrarEn23AlMomento
            ? FirmaLocal(nombre) ?? string.Empty
            : DateTimeOffset.Now.ToString("O"));

        TryDelete(Path.Combine(GetPendingDirectory(), nombre));
        BorrarCaliente(nombre);
        TocarActividad(nombre, forzar: true);

        if (_opciones.BorrarEn23AlMomento
            && QuitarDel23(nombre, soloSiEs: null)
            && !_publicando.ContainsKey(nombre))
        {
            // Ya no está en el 23 y nada va en camino: la marca no tiene nada que alcanzar.
            TryDelete(marca);
            return;
        }

        AvisarTrabajo();
    }

    public string? FindReadablePhotoPath(string fileName)
    {
        string safeFileName = Path.GetFileName(fileName);
        Encontrada? foto = LocalizarLocal(safeFileName) ?? LocalizarRemota(safeFileName);
        return foto == null ? null : AlEncontrar(foto);
    }

    /// <summary>
    /// Ruta del original para quien necesita el archivo completo (enlaces, PDF, ZIP). Prueba cada nombre posible, primero
    /// en lo local (barato) y después en el 23.
    /// </summary>
    public string? BuscarOriginal(IReadOnlyList<string> nombres)
    {
        Encontrada? foto = LocalizarOriginal(nombres);
        return foto == null ? null : AlEncontrar(foto);
    }

    /// <summary>
    /// Archivo a servir para una foto en el tamaño pedido. La miniatura y la vista se generan al vuelo la primera vez
    /// (de lo local si existe, del 23 si no) y se guardan en lo caliente. Si no se pueden generar se sirve el original:
    /// la foto siempre se ve.
    /// </summary>
    public async Task<FotoArchivo?> ResolverAsync(
        IReadOnlyList<string> nombres,
        FotoTamano tamano,
        CancellationToken ct = default)
    {
        if (tamano == FotoTamano.Original || !VariantesDisponibles)
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

            FotoArchivo? variante = await VarianteDeAsync(
                remota,
                tamano,
                calentarOriginal: remota.Donde == Ubicacion.Primaria,
                ct);
            return variante ?? ComoArchivo(AlEncontrar(remota), esVariante: false);
        }

        return null;
    }

    /// <summary>Ruta dentro del almacén caliente del original, la vista o la miniatura de una foto.</summary>
    public string RutaCaliente(string fileName, FotoTamano tamano)
    {
        string directorio = tamano switch
        {
            FotoTamano.Vista => GetHotVistaDirectory(),
            FotoTamano.Mini => GetHotMiniDirectory(),
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

            string[] files = Directory.GetFiles(pending)
                .Where(f => !EsTemporal(Path.GetFileName(f)))
                .OrderBy(x => x)
                .ToArray();
            if (files.Length == 0)
            {
                return;
            }

            // Cada copia al 23 cuesta varios segundos fijos (se midió ~10 s por foto el 18-nov-2026 sin importar el
            // tamaño): varias a la vez para que un registro de 10 fotos no tarde minutos en verse en FoxPro.
            await Parallel.ForEachAsync(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = _opciones.Paralelo, CancellationToken = ct },
                async (file, token) => await PublishOneAsync(file, token));
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Quita de lo caliente lo que ya cumplió su tiempo. Un original solo se quita si el 23 lo tiene; si no, se queda y
    /// se avisa en el log. La vista sigue el reloj de su registro y la miniatura se queda
    /// <see cref="AlmacenFotosOpciones.DiasMiniatura"/>.
    /// </summary>
    public Task<LimpiezaFotosResultado> LimpiarCalienteAsync(DateTime ahoraUtc, CancellationToken ct = default)
    {
        return HotCacheActivo
            ? Task.Run(() => LimpiarCaliente(ahoraUtc, ct), ct)
            : Task.FromResult(LimpiezaFotosResultado.Nada);
    }

    private LimpiezaFotosResultado LimpiarCaliente(DateTime ahoraUtc, CancellationToken ct)
    {
        DateTime limite = ahoraUtc - _opciones.HorasCaliente;
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
                        "Foto {FileName} ({Almacen}) cumplió su tiempo en el almacén caliente pero no está confirmada en el 23: se conserva.",
                        nombre,
                        _opciones.Nombre);
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

            if (ahoraUtc - File.GetLastWriteTimeUtc(ruta) > _opciones.DiasMiniatura && TryDelete(ruta))
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

        return new LimpiezaFotosResultado(desalojadas, sinCopia, vistas, minis, marcas);
    }

    private async Task PublishOneAsync(string pendingFile, CancellationToken ct)
    {
        string fileName = Path.GetFileName(pendingFile);
        string targetFile = RutaEn23(fileName);
        string tempTarget = targetFile + ".uploading";
        var reloj = Stopwatch.StartNew();
        long msVariantes = 0, msCopia = 0, msRenombre = 0;

        _publicando.TryAdd(fileName, 0);
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

            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
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

            if (ExisteBaja(fileName))
            {
                // Se borró mientras viajaba al 23: lo que acaba de llegar es exactamente esta versión y sobra.
                QuitarDel23(fileName, soloSiEs: (largo, firma.Ticks));
                AvisarTrabajo();
                return;
            }

            RetirarDePendientes(pendingFile, fileName, largo, firma);
            if (_opciones.CarpetaRespaldo != null)
            {
                TryDelete(Path.Combine(_opciones.CarpetaRespaldo, fileName));
            }

            _logger.LogInformation(
                "Foto de {Almacen} sincronizada: {FileName} ({Kb} KB) en {Ms} ms (variantes {MsVariantes} ms, copia al 23 {MsCopia} ms, renombre {MsRenombre} ms)",
                _opciones.Nombre,
                fileName,
                largo / 1024,
                reloj.ElapsedMilliseconds,
                msVariantes,
                msCopia,
                msRenombre);

            // Ya está en su lugar definitivo: se avisa para mejorarla en segundo plano (no espera ni puede fallar).
            _mejora?.Encolar(targetFile, OrigenMejora(fileName));
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempTarget);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempTarget);
            _logger.LogWarning(ex, "No se pudo sincronizar la foto {FileName} ({Almacen}). Se reintentara.", fileName, _opciones.Nombre);
        }
        finally
        {
            _publicando.TryRemove(fileName, out _);
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
                File.Move(apartado, RutaCaliente(fileName, FotoTamano.Original), overwrite: true);
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
                if (_opciones.CarpetaRespaldo != null)
                {
                    TryDelete(Path.Combine(_opciones.CarpetaRespaldo, fileName));
                }

                BorrarCaliente(fileName);

                // Si el 23 no contesta, la marca se queda y se reintenta en el siguiente ciclo.
                if (QuitarDel23(fileName, LeerFirma(File.ReadAllText(marker))))
                {
                    File.Delete(marker);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo procesar la baja de la foto {FileName} ({Almacen}).", fileName, _opciones.Nombre);
            }
        }
    }

    /// <summary>
    /// Quita la foto del 23. Con <paramref name="soloSiEs"/> solo si lo que hay allá es esa versión (tamaño y fecha): otra
    /// foto que alguien haya dejado después en esa ruta no es la que se borró. True si allá ya no está la que se borró;
    /// false si no se pudo saber o borrar (el 23 no contesta) y hay que reintentar.
    /// </summary>
    private bool QuitarDel23(string nombre, (long Largo, long Ticks)? soloSiEs)
    {
        string ruta = RutaEn23(nombre);
        try
        {
            var info = new FileInfo(ruta);
            if (info.Exists)
            {
                if (soloSiEs is { } firma && (info.Length != firma.Largo || info.LastWriteTimeUtc.Ticks != firma.Ticks))
                {
                    return true;
                }

                File.Delete(ruta);
                return true;
            }

            // No está: ¿de verdad no está o el 23 no contesta? Si ni su raíz se ve, se reintenta después.
            return Directory.Exists(Raiz23);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo quitar {FileName} del 23.", nombre);
            return false;
        }
    }

    /// <summary>Versión (tamaño y fecha) de la copia local de una foto, la pendiente o si no la caliente.</summary>
    private string? FirmaLocal(string nombre)
    {
        foreach (string ruta in new[] { Path.Combine(GetPendingDirectory(), nombre), RutaCaliente(nombre, FotoTamano.Original) })
        {
            try
            {
                var info = new FileInfo(ruta);
                if (info.Exists)
                {
                    return $"{FirmaPrefijo}{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                }
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    private static (long Largo, long Ticks)? LeerFirma(string contenido)
    {
        if (!contenido.StartsWith(FirmaPrefijo, StringComparison.Ordinal))
        {
            return null;
        }

        string[] partes = contenido[FirmaPrefijo.Length..].Trim().Split('|');
        return partes.Length == 2 && long.TryParse(partes[0], out long largo) && long.TryParse(partes[1], out long ticks)
            ? (largo, ticks)
            : null;
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
            string caliente = RutaCaliente(nombre, FotoTamano.Original);
            if (File.Exists(caliente)) return new Encontrada(nombre, caliente, Ubicacion.Caliente);
        }

        if (_opciones.CarpetaRespaldo == null)
        {
            return null;
        }

        string fallback = Path.Combine(_opciones.CarpetaRespaldo, nombre);
        return File.Exists(fallback) ? new Encontrada(nombre, fallback, Ubicacion.Respaldo) : null;
    }

    private Encontrada? LocalizarRemota(string fileName)
    {
        string nombre = Path.GetFileName(fileName);
        if (ExisteBaja(nombre))
        {
            return null;
        }

        string primary = RutaEn23(nombre);
        if (File.Exists(primary)) return new Encontrada(nombre, primary, Ubicacion.Primaria);

        foreach (string carpeta in _opciones.CarpetasMiniaturaAnterior)
        {
            string miniatura = Path.Combine(carpeta, nombre);
            if (File.Exists(miniatura)) return new Encontrada(nombre, miniatura, Ubicacion.MiniaturaAnterior);
        }

        return null;
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

    private static FotoArchivo ComoArchivo(string ruta, bool esVariante)
    {
        return new FotoArchivo(ruta, File.GetLastWriteTimeUtc(ruta), esVariante);
    }

    // ── Miniatura y vista ───────────────────────────────────────────────────────────────────────────────────────

    private async Task<FotoArchivo?> VarianteDeAsync(
        Encontrada origen,
        FotoTamano tamano,
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
            return VarianteVigente(destino, firma) ? new FotoArchivo(destino, firma, EsVariante: true) : null;
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
        if (VarianteVigente(RutaCaliente(nombre, FotoTamano.Mini), firma)
            && VarianteVigente(RutaCaliente(nombre, FotoTamano.Vista), firma))
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
                _logger.LogWarning("No se pudo leer la foto {FileName} ({Almacen}) para hacer su miniatura; se servirá el original.", nombre, _opciones.Nombre);
                return false;
            }

            // Si se borró mientras tanto (o desapareció del 23 mientras se leía), no se dejan variantes huérfanas.
            if (ExisteBaja(nombre) || (calentarOriginal && !File.Exists(rutaOrigen)))
            {
                return false;
            }

            EscribirVariante(RutaCaliente(nombre, FotoTamano.Vista), variantes.Vista, firma);
            EscribirVariante(RutaCaliente(nombre, FotoTamano.Mini), variantes.Mini, firma);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudieron preparar miniatura y vista de la foto {FileName} ({Almacen}).", nombre, _opciones.Nombre);
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
        TryDelete(RutaCaliente(fileName, FotoTamano.Vista));
        TryDelete(RutaCaliente(fileName, FotoTamano.Mini));
    }

    private void BorrarCaliente(string fileName)
    {
        TryDelete(RutaCaliente(fileName, FotoTamano.Original));
        InvalidarVariantes(fileName);
    }

    // ── Calentar lo que solo está en el 23 ──────────────────────────────────────────────────────────────────────

    /// <summary>Copia en segundo plano una foto que se leyó del 23 para que la siguiente lectura sea local.</summary>
    private void ProgramarCalentado(string nombre, string rutaEn23)
    {
        if (!HotCacheActivo || ExisteBaja(nombre) || File.Exists(RutaCaliente(nombre, FotoTamano.Original)))
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
                string destino = RutaCaliente(nombre, FotoTamano.Original);
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
                _logger.LogDebug(ex, "No se pudo calentar la foto {FileName}.", nombre);
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

        string destino = RutaCaliente(nombre, FotoTamano.Original);
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
            _logger.LogDebug(ex, "No se pudo calentar la foto {FileName}.", nombre);
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

    /// <summary>
    /// Registro al que pertenece una foto (todas las de un registro comparten reloj): lo que va antes del último guion.
    /// "E-021107-26-0003.jpg" → "E-021107-26"; "FREUDENBERG~01I26106450-1001~4521.jpg" → "FREUDENBERG~01I26106450".
    /// </summary>
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
            var en23 = new FileInfo(RutaEn23(nombre));
            return en23.Exists
                && en23.Length > 0
                && (en23.Length == caliente.Length || en23.LastWriteTimeUtc > caliente.LastWriteTimeUtc);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── Para los almacenes ──────────────────────────────────────────────────────────────────────────────────────

    protected string DirectorioPendientes => GetPendingDirectory();

    /// <summary>Las carpetas de la cola local (preparación, pendientes y bajas).</summary>
    protected IReadOnlyList<string> CarpetasDeCola => new[] { GetStagingDirectory(), GetPendingDirectory(), GetDeleteDirectory() };

    protected string DirectorioCalienteFotos => GetHotFotosDirectory();

    protected string? DirectorioRespaldo => _opciones.CarpetaRespaldo;

    protected bool ExisteBaja(string fileName)
    {
        return File.Exists(RutaMarca(Path.GetFileName(fileName)));
    }

    protected static bool EsTemporal(string fileName)
    {
        return fileName.EndsWith(TempSufijo, StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(ApartadoSufijo, StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".uploading", StringComparison.OrdinalIgnoreCase);
    }

    protected static bool TryDelete(string path)
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

    /// <summary>Prueba que se pueda escribir en una carpeta (la crea si falta).</summary>
    protected static bool PuedeEscribir(string carpeta, out Exception? error)
    {
        try
        {
            Directory.CreateDirectory(carpeta);
            string prueba = Path.Combine(carpeta, $".prueba-{Guid.NewGuid():N}");
            File.WriteAllText(prueba, "ok");
            File.Delete(prueba);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
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

    private string RutaMarca(string nombre) => Path.Combine(GetDeleteDirectory(), nombre + ".delete");

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
        foreach (string directorio in new[] { GetHotFotosDirectory(), GetHotVistaDirectory(), GetHotMiniDirectory(), GetActividadDirectory() })
        {
            if (!PuedeEscribir(directorio, out Exception? error))
            {
                _logger.LogWarning(
                    error,
                    "Almacén caliente de fotos de {Almacen} APAGADO: no se puede escribir en {Ruta}. Las fotos se sirven como antes, del 23. Configura {Seccion}:HotPath con una carpeta escribible.",
                    _opciones.Nombre,
                    _opciones.RaizCaliente,
                    _opciones.SeccionConfig);
                return false;
            }
        }

        _logger.LogInformation(
            "Almacén caliente de fotos de {Almacen} activo en {Ruta}: {Horas} h desde el último movimiento, miniaturas {Dias} días, copia al 23 de {Paralelo} en {Paralelo}.",
            _opciones.Nombre,
            _opciones.RaizCaliente,
            _opciones.HorasCaliente.TotalHours,
            _opciones.DiasMiniatura.TotalDays,
            _opciones.Paralelo,
            _opciones.Paralelo);
        return true;
    }

    private string GetHotFotosDirectory() => Path.Combine(_opciones.RaizCaliente, "fotos");

    private string GetHotVistaDirectory() => Path.Combine(_opciones.RaizCaliente, "vista");

    private string GetHotMiniDirectory() => Path.Combine(_opciones.RaizCaliente, "mini");

    private string GetActividadDirectory() => Path.Combine(_opciones.RaizCaliente, "actividad");

    private string GetPendingDirectory() => Path.Combine(_opciones.RaizCola, "pending");

    private string GetStagingDirectory() => Path.Combine(_opciones.RaizCola, "staging");

    private string GetDeleteDirectory() => Path.Combine(_opciones.RaizCola, "delete");
}
