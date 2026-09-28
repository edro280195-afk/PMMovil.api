using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Dapper;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public sealed record MonitorTvLinkAccess(long Id, DateTime? FechaExpiracion);

public interface IMonitorTvLinkService
{
    Task<CrearMonitorTvLinkResult> CrearAsync(CrearMonitorTvLinkRequest request, string usuario, CancellationToken cancellationToken);
    Task<IReadOnlyList<MonitorTvLinkItem>> ListarAsync(CancellationToken cancellationToken);
    Task<bool> RevocarAsync(long id, string usuario, CancellationToken cancellationToken);
    Task<MonitorTvLinkAccess?> ResolverAsync(string token, CancellationToken cancellationToken);
}

/// <summary>
/// Administra enlaces de pantalla TV. El valor utilizable nunca se guarda en
/// la base de datos: solo se almacena SHA-256 y una pista de ocho caracteres.
/// </summary>
public sealed class MonitorTvLinkService : IMonitorTvLinkService
{
    private sealed class MonitorTvLinkAccessRow
    {
        public long Id { get; init; }
        public string? FechaExpiracion { get; init; }
    }

    private const int TokenBytes = 32;
    private const int MaxNameLength = 100;
    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private static volatile bool _schemaReady;

    private readonly BaseRepository _db;

    public MonitorTvLinkService(BaseRepository db)
    {
        _db = db;
    }

    public async Task<CrearMonitorTvLinkResult> CrearAsync(
        CrearMonitorTvLinkRequest request,
        string usuario,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string name = string.IsNullOrWhiteSpace(request.Nombre)
            ? "Pantalla de operación"
            : request.Nombre.Trim();
        name = name[..Math.Min(name.Length, MaxNameLength)];

        if (request.ExpiraEnDias is < 0 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ExpiraEnDias), "La vigencia debe estar entre 0 y 3650 días.");
        }

        await EnsureSchemaAsync(cancellationToken);

        string token = ToBase64Url(RandomNumberGenerator.GetBytes(TokenBytes));
        string tokenHash = HashToken(token);
        string tokenHint = token[..8];
        DateTime? expiration = request.ExpiraEnDias == 0
            ? null
            : DateTime.Now.AddDays(request.ExpiraEnDias);

        using IDbConnection connection = _db.CreatePmmutileriaConnection();
        long id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            @"INSERT INTO monitor_tv_links
                (token_hash, token_hint, nombre, usucrea, fexpira)
              VALUES (@tokenHash, @tokenHint, @nombre, @usuario, @expiration);
              SELECT LAST_INSERT_ID();",
            new
            {
                tokenHash,
                tokenHint,
                nombre = name,
                usuario = string.IsNullOrWhiteSpace(usuario) ? "sistema" : usuario,
                expiration,
            },
            cancellationToken: cancellationToken));

        return new CrearMonitorTvLinkResult
        {
            Id = id,
            Token = token,
            TokenHint = tokenHint,
            Nombre = name,
            FechaExpiracion = expiration,
        };
    }

    public async Task<IReadOnlyList<MonitorTvLinkItem>> ListarAsync(CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        using IDbConnection connection = _db.CreatePmmutileriaConnection();
        IEnumerable<MonitorTvLinkItem> rows = await connection.QueryAsync<MonitorTvLinkItem>(new CommandDefinition(
            @"SELECT id AS Id,
                     nombre AS Nombre,
                     token_hint AS TokenHint,
                     usucrea AS UsuarioCrea,
                     fcrea AS FechaCreacion,
                     fexpira AS FechaExpiracion,
                     revocado AS Revocado
              FROM monitor_tv_links
              ORDER BY revocado ASC, fcrea DESC",
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task<bool> RevocarAsync(long id, string usuario, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        using IDbConnection connection = _db.CreatePmmutileriaConnection();
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            @"UPDATE monitor_tv_links
              SET revocado = 1,
                  frevoca = NOW(),
                  usu_revoca = @usuario
              WHERE id = @id AND revocado = 0",
            new
            {
                id,
                usuario = string.IsNullOrWhiteSpace(usuario) ? "sistema" : usuario,
            },
            cancellationToken: cancellationToken));

        return affected > 0;
    }

    public async Task<MonitorTvLinkAccess?> ResolverAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
        {
            return null;
        }

        await EnsureSchemaAsync(cancellationToken);

        using IDbConnection connection = _db.CreatePmmutileriaConnection();
        MonitorTvLinkAccessRow? row = await connection.QueryFirstOrDefaultAsync<MonitorTvLinkAccessRow>(new CommandDefinition(
            @"SELECT id AS Id,
                     DATE_FORMAT(fexpira, '%Y-%m-%d %H:%i:%s') AS FechaExpiracion
              FROM monitor_tv_links
              WHERE token_hash = @tokenHash
                AND revocado = 0
                AND (fexpira IS NULL OR fexpira > NOW())
              LIMIT 1",
            new { tokenHash = HashToken(token) },
            cancellationToken: cancellationToken));

        if (row == null)
        {
            return null;
        }

        DateTime? expiration = null;
        if (!string.IsNullOrWhiteSpace(row.FechaExpiracion)
            && DateTime.TryParseExact(
                row.FechaExpiracion,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsedExpiration))
        {
            expiration = parsedExpiration;
        }

        return new MonitorTvLinkAccess(row.Id, expiration);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }

        await SchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            using IDbConnection connection = _db.CreatePmmutileriaConnection();
            await connection.ExecuteAsync(new CommandDefinition(
                @"CREATE TABLE IF NOT EXISTS monitor_tv_links (
                    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                    token_hash CHAR(64) NOT NULL,
                    token_hint CHAR(8) NOT NULL,
                    nombre VARCHAR(100) NOT NULL,
                    usucrea VARCHAR(100) NOT NULL,
                    fcrea DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    fexpira DATETIME NULL,
                    revocado TINYINT(1) NOT NULL DEFAULT 0,
                    frevoca DATETIME NULL,
                    usu_revoca VARCHAR(100) NULL,
                    PRIMARY KEY (id),
                    UNIQUE KEY uq_monitor_tv_links_hash (token_hash),
                    KEY ix_monitor_tv_links_status (revocado, fexpira)
                  ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                cancellationToken: cancellationToken));
            _schemaReady = true;
        }
        finally
        {
            SchemaLock.Release();
        }
    }

    // Factorizado a ShareTokenHasher para que EntradaShareService/
    // RevisionShareService/ReportShareService usen el mismo algoritmo en vez
    // de reimplementarlo cada uno.
    private static string HashToken(string token) => ShareTokenHasher.Hash(token);

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
