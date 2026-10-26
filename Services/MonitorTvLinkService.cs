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
using Microsoft.AspNetCore.DataProtection;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public sealed record MonitorTvLinkAccess(long Id, DateTime? FechaExpiracion, string TipoPantalla);
public sealed record MonitorTvLinkToken(string Token, string TokenHint);

public interface IMonitorTvLinkService
{
    Task<CrearMonitorTvLinkResult> CrearAsync(CrearMonitorTvLinkRequest request, string usuario, CancellationToken cancellationToken);
    Task<IReadOnlyList<MonitorTvLinkItem>> ListarAsync(CancellationToken cancellationToken);
    Task<string?> GetTokenAsync(long id, CancellationToken cancellationToken);
    Task<MonitorTvLinkToken?> RenewLegacyTokenAsync(long id, CancellationToken cancellationToken);
    Task<bool> RevocarAsync(long id, string usuario, CancellationToken cancellationToken);
    Task<MonitorTvLinkAccess?> ResolverAsync(string token, CancellationToken cancellationToken);
    Task<string> EmitirTicketTiempoRealAsync(long monitorTvLinkId, CancellationToken cancellationToken);
    Task<bool> ConsumirTicketTiempoRealAsync(string ticket, CancellationToken cancellationToken);
}

/// <summary>
/// Administra enlaces de pantalla TV. El valor utilizable se guarda cifrado
/// para permitir que un administrador autorizado lo copie de nuevo; la
/// validación pública sigue usando SHA-256 y una pista de ocho caracteres.
/// </summary>
public sealed class MonitorTvLinkService : IMonitorTvLinkService
{
    private sealed class MonitorTvLinkAccessRow
    {
        public long Id { get; init; }
        public string? FechaExpiracion { get; init; }
        public string TipoPantalla { get; init; } = "OPERACION_NLD";
    }

    private sealed class MonitorTvLinkTokenRow
    {
        public string TokenHash { get; init; } = string.Empty;
        public string? TokenEncrypted { get; init; }
    }

    private const int TokenBytes = 32;
    private const int MaxNameLength = 100;
    private const string OperacionNld = "OPERACION_NLD";
    private const string DesempenoImex = "DESEMPENO_IMEX";
    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private static volatile bool _schemaReady;

    private readonly BaseRepository _db;
    private readonly IDataProtector _tokenProtector;

    public MonitorTvLinkService(BaseRepository db, IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _tokenProtector = dataProtectionProvider.CreateProtector("PMMovil.MonitorTvLink.v1");
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
        string tipoPantalla = string.Equals(request.TipoPantalla, DesempenoImex, StringComparison.OrdinalIgnoreCase)
            ? DesempenoImex
            : OperacionNld;

        if (request.ExpiraEnDias is < 0 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ExpiraEnDias), "La vigencia debe estar entre 0 y 3650 días.");
        }

        await EnsureSchemaAsync(cancellationToken);

        string token = ToBase64Url(RandomNumberGenerator.GetBytes(TokenBytes));
        string tokenHash = HashToken(token);
        string tokenHint = token[..8];
        string tokenEncrypted = _tokenProtector.Protect(token);
        DateTime? expiration = request.ExpiraEnDias == 0
            ? null
            : DateTime.Now.AddDays(request.ExpiraEnDias);

        using IDbConnection connection = _db.CreatePmmutileriaConnection();
        long id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            @"INSERT INTO monitor_tv_links
                (token_hash, token_hint, token_encrypted, nombre, tipo_pantalla, usucrea, fexpira)
              VALUES (@tokenHash, @tokenHint, @tokenEncrypted, @nombre, @tipoPantalla, @usuario, @expiration);
              SELECT LAST_INSERT_ID();",
            new
            {
                tokenHash,
                tokenHint,
                tokenEncrypted,
                nombre = name,
                tipoPantalla,
                usuario = string.IsNullOrWhiteSpace(usuario) ? "sistema" : usuario,
                expiration,
            },
            cancellationToken: cancellationToken));

        return new CrearMonitorTvLinkResult
        {
            Id = id,
            Token = token,
            TipoPantalla = tipoPantalla,
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
                     tipo_pantalla AS TipoPantalla,
                     token_hint AS TokenHint,
                     (token_encrypted IS NOT NULL) AS CanCopy,
                     usucrea AS UsuarioCrea,
                     fcrea AS FechaCreacion,
                     fexpira AS FechaExpiracion,
                     revocado AS Revocado
              FROM monitor_tv_links
              ORDER BY revocado ASC, fcrea DESC",
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task<string?> GetTokenAsync(long id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        using IDbConnection connection = _db.CreatePmmutileriaConnection();
        MonitorTvLinkTokenRow? row = await connection.QueryFirstOrDefaultAsync<MonitorTvLinkTokenRow>(new CommandDefinition(
            @"SELECT token_hash AS TokenHash,
                     token_encrypted AS TokenEncrypted
              FROM monitor_tv_links
              WHERE id = @id
                AND revocado = 0
                AND (fexpira IS NULL OR fexpira > NOW())
              LIMIT 1",
            new { id },
            cancellationToken: cancellationToken));

        if (row == null || string.IsNullOrWhiteSpace(row.TokenEncrypted))
        {
            return null;
        }

        try
        {
            string token = _tokenProtector.Unprotect(row.TokenEncrypted);
            return string.Equals(HashToken(token), row.TokenHash, StringComparison.Ordinal)
                ? token
                : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public async Task<MonitorTvLinkToken?> RenewLegacyTokenAsync(long id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        string token = ToBase64Url(RandomNumberGenerator.GetBytes(TokenBytes));
        string tokenHash = HashToken(token);
        string tokenHint = token[..8];
        string tokenEncrypted = _tokenProtector.Protect(token);

        using IDbConnection connection = _db.CreatePmmutileriaConnection();
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            @"UPDATE monitor_tv_links
              SET token_hash = @tokenHash,
                  token_hint = @tokenHint,
                  token_encrypted = @tokenEncrypted
              WHERE id = @id
                AND revocado = 0
                AND token_encrypted IS NULL
                AND (fexpira IS NULL OR fexpira > NOW())",
            new { id, tokenHash, tokenHint, tokenEncrypted },
            cancellationToken: cancellationToken));

        return affected == 0 ? null : new MonitorTvLinkToken(token, tokenHint);
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
                     DATE_FORMAT(fexpira, '%Y-%m-%d %H:%i:%s') AS FechaExpiracion,
                     tipo_pantalla AS TipoPantalla
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

        return new MonitorTvLinkAccess(row.Id, expiration, row.TipoPantalla);
    }

    public async Task<string> EmitirTicketTiempoRealAsync(long monitorTvLinkId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        string ticket = ToBase64Url(RandomNumberGenerator.GetBytes(TokenBytes));
        string ticketHash = HashToken(ticket);
        using IDbConnection connection = _db.CreatePmmutileriaConnection();
        int inserted = await connection.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO monitor_tv_realtime_tickets (ticket_hash, monitor_tv_link_id, fexpira)
              SELECT @ticketHash, id, DATE_ADD(NOW(), INTERVAL 60 SECOND)
              FROM monitor_tv_links
              WHERE id = @monitorTvLinkId
                AND revocado = 0
                AND (fexpira IS NULL OR fexpira > NOW())",
            new { ticketHash, monitorTvLinkId },
            cancellationToken: cancellationToken));

        if (inserted == 0)
        {
            throw new InvalidOperationException("El enlace de Monitor TV ya no está disponible.");
        }

        return ticket;
    }

    public async Task<bool> ConsumirTicketTiempoRealAsync(string ticket, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 200)
        {
            return false;
        }

        await EnsureSchemaAsync(cancellationToken);
        using IDbConnection connection = _db.CreatePmmutileriaConnection();
        if (connection.State != ConnectionState.Open) connection.Open();
        using IDbTransaction transaction = connection.BeginTransaction();

        long? ticketId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            @"SELECT t.id
              FROM monitor_tv_realtime_tickets t
              INNER JOIN monitor_tv_links l ON l.id = t.monitor_tv_link_id
              WHERE t.ticket_hash = @ticketHash
                AND t.fconsumido IS NULL
                AND t.fexpira > NOW()
                AND l.revocado = 0
                AND (l.fexpira IS NULL OR l.fexpira > NOW())
              LIMIT 1
              FOR UPDATE",
            new { ticketHash = HashToken(ticket) },
            transaction,
            cancellationToken: cancellationToken));

        if (!ticketId.HasValue)
        {
            transaction.Rollback();
            return false;
        }

        int updated = await connection.ExecuteAsync(new CommandDefinition(
            @"UPDATE monitor_tv_realtime_tickets
              SET fconsumido = NOW()
              WHERE id = @id AND fconsumido IS NULL AND fexpira > NOW()",
            new { id = ticketId.Value },
            transaction,
            cancellationToken: cancellationToken));

        if (updated != 1)
        {
            transaction.Rollback();
            return false;
        }

        transaction.Commit();
        return true;
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
                    token_encrypted TEXT NULL,
                    nombre VARCHAR(100) NOT NULL,
                    tipo_pantalla VARCHAR(30) NOT NULL DEFAULT 'OPERACION_NLD',
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

            int encryptedTokenColumnCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                @"SELECT COUNT(*)
                  FROM information_schema.COLUMNS
                  WHERE TABLE_SCHEMA = DATABASE()
                    AND TABLE_NAME = 'monitor_tv_links'
                    AND COLUMN_NAME = 'token_encrypted'",
                cancellationToken: cancellationToken));
            if (encryptedTokenColumnCount == 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "ALTER TABLE monitor_tv_links ADD COLUMN token_encrypted TEXT NULL AFTER token_hint",
                    cancellationToken: cancellationToken));
            }

            int screenTypeColumnCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                @"SELECT COUNT(*)
                  FROM information_schema.COLUMNS
                  WHERE TABLE_SCHEMA = DATABASE()
                    AND TABLE_NAME = 'monitor_tv_links'
                    AND COLUMN_NAME = 'tipo_pantalla'",
                cancellationToken: cancellationToken));
            if (screenTypeColumnCount == 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "ALTER TABLE monitor_tv_links ADD COLUMN tipo_pantalla VARCHAR(30) NOT NULL DEFAULT 'OPERACION_NLD' AFTER nombre",
                    cancellationToken: cancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                @"CREATE TABLE IF NOT EXISTS monitor_tv_realtime_tickets (
                    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                    ticket_hash CHAR(64) NOT NULL,
                    monitor_tv_link_id BIGINT UNSIGNED NOT NULL,
                    fexpira DATETIME NOT NULL,
                    fconsumido DATETIME NULL,
                    PRIMARY KEY (id),
                    UNIQUE KEY uq_monitor_tv_realtime_tickets_hash (ticket_hash),
                    KEY ix_monitor_tv_realtime_tickets_expiry (fexpira),
                    KEY ix_monitor_tv_realtime_tickets_link (monitor_tv_link_id, fexpira)
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
