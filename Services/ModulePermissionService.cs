using Dapper;
using MySqlConnector;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public interface IModulePermissionService
{
    Task<bool> HasFullAccessAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModuloPermisoDto>> GetPermissionsAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task<bool> HasPermissionAsync(
        string username,
        int moduleId,
        string action,
        CancellationToken cancellationToken = default);
}

public sealed class ModulePermissionService(
    IConfiguration configuration,
    ILogger<ModulePermissionService> logger) : IModulePermissionService
{
    private readonly string _connectionString = configuration.GetConnectionString("PmmtrafDb")
        ?? throw new InvalidOperationException("Connection string 'PmmtrafDb' not found.");

    /// <summary>
    /// Departamento SISTEMAS en <c>pmmtraf.departamentos</c>. Mismo valor que
    /// usa PMMAdmin (<c>SystemsAccessService.SystemsDepartmentId</c>) para
    /// decidir acceso total -- se comparte aquí para que los dos sistemas
    /// nunca vuelvan a divergir en quién es "Sistemas".
    /// </summary>
    public const int SystemsDepartmentId = 1;

    public async Task<bool> HasFullAccessAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var user = await ResolveUserAsync(connection, username, cancellationToken);
        return user is not null && IsSystemsUser(user);
    }

    public async Task<IReadOnlyList<ModuloPermisoDto>> GetPermissionsAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var user = await ResolveUserAsync(connection, username, cancellationToken);
        if (user is null) return [];
        var hasFullAccess = IsSystemsUser(user);

        const string modulesSql = """
            SELECT m.ModuloID AS ModuloId,
                   COALESCE(m.Descripcion, '') AS Descripcion,
                   COALESCE(m.FormularioWeb, '') AS FormularioWeb,
                   COALESCE(m.iconW, '') AS IconW
            FROM modulos m
            WHERE COALESCE(m.Inactivo, 0) = 0
            ORDER BY m.OrdenenLista, m.ModuloID
            """;

        var modules = (await connection.QueryAsync<ModuleRow>(new CommandDefinition(
            modulesSql,
            cancellationToken: cancellationToken))).AsList();

        var accessByModule = await LoadAccessAsync(connection, user.UserId, cancellationToken);

        return modules.Select(module =>
        {
            // Los módulos móviles (151-167) existen en el catálogo pero nunca
            // se poblaron en `accesos`: sin la traducción desde los módulos
            // del sistema web, todo usuario ajeno a Sistemas recibía los doce
            // en cero y se quedaba sin ninguna opción en la aplicación.
            var access = hasFullAccess
                ? new ModuleAccess(true, true, true, true)
                : LegacyModuleMap.Resolve(module.ModuloId, accessByModule, username);

            return new ModuloPermisoDto
            {
                ModuloId = module.ModuloId,
                Descripcion = module.Descripcion,
                FormularioWeb = module.FormularioWeb,
                IconW = module.IconW,
                Lectura = access.Lectura,
                Agregar = access.Agregar,
                Modificar = access.Modificar,
                Borrar = access.Borrar,
                AccesoTotal = hasFullAccess
            };
        }).ToList();
    }

    /// <summary>
    /// Permisos del usuario indexados por módulo, agregando con OR las filas
    /// repetidas igual que hacía el <c>MAX()</c> de la consulta original.
    /// </summary>
    private static async Task<Dictionary<int, ModuleAccess>> LoadAccessAsync(
        MySqlConnection connection,
        int userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT a.Fk_ModuloID AS ModuloId,
                   MAX(COALESCE(a.Lectura, 0)) AS Lectura,
                   MAX(COALESCE(a.Agregar, 0)) AS Agregar,
                   MAX(COALESCE(a.Modificar, 0)) AS Modificar,
                   MAX(COALESCE(a.Borrar, 0)) AS Borrar
            FROM accesos a
            WHERE a.Fk_UsuarioID = @userId
            GROUP BY a.Fk_ModuloID
            """;

        var rows = await connection.QueryAsync<PermissionRow>(new CommandDefinition(
            sql,
            new { userId },
            cancellationToken: cancellationToken));

        return rows.ToDictionary(
            row => row.ModuloId,
            row => new ModuleAccess(
                row.Lectura != 0,
                row.Agregar != 0,
                row.Modificar != 0,
                row.Borrar != 0));
    }

    public async Task<bool> HasPermissionAsync(
        string username,
        int moduleId,
        string action,
        CancellationToken cancellationToken = default)
    {
        if (moduleId <= 0 || !TryNormalizeAction(action, out var normalizedAction))
            return false;

        await using var connection = new MySqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                exception,
                "Consulta de permisos cancelada antes de abrir MySQL. Username={Username}, ModuleId={ModuleId}, Action={Action}",
                username,
                moduleId,
                action);
            throw;
        }

        var user = await ResolveUserAsync(connection, username, cancellationToken);
        if (user is null) return false;

        // Sistemas tiene acceso integral incluso mientras se regularizan en el
        // catálogo los identificadores de módulos que ya usa la aplicación móvil.
        if (IsSystemsUser(user))
            return true;

        const string moduleSql = """
            SELECT COUNT(*)
            FROM modulos
            WHERE ModuloID = @moduleId
              AND COALESCE(Inactivo, 0) = 0
            """;
        var activeModule = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            moduleSql,
            new { moduleId },
            cancellationToken: cancellationToken)) > 0;
        if (!activeModule) return false;

        // Una fila explícita del módulo móvil, incluso con todo en cero,
        // sobrescribe la herencia histórica. Esto permite revocar una
        // capacidad sin modificar los permisos históricos equivalentes.
        const string directRowSql = """
            SELECT COUNT(*)
            FROM accesos
            WHERE Fk_UsuarioID = @userId
              AND Fk_ModuloID = @moduleId
            """;
        var hasDirectRow = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            directRowSql,
            new { userId = user.UserId, moduleId },
            cancellationToken: cancellationToken)) > 0;

        if (hasDirectRow)
        {
            const string directPermissionSql = """
                SELECT COALESCE(MAX(
                    CASE @action
                        WHEN 'Lectura' THEN a.Lectura
                        WHEN 'Agregar' THEN a.Agregar
                        WHEN 'Modificar' THEN a.Modificar
                        WHEN 'Borrar' THEN a.Borrar
                        ELSE 0
                    END
                ), 0)
                FROM accesos a
                WHERE a.Fk_UsuarioID = @userId
                  AND a.Fk_ModuloID = @moduleId
                """;
            var directPermission = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                directPermissionSql,
                new { userId = user.UserId, moduleId, action = normalizedAction },
                cancellationToken: cancellationToken));
            return directPermission == 1;
        }

        // Módulos sin equivalencia en el catálogo histórico que se conceden a
        // personas concretas (hoy, Revisión a la lista oficial de revisores).
        if (LegacyModuleMap.IsGrantedToUser(moduleId, username))
            return true;

        // Se consulta también el módulo histórico que habilita al módulo
        // móvil, porque el catálogo móvil puede no tener fila propia.
        var moduleIds = new List<int> { moduleId };
        moduleIds.AddRange(LegacyModuleMap.LegacyIdsFor(moduleId));

        const string permissionSql = """
            SELECT COALESCE(MAX(
                CASE @action
                    WHEN 'Lectura' THEN a.Lectura
                    WHEN 'Agregar' THEN a.Agregar
                    WHEN 'Modificar' THEN a.Modificar
                    WHEN 'Borrar' THEN a.Borrar
                    ELSE 0
                END
            ), 0)
            FROM accesos a
            WHERE a.Fk_UsuarioID = @userId
              AND a.Fk_ModuloID IN @moduleIds
            """;

        var allowed = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            permissionSql,
            new { userId = user.UserId, moduleIds, action = normalizedAction },
            cancellationToken: cancellationToken));
        return allowed == 1;
    }

    private async Task<PermissionUser?> ResolveUserAsync(
        MySqlConnection connection,
        string username,
        CancellationToken cancellationToken)
    {
        var normalizedUsername = username.Trim();
        if (normalizedUsername.Length == 0) return null;

        const string sql = """
            SELECT usuarioid AS UserId,
                   iddepto AS DepartmentId,
                   TRIM(COALESCE(area, '')) AS Area
            FROM usuarios
            WHERE TRIM(claveusuario) = @username
              AND inactivo = 0
            """;
        var users = (await connection.QueryAsync<PermissionUser>(new CommandDefinition(
            sql,
            new { username = normalizedUsername },
            cancellationToken: cancellationToken))).AsList();

        if (users.Count == 1) return users[0];

        if (users.Count > 1)
        {
            logger.LogWarning(
                "Permisos rechazados: {Username} tiene {Count} identidades activas en pmmtraf.usuarios.",
                normalizedUsername,
                users.Count);
        }

        return null;
    }

    private static bool TryNormalizeAction(string action, out string normalized)
    {
        normalized = action.Trim() switch
        {
            var value when value.Equals("Lectura", StringComparison.OrdinalIgnoreCase) => "Lectura",
            var value when value.Equals("Agregar", StringComparison.OrdinalIgnoreCase) => "Agregar",
            var value when value.Equals("Modificar", StringComparison.OrdinalIgnoreCase) => "Modificar",
            var value when value.Equals("Borrar", StringComparison.OrdinalIgnoreCase) => "Borrar",
            _ => string.Empty
        };
        return normalized.Length > 0;
    }

    private sealed class PermissionUser
    {
        public int UserId { get; set; }
        public int DepartmentId { get; set; }
        public string Area { get; set; } = string.Empty;
    }

    private static bool IsSystemsArea(string? area)
    {
        var normalized = area?.Trim().ToUpperInvariant();
        return normalized is "HARDWARE" or "SOFTWARE";
    }

    /// <summary>
    /// Acceso total por Area (HARDWARE/SOFTWARE, criterio histórico de
    /// PMMovil) O por Departamento (SISTEMAS, criterio que ya usa PMMAdmin
    /// para decidir si un usuario "no necesita permisos explícitos"). Es una
    /// unión deliberada: antes de esto, un usuario del departamento de
    /// Sistemas sin el campo Area bien llenado (p. ej. cparra, isabel.campos)
    /// aparecía como acceso total en la página pero como usuario normal en
    /// PMMovil. Ver <see cref="AuthService.EsUsuarioSistemas"/>, que aplica
    /// el mismo criterio al armar la respuesta de login.
    /// </summary>
    private static bool IsSystemsUser(PermissionUser user) =>
        IsSystemsArea(user.Area) || user.DepartmentId == SystemsDepartmentId;

    private sealed class ModuleRow
    {
        public int ModuloId { get; set; }
        public string Descripcion { get; set; } = string.Empty;
        public string FormularioWeb { get; set; } = string.Empty;
        public string IconW { get; set; } = string.Empty;
    }

    private sealed class PermissionRow
    {
        public int ModuloId { get; set; }
        public long Lectura { get; set; }
        public long Agregar { get; set; }
        public long Modificar { get; set; }
        public long Borrar { get; set; }
    }
}
