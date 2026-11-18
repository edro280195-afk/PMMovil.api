namespace PMMovil.Api.Services;

/// <summary>
/// Una foto que vive en la carpeta del cliente en el 23: la fila de <c>fotos</c> (su <c>idfotos</c>) y dónde va. Tipos:
/// 100 evidencia de Entrada Bodega, 103 daño de Entrada o discrepancia de Revisión, 107 evidencia de Revisión.
/// </summary>
public sealed record FotoCliente(long IdFotos, string Carpeta, string Creferencia, int TipoDocto, int Linea, string? Extension);

/// <summary>
/// Foto de cliente recibida y escrita, esperando a que la base de datos le dé su <c>idfotos</c> para activarse.
/// <see cref="Directa"/>: se escribió en un temporal del 23 porque la cola local no está disponible.
/// </summary>
public sealed record FotoClientePreparada(string Carpeta, string Archivo, string Ruta, bool Directa);

/// <summary>
/// Fotos de Entrada Bodega y Revisión: viven en el 23 en la carpeta de su cliente (<c>Fotos\CLIENTE\REF-1071.jpg</c>, el
/// formato que lee FoxPro). Antes se escribían directo al 23 dentro de la petición (cada foto esperaba la copia lenta por
/// el túnel) y se leían siempre de allá; ahora pasan por el mismo almacén que Caseta (<see cref="AlmacenFotos"/>): llegan a
/// la cola local, se copian al 23 en segundo plano, se quedan calientes 48 h desde el último movimiento de su registro y
/// tienen miniatura y vista.
///
/// En la cola y en lo caliente cada foto se llama <c>CARPETA~REF-1071~idfotos.jpg</c>. El <c>idfotos</c> va en el nombre a
/// propósito: el consecutivo de línea se reusa (si se borra la última foto, la siguiente toma su número, y FoxPro o la
/// captura de etiquetas también escriben en esas carpetas), así que una copia local solo se sirve si es exactamente la
/// foto de esa fila; si no, se lee la del 23.
///
/// Sin cola local (carpeta no escribible, o <c>ClientePhotoSettings:Enabled=false</c>) todo funciona como antes: se escribe,
/// se lee y se borra directo en el 23.
/// </summary>
public class ClientePhotoStorage : AlmacenFotos
{
    public const string Seccion = "ClientePhotoSettings";
    private const char Separador = '~';
    private static readonly char[] Invalidos = Path.GetInvalidFileNameChars();

    private readonly string _raiz23;
    private readonly string _raizCola;
    private readonly bool _habilitado;
    private readonly ILogger _logger;
    private readonly IFotoMejoraCola? _mejora;
    private readonly Lazy<bool> _colaDisponible;

    public ClientePhotoStorage(IConfiguration config, ILogger<ClientePhotoStorage> logger, IFotoMejoraCola? mejora = null)
        : base(Opciones(config), logger, mejora)
    {
        _raiz23 = Raiz23De(config);
        _raizCola = RaizColaDe(config);
        _habilitado = HabilitadoEn(config);
        _logger = logger;
        _mejora = mejora;
        _colaDisponible = new Lazy<bool>(ProbarCola, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private static AlmacenFotosOpciones Opciones(IConfiguration config)
    {
        return AlmacenFotosOpciones.Leer(
            config,
            Seccion,
            "Entrada y Revisión",
            "Clientes",
            RaizColaDe(config),
            HabilitadoEn(config)) with
        {
            // Entrada y Revisión siempre borraron en el 23 al momento: se conserva (ver AlmacenFotosOpciones).
            BorrarEn23AlMomento = true
        };
    }

    /// <summary>
    /// True si las fotos nuevas pasan por la cola local; false = se escriben directo al 23, como antes. Hace falta que su
    /// trabajador esté corriendo (<see cref="ClientePhotoSyncWorker"/>): sin él nadie copiaría la cola al 23.
    /// </summary>
    public bool ColaActiva => TrabajadorActivo && _colaDisponible.Value;

    protected override bool CalientePermitido => ColaActiva;

    protected override string Raiz23 => _raiz23;

    protected override string RutaEn23(string nombre)
    {
        (string Carpeta, string Archivo)? partes = Desarmar(Path.GetFileName(nombre));
        return partes is { } p
            ? Path.Combine(_raiz23, p.Carpeta, p.Archivo)
            : Path.Combine(_raiz23, Path.GetFileName(nombre));
    }

    protected override string OrigenMejora(string nombre)
    {
        string archivo = Desarmar(Path.GetFileName(nombre))?.Archivo ?? nombre;
        return OrigenDeArchivo(archivo);
    }

    // ── Lectura ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Nombres locales posibles de la foto: el formato vigente (REF-1071) y el anterior del API (REF-10701).</summary>
    public IReadOnlyList<string> NombresLocales(FotoCliente foto)
    {
        string carpeta = (foto.Carpeta ?? string.Empty).Trim();
        var nombres = new List<string>(2);
        foreach (string archivo in ArchivosPosibles(foto))
        {
            if (NombreLocal(carpeta, archivo, foto.IdFotos) is { } nombre
                && !nombres.Contains(nombre, StringComparer.OrdinalIgnoreCase))
            {
                nombres.Add(nombre);
            }
        }

        return nombres;
    }

    /// <summary>Archivo a servir en el tamaño pedido (ver <see cref="AlmacenFotos.ResolverAsync(IReadOnlyList{string}, FotoTamano, CancellationToken)"/>).</summary>
    public async Task<FotoArchivo?> ResolverAsync(FotoCliente foto, FotoTamano tamano, CancellationToken ct = default)
    {
        IReadOnlyList<string> nombres = NombresLocales(foto);
        if (nombres.Count == 0)
        {
            // Una carpeta o un nombre que no se pueden guardar aquí: se sirve del 23, como siempre.
            string? ruta = BuscarEn23(foto);
            return ruta == null ? null : new FotoArchivo(ruta, File.GetLastWriteTimeUtc(ruta), EsVariante: false);
        }

        return await ResolverAsync(nombres, tamano, ct);
    }

    /// <summary>Ruta del original completo (enlaces públicos, PDF, ZIP): primero lo local, después el 23.</summary>
    public string? BuscarOriginal(FotoCliente foto)
    {
        IReadOnlyList<string> nombres = NombresLocales(foto);
        return nombres.Count == 0 ? BuscarEn23(foto) : BuscarOriginal(nombres);
    }

    // ── Altas ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Escribe la foto que acaba de llegar, ANTES de insertar su fila. Con la cola activa queda en la preparación local
    /// (milisegundos) y después se activa con su <c>idfotos</c> (<see cref="Activar"/>); sin cola se escribe en un temporal
    /// del 23 junto a su lugar definitivo. Si la fila no se pudo insertar, <see cref="Descartar"/>.
    /// </summary>
    public async Task<FotoClientePreparada> PrepararAsync(
        byte[] bytes,
        string carpeta,
        string creferencia,
        int tipoDocto,
        int linea,
        string? extension,
        CancellationToken ct = default)
    {
        carpeta = (carpeta ?? string.Empty).Trim();
        string archivo = PhotoFileNameResolver.Build(creferencia ?? string.Empty, tipoDocto, linea, extension);

        if (ColaActiva && CarpetaValida(carpeta) && ArchivoValido(archivo))
        {
            FotoPreparada preparada = await StageAsync(bytes, archivo, ct);
            return new FotoClientePreparada(carpeta, archivo, preparada.StagedPath, Directa: false);
        }

        string destino = Path.Combine(_raiz23, carpeta, archivo);
        string carpetaDestino = Path.GetDirectoryName(destino)!;
        Directory.CreateDirectory(carpetaDestino);
        string temporal = Path.Combine(carpetaDestino, $".{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(temporal, bytes, ct);
        return new FotoClientePreparada(carpeta, archivo, temporal, Directa: true);
    }

    /// <summary>
    /// La fila ya existe: la foto se activa con su <c>idfotos</c>. Con cola, pasa a pendientes y se copia al 23 en segundo
    /// plano (ya se puede ver); sin cola, el temporal toma su nombre definitivo en el 23.
    /// </summary>
    public void Activar(FotoClientePreparada preparada, long idFotos)
    {
        string? nombre = preparada.Directa ? null : NombreLocal(preparada.Carpeta, preparada.Archivo, idFotos);
        if (nombre != null)
        {
            ActivateStagedPhotos(new[] { new FotoPreparada(nombre, preparada.Ruta) });
            return;
        }

        // Directa, o sin idfotos (no debería pasar): se deja en el 23 como siempre, en este momento.
        string destino = Path.Combine(_raiz23, preparada.Carpeta, preparada.Archivo);
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        if (preparada.Directa)
        {
            File.Move(preparada.Ruta, destino, overwrite: true);
        }
        else
        {
            File.Copy(preparada.Ruta, destino, overwrite: true);
            TryDelete(preparada.Ruta);
            _logger.LogWarning("Foto {Archivo} activada sin idfotos: se copió directo al 23.", preparada.Archivo);
        }

        _mejora?.Encolar(destino, OrigenDeArchivo(preparada.Archivo));
    }

    /// <summary>La fila no se insertó: la foto escrita sobra.</summary>
    public void Descartar(FotoClientePreparada preparada)
    {
        TryDelete(preparada.Ruta);
    }

    // ── Bajas ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Borra la foto: lo local en el acto y la copia del 23 también en el acto (si el 23 no contesta, se reintenta en
    /// segundo plano). Sin cola, directo en el 23 como siempre.
    /// </summary>
    public void Borrar(FotoCliente foto)
    {
        IReadOnlyList<string> nombres = ColaActiva ? NombresLocales(foto) : Array.Empty<string>();
        if (nombres.Count == 0)
        {
            foreach (string ruta in PhotoFileNameResolver.GetCandidatePaths(
                         _raiz23,
                         (foto.Carpeta ?? string.Empty).Trim(),
                         foto.Creferencia,
                         foto.TipoDocto,
                         foto.Linea,
                         foto.Extension))
            {
                if (File.Exists(ruta))
                {
                    File.Delete(ruta);
                }
            }

            return;
        }

        foreach (string nombre in nombres)
        {
            QueueDelete(nombre);
        }
    }

    // ── Nombres ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"FREUDENBERG" + "01I26106450-1001.jpg" + 4521 → "FREUDENBERG~01I26106450-1001~4521.jpg"; null si no se puede.</summary>
    internal static string? NombreLocal(string carpeta, string archivo, long idFotos)
    {
        if (idFotos <= 0 || !CarpetaValida(carpeta) || !ArchivoValido(archivo))
        {
            return null;
        }

        string extension = Path.GetExtension(archivo);
        return $"{carpeta}{Separador}{Path.GetFileNameWithoutExtension(archivo)}{Separador}{idFotos}{extension}";
    }

    /// <summary>Lo contrario de <see cref="NombreLocal"/>: la carpeta y el nombre en el 23. Se lee de derecha a izquierda
    /// porque la carpeta puede traer el separador y el nombre del archivo nunca (solo letras, números y guiones).</summary>
    internal static (string Carpeta, string Archivo)? Desarmar(string nombre)
    {
        string extension = Path.GetExtension(nombre);
        string sinExtension = nombre[..^extension.Length];
        int antesDelId = sinExtension.LastIndexOf(Separador);
        if (antesDelId <= 0 || !long.TryParse(sinExtension[(antesDelId + 1)..], out _))
        {
            return null;
        }

        int antesDelArchivo = sinExtension.LastIndexOf(Separador, antesDelId - 1);
        if (antesDelArchivo < 0 || antesDelArchivo + 1 >= antesDelId)
        {
            return null;
        }

        return (sinExtension[..antesDelArchivo], sinExtension[(antesDelArchivo + 1)..antesDelId] + extension);
    }

    /// <summary>La carpeta del cliente tiene que caber en un nombre de archivo (sin diagonales ni caracteres prohibidos).</summary>
    internal static bool CarpetaValida(string carpeta)
    {
        return carpeta.Length <= 120
            && carpeta != "."
            && carpeta != ".."
            && carpeta.IndexOfAny(Invalidos) < 0;
    }

    private static bool ArchivoValido(string archivo)
    {
        return archivo.Length > 0
            && archivo.IndexOf(Separador) < 0
            && archivo.IndexOfAny(Invalidos) < 0;
    }

    private static IEnumerable<string> ArchivosPosibles(FotoCliente foto)
    {
        string cref = foto.Creferencia ?? string.Empty;
        string vigente = PhotoFileNameResolver.Build(cref, foto.TipoDocto, foto.Linea, foto.Extension);
        string anterior = PhotoFileNameResolver.BuildLegacy(cref, foto.TipoDocto, foto.Linea, foto.Extension);
        yield return vigente;
        if (!string.Equals(vigente, anterior, StringComparison.OrdinalIgnoreCase))
        {
            yield return anterior;
        }
    }

    private string? BuscarEn23(FotoCliente foto)
    {
        return PhotoFileNameResolver.FindExistingPath(
            _raiz23,
            (foto.Carpeta ?? string.Empty).Trim(),
            foto.Creferencia,
            foto.TipoDocto,
            foto.Linea,
            foto.Extension);
    }

    /// <summary>"REF-1071.jpg" → "revision"; 100 = entrada, 103 = daño de entrada o discrepancia de revisión.</summary>
    private static string OrigenDeArchivo(string archivo)
    {
        string sinExtension = Path.GetFileNameWithoutExtension(archivo);
        int guion = sinExtension.LastIndexOf('-');
        string sufijo = guion >= 0 ? sinExtension[(guion + 1)..] : string.Empty;
        return sufijo.Length >= 3 ? sufijo[..3] switch
        {
            "100" => "entrada",
            "103" => "entrada-danio-o-discrepancia",
            "107" => "revision",
            _ => "fotos-cliente"
        } : "fotos-cliente";
    }

    // ── Configuración ───────────────────────────────────────────────────────────────────────────────────────────

    private bool ProbarCola()
    {
        if (!_habilitado)
        {
            _logger.LogInformation(
                "Almacén de fotos de Entrada y Revisión APAGADO ({Seccion}:Enabled=false): las fotos se escriben, leen y borran directo en el 23, como antes.",
                Seccion);
            return false;
        }

        foreach (string carpeta in CarpetasDeCola)
        {
            if (!PuedeEscribir(carpeta, out Exception? error))
            {
                _logger.LogWarning(
                    error,
                    "Cola de fotos de Entrada y Revisión APAGADA: no se puede escribir en {Ruta}. Las fotos se escriben directo al 23, como antes. Configura {Seccion}:QueuePath con una carpeta escribible.",
                    _raizCola,
                    Seccion);
                return false;
            }
        }

        _logger.LogInformation(
            "Cola de fotos de Entrada y Revisión activa en {Ruta}: cada foto se guarda aquí al llegar y se copia al 23 ({Raiz23}) en segundo plano.",
            _raizCola,
            _raiz23);
        return true;
    }

    private static bool HabilitadoEn(IConfiguration config) => config.GetValue<bool?>($"{Seccion}:Enabled") ?? true;

    private static string Raiz23De(IConfiguration config)
    {
        return config[$"{Seccion}:BasePath"] is { Length: > 0 } explicita
            ? explicita
            : config["PhotoSettings:BasePath"] ?? @"F:\Trafico\fotos";
    }

    private static string RaizColaDe(IConfiguration config)
    {
        return config[$"{Seccion}:QueuePath"] is { Length: > 0 } cola
            ? cola
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PMMovil",
                "FotosCola",
                "Clientes");
    }
}
