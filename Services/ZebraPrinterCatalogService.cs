using Dapper;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public class ZebraPrinterCatalogService
{
    private readonly BaseRepository _db;

    public ZebraPrinterCatalogService(BaseRepository db)
    {
        _db = db;
    }

    public async Task<ZebraPrinterListResult> GetActivePrintersAsync(CancellationToken ct)
    {
        return await GetPrintersAsync(activeOnly: true, ct);
    }

    public async Task<ZebraPrinterListResult> GetPrintersAsync(bool activeOnly, CancellationToken ct)
    {
        using var conn = _db.CreatePmmtoolsConnection();
        var items = (await conn.QueryAsync<ZebraPrinterDto>(
            new CommandDefinition(
                $"""
                SELECT
                    id AS Id,
                    name AS Name,
                    host AS Host,
                    port AS Port,
                    location AS Location,
                    terminal_name AS TerminalName,
                    connect_timeout_ms AS ConnectTimeoutMillis,
                    write_timeout_ms AS WriteTimeoutMillis,
                    print_darkness AS PrintDarkness,
                    print_speed_ips AS PrintSpeedIps,
                    print_method AS PrintMethod,
                    media_type AS MediaType,
                    label_top AS LabelTop,
                    left_position AS LeftPosition,
                    CASE WHEN is_active = 1 THEN TRUE ELSE FALSE END AS IsActive
                FROM pmmovil_zebra_printers
                {(activeOnly ? "WHERE is_active = 1" : "")}
                ORDER BY name ASC, id ASC;
                """,
                cancellationToken: ct
            )
        )).ToList();

        return new ZebraPrinterListResult
        {
            Success = true,
            Message = items.Count == 0
                ? "No hay impresoras Zebra configuradas."
                : "Impresoras Zebra cargadas.",
            Items = items
        };
    }

    public async Task<ZebraPrinterSaveResult> SavePrinterAsync(ZebraPrinterSaveRequest request, CancellationToken ct)
    {
        var clean = Normalize(request);
        var validation = Validate(clean);
        if (!validation.Success)
        {
            return validation;
        }

        using var conn = _db.CreatePmmtoolsConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        try
        {
            int id;
            if (clean.Id > 0)
            {
                var rows = await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE pmmovil_zebra_printers
                    SET
                        name = @Name,
                        host = @Host,
                        port = @Port,
                        location = @Location,
                        terminal_name = @TerminalName,
                        connect_timeout_ms = @ConnectTimeoutMillis,
                        write_timeout_ms = @WriteTimeoutMillis,
                        print_darkness = @PrintDarkness,
                        print_speed_ips = @PrintSpeedIps,
                        print_method = @PrintMethod,
                        media_type = @MediaType,
                        label_top = @LabelTop,
                        left_position = @LeftPosition,
                        is_active = @IsActive
                    WHERE id = @Id;
                    """,
                    clean,
                    tx,
                    cancellationToken: ct
                ));

                if (rows == 0)
                {
                    tx.Rollback();
                    return new ZebraPrinterSaveResult
                    {
                        Success = false,
                        Message = "No se encontro la impresora Zebra a actualizar."
                    };
                }

                id = clean.Id;
            }
            else
            {
                id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    """
                    INSERT INTO pmmovil_zebra_printers
                        (name, host, port, location, terminal_name, connect_timeout_ms, write_timeout_ms,
                         print_darkness, print_speed_ips, print_method, media_type, label_top, left_position,
                         is_default, is_active)
                    VALUES
                        (@Name, @Host, @Port, @Location, @TerminalName, @ConnectTimeoutMillis, @WriteTimeoutMillis,
                        @PrintDarkness, @PrintSpeedIps, @PrintMethod, @MediaType, @LabelTop, @LeftPosition,
                        0, @IsActive);
                    SELECT LAST_INSERT_ID();
                    """,
                    clean,
                    tx,
                    cancellationToken: ct
                ));
            }

            var item = await GetPrinterByIdAsync(conn, tx, id, ct);
            tx.Commit();

            return new ZebraPrinterSaveResult
            {
                Success = true,
                Message = clean.Id > 0 ? "Impresora Zebra actualizada." : "Impresora Zebra agregada.",
                Item = item
            };
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static ZebraPrinterSaveRequest Normalize(ZebraPrinterSaveRequest request)
    {
        return new ZebraPrinterSaveRequest
        {
            Id = request.Id,
            Name = request.Name.Trim(),
            Host = request.Host.Trim(),
            Port = request.Port,
            Location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim(),
            TerminalName = string.IsNullOrWhiteSpace(request.TerminalName) ? null : request.TerminalName.Trim(),
            ConnectTimeoutMillis = request.ConnectTimeoutMillis <= 0 ? 4000 : request.ConnectTimeoutMillis,
            WriteTimeoutMillis = request.WriteTimeoutMillis <= 0 ? 8000 : request.WriteTimeoutMillis,
            PrintDarkness = request.PrintDarkness,
            PrintSpeedIps = request.PrintSpeedIps,
            PrintMethod = NormalizePrintMethod(request.PrintMethod),
            MediaType = NormalizeMediaType(request.MediaType),
            LabelTop = request.LabelTop,
            LeftPosition = request.LeftPosition,
            IsActive = request.IsActive
        };
    }

    private static ZebraPrinterSaveResult Validate(ZebraPrinterSaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Fail("Captura el nombre de la impresora.");
        }

        if (string.IsNullOrWhiteSpace(request.Host))
        {
            return Fail("Captura la IP o host de la impresora.");
        }

        if (request.Port is < 1 or > 65535)
        {
            return Fail("El puerto debe estar entre 1 y 65535.");
        }

        if (request.ConnectTimeoutMillis is < 1000 or > 30000)
        {
            return Fail("El timeout de conexion debe estar entre 1000 y 30000 ms.");
        }

        if (request.WriteTimeoutMillis is < 1000 or > 60000)
        {
            return Fail("El timeout de escritura debe estar entre 1000 y 60000 ms.");
        }

        if (request.PrintDarkness is < 0 or > 30)
        {
            return Fail("La oscuridad Zebra debe estar entre 0.0 y 30.0.");
        }

        if (request.PrintSpeedIps is < 2 or > 6)
        {
            return Fail("La velocidad Zebra debe estar entre 2 y 6 ips.");
        }

        if (request.PrintMethod is not null && request.PrintMethod is not ("thermal_transfer" or "direct_thermal"))
        {
            return Fail("El metodo de impresion Zebra no es valido.");
        }

        if (request.MediaType is not null && request.MediaType is not ("continuous" or "gap_notch" or "mark"))
        {
            return Fail("El tipo de material Zebra no es valido.");
        }

        if (request.LabelTop is < -120 or > 120)
        {
            return Fail("El ajuste vertical Zebra debe estar entre -120 y 120 puntos.");
        }

        if (request.LeftPosition is < -9999 or > 9999)
        {
            return Fail("El ajuste horizontal Zebra debe estar entre -9999 y 9999 puntos.");
        }

        return new ZebraPrinterSaveResult { Success = true };
    }

    private static ZebraPrinterSaveResult Fail(string message)
    {
        return new ZebraPrinterSaveResult
        {
            Success = false,
            Message = message
        };
    }

    private static string? NormalizePrintMethod(string? value)
    {
        return value?.Trim().ToLowerInvariant().Replace(' ', '_') switch
        {
            "thermal_transfer" => "thermal_transfer",
            "direct_thermal" => "direct_thermal",
            null or "" => null,
            var normalized => normalized
        };
    }

    private static string? NormalizeMediaType(string? value)
    {
        return value?.Trim().ToLowerInvariant().Replace('/', '_').Replace(' ', '_') switch
        {
            "continuous" => "continuous",
            "gap_notch" => "gap_notch",
            "mark" => "mark",
            null or "" => null,
            var normalized => normalized
        };
    }

    private static async Task<ZebraPrinterDto?> GetPrinterByIdAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        int id,
        CancellationToken ct)
    {
        return await conn.QueryFirstOrDefaultAsync<ZebraPrinterDto>(
            new CommandDefinition(
                """
                SELECT
                    id AS Id,
                    name AS Name,
                    host AS Host,
                    port AS Port,
                    location AS Location,
                    terminal_name AS TerminalName,
                    connect_timeout_ms AS ConnectTimeoutMillis,
                    write_timeout_ms AS WriteTimeoutMillis,
                    print_darkness AS PrintDarkness,
                    print_speed_ips AS PrintSpeedIps,
                    print_method AS PrintMethod,
                    media_type AS MediaType,
                    label_top AS LabelTop,
                    left_position AS LeftPosition,
                    CASE WHEN is_active = 1 THEN TRUE ELSE FALSE END AS IsActive
                FROM pmmovil_zebra_printers
                WHERE id = @Id;
                """,
                new { Id = id },
                tx,
                cancellationToken: ct
            )
        );
    }

    private static async Task EnsureLayoutTableAsync(System.Data.IDbConnection conn)
    {
        await conn.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS pmmovil_zebra_label_layouts (
                id INT NOT NULL AUTO_INCREMENT,
                layout_type VARCHAR(64) NOT NULL,
                layout_json LONGTEXT NOT NULL,
                version INT NOT NULL DEFAULT 1,
                updated_by VARCHAR(80) NOT NULL DEFAULT '',
                updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (id),
                UNIQUE KEY ux_pmmovil_zebra_label_layouts_type (layout_type)
            );
            """);
    }

    public async Task<ZebraLabelLayoutListResult> GetLayoutsAsync(CancellationToken ct)
    {
        using var conn = _db.CreatePmmtoolsConnection();
        await EnsureLayoutTableAsync(conn);

        var items = (await conn.QueryAsync<ZebraLabelLayoutDto>(
            new CommandDefinition(
                """
                SELECT
                    id AS Id,
                    layout_type AS LayoutType,
                    layout_json AS LayoutJson,
                    version AS Version,
                    updated_by AS UpdatedBy,
                    updated_at AS UpdatedAt
                FROM pmmovil_zebra_label_layouts
                ORDER BY layout_type ASC;
                """,
                cancellationToken: ct
            )
        )).ToList();

        return new ZebraLabelLayoutListResult
        {
            Success = true,
            Message = items.Count == 0 ? "No hay plantillas de etiquetas guardadas en el servidor." : "Plantillas de etiquetas cargadas.",
            Items = items
        };
    }

    public async Task<ZebraLabelLayoutDto?> GetLayoutAsync(string layoutType, CancellationToken ct)
    {
        using var conn = _db.CreatePmmtoolsConnection();
        await EnsureLayoutTableAsync(conn);

        return await conn.QueryFirstOrDefaultAsync<ZebraLabelLayoutDto>(
            new CommandDefinition(
                """
                SELECT
                    id AS Id,
                    layout_type AS LayoutType,
                    layout_json AS LayoutJson,
                    version AS Version,
                    updated_by AS UpdatedBy,
                    updated_at AS UpdatedAt
                FROM pmmovil_zebra_label_layouts
                WHERE layout_type = @LayoutType
                LIMIT 1;
                """,
                new { LayoutType = layoutType?.Trim().ToUpperInvariant() },
                cancellationToken: ct
            )
        );
    }

    public async Task<ZebraLabelLayoutSaveResult> SaveLayoutAsync(
        ZebraLabelLayoutSaveRequest request,
        string updatedBy,
        CancellationToken ct)
    {
        string cleanType = (request.LayoutType ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(cleanType))
        {
            return new ZebraLabelLayoutSaveResult
            {
                Success = false,
                Message = "El tipo de plantilla es obligatorio."
            };
        }

        if (string.IsNullOrWhiteSpace(request.LayoutJson))
        {
            return new ZebraLabelLayoutSaveResult
            {
                Success = false,
                Message = "El JSON de la plantilla no puede estar vacío."
            };
        }

        using var conn = _db.CreatePmmtoolsConnection();
        await EnsureLayoutTableAsync(conn);

        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO pmmovil_zebra_label_layouts (layout_type, layout_json, version, updated_by, updated_at)
                VALUES (@LayoutType, @LayoutJson, 1, @UpdatedBy, NOW())
                ON DUPLICATE KEY UPDATE
                    layout_json = VALUES(layout_json),
                    version = version + 1,
                    updated_by = VALUES(updated_by),
                    updated_at = NOW();
                """,
                new
                {
                    LayoutType = cleanType,
                    LayoutJson = request.LayoutJson,
                    UpdatedBy = updatedBy ?? "sistema"
                },
                cancellationToken: ct
            )
        );

        var saved = await GetLayoutAsync(cleanType, ct);

        return new ZebraLabelLayoutSaveResult
        {
            Success = true,
            Message = $"Plantilla {cleanType} guardada correctamente.",
            Item = saved
        };
    }
}
