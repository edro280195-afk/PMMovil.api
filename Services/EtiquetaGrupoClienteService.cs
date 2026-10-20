using System.Data;

using Dapper;

using Microsoft.Extensions.Caching.Memory;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Grupos de cliente para la revisión por códigos de barras: qué clientes
/// tienen etiquetas conocidas y bajo qué perfil se leen.
///
/// La tabla vive en pmmutileria (ver Scripts/etiqueta_grupos_cliente_20261019.sql)
/// y el catálogo de clientes en pmmtraf, en el mismo servidor, así que las
/// consultas que necesitan el nombre del cliente cruzan las dos bases con el
/// prefijo explícito. La columna cidcte se creó en latin1 a propósito para que
/// ese cruce no muera por colaciones mezcladas.
/// </summary>
public sealed class EtiquetaGrupoClienteService
{
    private const string CachePerfil = "etiquetas:perfil:";
    private const string CacheGrupos = "etiquetas:grupos";

    private readonly BaseRepository _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EtiquetaGrupoClienteService> _logger;

    public EtiquetaGrupoClienteService(
        BaseRepository db,
        IMemoryCache cache,
        ILogger<EtiquetaGrupoClienteService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Lo que pregunta la app al abrir una entrada. Se cachea cinco minutos
    /// porque se consulta en cada apertura y cambia muy de vez en cuando.
    /// Un cliente sin grupo, o con el grupo apagado, responde que no aplica.
    /// </summary>
    public async Task<EtiquetaPerfilClienteResponse> GetPerfilAsync(string cidcte, CancellationToken ct = default)
    {
        string clave = (cidcte ?? string.Empty).Trim();
        if (clave.Length == 0) return new EtiquetaPerfilClienteResponse { Aplica = false };

        string cacheKey = CachePerfil + clave.ToUpperInvariant();
        if (_cache.TryGetValue(cacheKey, out EtiquetaPerfilClienteResponse? cached) && cached is not null)
            return cached;

        EtiquetaPerfilClienteResponse respuesta = new() { Aplica = false };
        try
        {
            using IDbConnection conn = _db.CreatePmmutileriaConnection();
            FilaPerfil? fila = await conn.QueryFirstOrDefaultAsync<FilaPerfil>(new CommandDefinition(@"
                SELECT g.clave AS Clave, g.nombre AS Nombre
                FROM etiqueta_grupo_cliente_miembro m
                JOIN etiqueta_grupo_cliente g ON g.id_grupo = m.id_grupo
                WHERE m.cidcte = @cidcte AND g.activo = 1
                LIMIT 1",
                new { cidcte = clave }, cancellationToken: ct));

            if (fila is not null)
            {
                respuesta = new EtiquetaPerfilClienteResponse
                {
                    Aplica = true,
                    Clave = fila.Clave,
                    Nombre = fila.Nombre
                };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Que falle la consulta no debe tumbar la apertura de la entrada:
            // sin perfil el revisador sigue con la revisión normal de siempre.
            _logger.LogError(ex, "No se pudo resolver el perfil de etiquetas del cliente {Cliente}", clave);
            return respuesta;
        }

        _cache.Set(cacheKey, respuesta, TimeSpan.FromMinutes(5));
        return respuesta;
    }

    /// <summary>
    /// El mismo perfil, pero preguntando por la referencia de la entrada en vez de por el cliente.
    /// Es el que usa la app: al abrir una entrada siempre tiene la referencia a mano, mientras que el
    /// número de cliente no viaja en todos los caminos (desde la bandeja de notificaciones no está).
    /// </summary>
    public async Task<EtiquetaPerfilClienteResponse> GetPerfilPorReferenciaAsync(
        string crefAgrupada,
        CancellationToken ct = default)
    {
        string cref = (crefAgrupada ?? string.Empty).Trim();
        if (cref.Length == 0) return new EtiquetaPerfilClienteResponse { Aplica = false };

        string cacheKey = CachePerfil + "ref:" + cref.ToUpperInvariant();
        if (_cache.TryGetValue(cacheKey, out EtiquetaPerfilClienteResponse? cached) && cached is not null)
            return cached;

        string? cliente;
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            // La entrada puede venir por su referencia propia o por la agrupada; se admiten las dos
            // porque las pantallas usan una u otra según de dónde se abra.
            cliente = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(@"
                SELECT cclienteid
                FROM revdet
                WHERE (cref_agrupada = @cref OR cref = @cref) AND cclienteid <> ''
                LIMIT 1",
                new { cref }, cancellationToken: ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo resolver el cliente de la entrada {Referencia}", cref);
            return new EtiquetaPerfilClienteResponse { Aplica = false };
        }

        EtiquetaPerfilClienteResponse perfil = await GetPerfilAsync(cliente ?? string.Empty, ct);
        _cache.Set(cacheKey, perfil, TimeSpan.FromMinutes(5));
        return perfil;
    }

    /// <summary>Todos los grupos con sus miembros, para el panel.</summary>
    public async Task<List<EtiquetaGrupoCliente>> ListarAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheGrupos, out List<EtiquetaGrupoCliente>? cached) && cached is not null)
            return cached;

        using IDbConnection conn = _db.CreatePmmutileriaConnection();

        var grupos = (await conn.QueryAsync<EtiquetaGrupoCliente>(new CommandDefinition(@"
            SELECT id_grupo AS IdGrupo, clave AS Clave, nombre AS Nombre,
                   activo AS Activo, notas AS Notas,
                   fmodi AS FechaModificacion, usumodi AS UsuarioModificacion
            FROM etiqueta_grupo_cliente
            ORDER BY nombre", cancellationToken: ct))).ToList();

        if (grupos.Count == 0)
        {
            _cache.Set(CacheGrupos, grupos, TimeSpan.FromMinutes(5));
            return grupos;
        }

        var miembros = await conn.QueryAsync<FilaMiembro>(new CommandDefinition(@"
            SELECT m.id_grupo AS IdGrupo, m.cidcte AS Cidcte, c.cnombre AS Nombre
            FROM etiqueta_grupo_cliente_miembro m
            LEFT JOIN pmmtraf.clientes c ON c.cidcte = m.cidcte
            ORDER BY c.cnombre, m.cidcte", cancellationToken: ct));

        var porGrupo = miembros.GroupBy(m => m.IdGrupo)
            .ToDictionary(g => g.Key, g => g.Select(m => new EtiquetaGrupoMiembro
            {
                Cidcte = m.Cidcte,
                Nombre = m.Nombre
            }).ToList());

        foreach (EtiquetaGrupoCliente grupo in grupos)
        {
            grupo.Miembros = porGrupo.TryGetValue(grupo.IdGrupo, out var lista) ? lista : [];
        }

        _cache.Set(CacheGrupos, grupos, TimeSpan.FromMinutes(5));
        return grupos;
    }

    /// <summary>
    /// Alta o cambio. Los miembros se reemplazan por completo con lo que mande
    /// el formulario. Todo va en una transacción: un grupo a medio guardar
    /// dejaría clientes sin modo o con el modo de otro.
    /// </summary>
    public async Task<(bool Ok, string? Error, int IdGrupo)> GuardarAsync(
        EtiquetaGrupoGuardarRequest request,
        string usuario,
        CancellationToken ct = default)
    {
        string clave = (request.Clave ?? string.Empty).Trim().ToUpperInvariant();
        string nombre = (request.Nombre ?? string.Empty).Trim();

        if (clave.Length == 0) return (false, "La clave del grupo es obligatoria.", 0);
        if (nombre.Length == 0) return (false, "El nombre del grupo es obligatorio.", 0);
        if (clave.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            return (false, "La clave solo admite letras, números y guion bajo.", 0);

        List<string> clientes = (request.Clientes ?? [])
            .Select(c => (c ?? string.Empty).Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        conn.Open();
        using IDbTransaction tx = conn.BeginTransaction();
        try
        {
            // La clave es única: si ya existe en otro grupo, se avisa en vez de
            // dejar que reviente el índice con un mensaje de MySQL.
            int? choqueClave = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT id_grupo FROM etiqueta_grupo_cliente WHERE clave = @clave AND id_grupo <> @id LIMIT 1",
                new { clave, id = request.IdGrupo }, tx, cancellationToken: ct));
            if (choqueClave is not null)
                return (false, $"Ya hay otro grupo con la clave {clave}.", 0);

            int idGrupo = request.IdGrupo;
            if (idGrupo > 0)
            {
                int filas = await conn.ExecuteAsync(new CommandDefinition(@"
                    UPDATE etiqueta_grupo_cliente
                    SET clave = @clave, nombre = @nombre, activo = @activo,
                        notas = @notas, usumodi = @usuario, fmodi = NOW()
                    WHERE id_grupo = @id",
                    new { clave, nombre, activo = request.Activo ? 1 : 0, notas = request.Notas, usuario, id = idGrupo },
                    tx, cancellationToken: ct));
                if (filas == 0)
                {
                    // Puede ser que no exista, o que se haya guardado igual que estaba.
                    int? existe = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                        "SELECT id_grupo FROM etiqueta_grupo_cliente WHERE id_grupo = @id LIMIT 1",
                        new { id = idGrupo }, tx, cancellationToken: ct));
                    if (existe is null) return (false, "El grupo ya no existe.", 0);
                }
            }
            else
            {
                idGrupo = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
                    INSERT INTO etiqueta_grupo_cliente (clave, nombre, activo, notas, usucrea, fcrea, usumodi, fmodi)
                    VALUES (@clave, @nombre, @activo, @notas, @usuario, NOW(), @usuario, NOW());
                    SELECT LAST_INSERT_ID();",
                    new { clave, nombre, activo = request.Activo ? 1 : 0, notas = request.Notas, usuario },
                    tx, cancellationToken: ct));
            }

            // Un cliente pertenece a un solo grupo: si viene uno que ya está en
            // otro, se avisa antes de tocar nada.
            if (clientes.Count > 0)
            {
                var ajenos = await conn.QueryAsync<string>(new CommandDefinition(@"
                    SELECT m.cidcte
                    FROM etiqueta_grupo_cliente_miembro m
                    WHERE m.cidcte IN @clientes AND m.id_grupo <> @id",
                    new { clientes, id = idGrupo }, tx, cancellationToken: ct));
                var lista = ajenos.ToList();
                if (lista.Count > 0)
                    return (false, $"Estos clientes ya están en otro grupo: {string.Join(", ", lista)}.", 0);
            }

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM etiqueta_grupo_cliente_miembro WHERE id_grupo = @id",
                new { id = idGrupo }, tx, cancellationToken: ct));

            if (clientes.Count > 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO etiqueta_grupo_cliente_miembro (id_grupo, cidcte, usucrea, fcrea)
                    VALUES (@idGrupo, @cidcte, @usuario, NOW())",
                    clientes.Select(c => new { idGrupo, cidcte = c, usuario }),
                    tx, cancellationToken: ct));
            }

            tx.Commit();
            LimpiarCache();
            return (true, null, idGrupo);
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { /* la conexión ya pudo morir */ }
            _logger.LogError(ex, "Error al guardar el grupo de etiquetas {Clave}", clave);
            return (false, "No se pudo guardar el grupo.", 0);
        }
    }

    /// <summary>Borra el grupo y, por la llave foránea, sus miembros.</summary>
    public async Task<(bool Ok, string? Error)> EliminarAsync(int idGrupo, CancellationToken ct = default)
    {
        try
        {
            using IDbConnection conn = _db.CreatePmmutileriaConnection();
            int filas = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM etiqueta_grupo_cliente WHERE id_grupo = @id",
                new { id = idGrupo }, cancellationToken: ct));
            LimpiarCache();
            return filas > 0 ? (true, null) : (false, "El grupo ya no existe.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al eliminar el grupo de etiquetas {Id}", idGrupo);
            return (false, "No se pudo eliminar el grupo.");
        }
    }

    /// <summary>
    /// Buscador de clientes para el formulario. Dice de una vez a qué grupo
    /// pertenece cada uno, para que no haya que intentarlo y fallar.
    /// </summary>
    public async Task<List<EtiquetaClienteCatalogoItem>> BuscarClientesAsync(
        string? texto,
        CancellationToken ct = default)
    {
        string filtro = (texto ?? string.Empty).Trim();
        using IDbConnection conn = _db.CreatePmmutileriaConnection();

        var filas = await conn.QueryAsync<EtiquetaClienteCatalogoItem>(new CommandDefinition(@"
            SELECT c.cidcte AS Cidcte, c.cnombre AS Nombre, g.clave AS GrupoActual
            FROM pmmtraf.clientes c
            LEFT JOIN etiqueta_grupo_cliente_miembro m ON m.cidcte = c.cidcte
            LEFT JOIN etiqueta_grupo_cliente g ON g.id_grupo = m.id_grupo
            WHERE @filtro = '' OR c.cnombre LIKE @like OR c.cidcte LIKE @like
            ORDER BY c.cnombre
            LIMIT 50",
            new { filtro, like = "%" + filtro + "%" }, cancellationToken: ct));

        return filas.ToList();
    }

    private void LimpiarCache()
    {
        _cache.Remove(CacheGrupos);
        // Los perfiles se cachean por cliente y no hay forma de enumerarlos en
        // IMemoryCache. Caducan solos en cinco minutos, que es tolerable para
        // un catálogo que cambia una vez cada tantos meses.
    }

    // Dapper mapea por nombre de propiedad; con ValueTuple el mapeo va por
    // posición y un cambio de orden en el SELECT lo rompería en silencio.
    private sealed class FilaPerfil
    {
        public string Clave { get; set; } = string.Empty;
        public string Nombre { get; set; } = string.Empty;
    }

    private sealed class FilaMiembro
    {
        public int IdGrupo { get; set; }
        public string Cidcte { get; set; } = string.Empty;
        public string? Nombre { get; set; }
    }
}
