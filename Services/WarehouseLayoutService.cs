using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

using Dapper;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public class WarehouseLayoutService
{
    private const int MaxLayoutBytes = 1_500_000;

    private readonly BaseRepository _db;

    public WarehouseLayoutService(BaseRepository db)
    {
        _db = db;
    }

    public async Task<WarehouseLayoutResponse> GetLayoutAsync(string warehouseId)
    {
        string cleanWarehouseId = NormalizeWarehouseId(warehouseId);

        using IDbConnection conn = _db.CreatePmmtoolsConnection();
        await EnsureTableAsync(conn);

        var row = await conn.QueryFirstOrDefaultAsync<WarehouseLayoutRow>(
            """
            SELECT warehouse_id AS WarehouseId,
                   layout_json  AS LayoutJson,
                   version      AS Version,
                   updated_by   AS UpdatedBy,
                   updated_at   AS UpdatedAt
            FROM warehouse_layouts
            WHERE warehouse_id = @warehouseId
            LIMIT 1
            """,
            new { warehouseId = cleanWarehouseId });

        if (row == null)
        {
            return new WarehouseLayoutResponse
            {
                Success = true,
                WarehouseId = cleanWarehouseId,
                Layout = BuildDefaultLayout(cleanWarehouseId),
                Source = "default",
                Version = 1,
                Message = "Layout base generado."
            };
        }

        JsonNode? layout;
        try
        {
            layout = JsonNode.Parse(row.LayoutJson);
        }
        catch
        {
            layout = BuildDefaultLayout(cleanWarehouseId);
        }

        return new WarehouseLayoutResponse
        {
            Success = true,
            WarehouseId = cleanWarehouseId,
            Layout = layout,
            Source = "database",
            Version = row.Version,
            UpdatedBy = row.UpdatedBy,
            UpdatedAt = row.UpdatedAt
        };
    }

    public async Task<WarehouseLayoutSaveResponse> SaveLayoutAsync(
        string warehouseId,
        WarehouseLayoutSaveRequest request,
        string usuarioActual)
    {
        string cleanWarehouseId = NormalizeWarehouseId(warehouseId);

        if (request.Layout.ValueKind != JsonValueKind.Object)
        {
            return new WarehouseLayoutSaveResponse
            {
                Success = false,
                WarehouseId = cleanWarehouseId,
                Message = "El layout debe ser un objeto JSON."
            };
        }

        string layoutJson = JsonSerializer.Serialize(request.Layout);
        if (layoutJson.Length > MaxLayoutBytes)
        {
            return new WarehouseLayoutSaveResponse
            {
                Success = false,
                WarehouseId = cleanWarehouseId,
                Message = "El layout es demasiado grande."
            };
        }

        var layoutNode = JsonNode.Parse(layoutJson)?.AsObject();
        if (layoutNode == null)
        {
            return new WarehouseLayoutSaveResponse
            {
                Success = false,
                WarehouseId = cleanWarehouseId,
                Message = "No se pudo leer el layout."
            };
        }

        layoutNode["warehouseId"] = cleanWarehouseId;
        layoutNode["savedAt"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        layoutJson = layoutNode.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        using IDbConnection conn = _db.CreatePmmtoolsConnection();
        await EnsureTableAsync(conn);

        await conn.ExecuteAsync(
            """
            INSERT INTO warehouse_layouts (warehouse_id, layout_json, version, updated_by, updated_at)
            VALUES (@warehouseId, @layoutJson, 1, @updatedBy, NOW())
            ON DUPLICATE KEY UPDATE
                layout_json = VALUES(layout_json),
                version = version + 1,
                updated_by = VALUES(updated_by),
                updated_at = VALUES(updated_at)
            """,
            new
            {
                warehouseId = cleanWarehouseId,
                layoutJson,
                updatedBy = usuarioActual ?? "sistema"
            });

        int version = await conn.ExecuteScalarAsync<int>(
            "SELECT version FROM warehouse_layouts WHERE warehouse_id = @warehouseId",
            new { warehouseId = cleanWarehouseId });

        return new WarehouseLayoutSaveResponse
        {
            Success = true,
            WarehouseId = cleanWarehouseId,
            Version = version,
            Message = "Mapa de bodega guardado."
        };
    }

    /// <summary>
    /// Ocupacion viva: por cada ubicacion con mercancia pendiente en bodega,
    /// regresa bultos y entradas. La salida real se toma de etiqueta_mcia.estatus:
    /// CARGADO/SALIDA/ELIMINAR ya no ocupan ubicacion. Las directas (D) sin
    /// etiqueta tambien ocupan; las consolidadas (C) sin etiqueta se omiten
    /// porque no estan en piso/bodega.
    /// </summary>
    public async Task<WarehouseOccupancyResponse> GetOccupancyAsync(string warehouseId)
    {
        string cleanWarehouseId = NormalizeWarehouseId(warehouseId);

        using IDbConnection conn = _db.CreateConnection();

        const string sql = """
            SELECT
                TRIM(d.ubicacion) AS Ubicacion,
                d.creferencia AS Creferencia,
                COALESCE(t.ref, '') AS Ref,
                COALESCE(t.tipop, '') AS Tipop,
                COALESCE(t.cclienteidtraf, '') AS ClienteId,
                COALESCE(c.cnombre, '') AS Cliente,
                d.nlineno AS Linea,
                DATE_FORMAT(t.dfechaarribo, '%Y-%m-%dT%H:%i') AS FechaArribo,
                COALESCE(NULLIF(TRIM(d.cdescripcion), ''), '') AS Descripcion,
                COALESCE(NULLIF(TRIM(d.cclase), ''), '') AS Clase,
                COALESCE(d.ncantidad, 0) AS CantidadDetalle,
                COUNT(e.id_etiqueta_mcia) AS EtiquetasTotal,
                COALESCE(SUM(CASE
                    WHEN e.id_etiqueta_mcia IS NOT NULL
                     AND COALESCE(NULLIF(TRIM(e.estatus), ''), '') NOT IN ('CARGADO', 'SALIDA', 'ELIMINAR')
                    THEN 1 ELSE 0
                END), 0) AS EtiquetasPendientes,
                COALESCE(SUM(CASE
                    WHEN COALESCE(NULLIF(TRIM(e.estatus), ''), '') = 'CARGADO'
                    THEN 1 ELSE 0
                END), 0) AS EtiquetasCargadas,
                COALESCE(SUM(CASE
                    WHEN COALESCE(NULLIF(TRIM(e.estatus), ''), '') = 'SALIDA'
                    THEN 1 ELSE 0
                END), 0) AS EtiquetasSalida,
                COALESCE(SUM(CASE
                    WHEN COALESCE(NULLIF(TRIM(e.estatus), ''), '') = 'ELIMINAR'
                    THEN 1 ELSE 0
                END), 0) AS EtiquetasEliminar
            FROM trafdet d
            JOIN traf t ON t.creferencia = d.creferencia
            LEFT JOIN clientes c ON c.cidcte = t.cclienteidtraf
            LEFT JOIN etiqueta_mcia e
              ON e.creferencia = d.creferencia
             AND e.nlineno = d.nlineno
            WHERE d.ubicacion IS NOT NULL
              AND TRIM(d.ubicacion) <> ''
              AND t.tipop IN ('C', 'D')
              AND COALESCE(t.estatus, 0) <> 9
            GROUP BY
                TRIM(d.ubicacion),
                d.creferencia,
                t.ref,
                t.tipop,
                t.cclienteidtraf,
                c.cnombre,
                d.nlineno,
                t.dfechaarribo,
                d.cdescripcion,
                d.cclase,
                d.ncantidad,
                t.fcreatrafico
            HAVING EtiquetasPendientes > 0
                OR (UPPER(COALESCE(t.tipop, '')) = 'D' AND EtiquetasTotal = 0)
            ORDER BY Ubicacion, t.fcreatrafico DESC, d.creferencia DESC, d.nlineno ASC
            """;

        var rows = (await conn.QueryAsync<OccupancyRow>(sql)).ToList();

        var locations = new Dictionary<string, LocationOccupancy>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Ubicacion)) continue;

            if (!locations.TryGetValue(r.Ubicacion, out var loc))
            {
                loc = new LocationOccupancy { Status = "ocupada" };
                locations[r.Ubicacion] = loc;
            }

            int pendingLabels = (int)r.EtiquetasPendientes;
            int loadedLabels = (int)r.EtiquetasCargadas;
            int salidaLabels = (int)r.EtiquetasSalida;
            bool isDirectWithoutLabels = string.Equals(r.Tipop, "D", StringComparison.OrdinalIgnoreCase)
                && r.EtiquetasTotal == 0;
            int unlabeledBultos = isDirectWithoutLabels
                ? Math.Max(1, (int)Math.Round(r.CantidadDetalle))
                : 0;
            int bultos = pendingLabels + unlabeledBultos;
            string confidence = isDirectWithoutLabels ? "directa" : "alta";
            string estado = isDirectWithoutLabels
                ? "directa"
                : loadedLabels + salidaLabels > 0
                    ? "parcial"
                    : "ocupada";

            loc.Bultos += bultos;
            loc.EtiquetasPendientes += pendingLabels;
            loc.EtiquetasCargadas += loadedLabels;
            loc.EtiquetasSalida += salidaLabels;
            loc.BultosSinEtiqueta += unlabeledBultos;
            loc.DirectasSinEtiqueta += unlabeledBultos;
            loc.EntradasCount++;

            if (!string.IsNullOrWhiteSpace(r.Clase) && bultos > 0)
            {
                var claseAgg = loc.Clases.FirstOrDefault(x =>
                    string.Equals(x.Clase, r.Clase, StringComparison.OrdinalIgnoreCase));
                if (claseAgg == null)
                {
                    claseAgg = new ClaseOccupancy { Clase = r.Clase };
                    loc.Clases.Add(claseAgg);
                }
                claseAgg.Bultos += bultos;
            }

            if (loc.Entradas.Count < 40)
            {
                loc.Entradas.Add(new OccupancyEntry
                {
                    Creferencia = r.Creferencia ?? "",
                    Ref = r.Ref ?? "",
                    Tipop = r.Tipop ?? "",
                    TipoOperacion = OperationTypeLabel(r.Tipop),
                    Linea = r.Linea,
                    Cliente = r.Cliente ?? "",
                    ClienteId = r.ClienteId ?? "",
                    Bultos = bultos,
                    EtiquetasPendientes = pendingLabels,
                    EtiquetasCargadas = loadedLabels,
                    EtiquetasSalida = salidaLabels,
                    BultosSinEtiqueta = unlabeledBultos,
                    DirectasSinEtiqueta = unlabeledBultos,
                    Estado = estado,
                    Confidence = confidence,
                    FechaArribo = r.FechaArribo ?? "",
                    Descripcion = r.Descripcion ?? "",
                    Clase = r.Clase ?? ""
                });
            }
        }

        foreach (var loc in locations.Values)
        {
            loc.EntradasOmitidas = Math.Max(0, loc.EntradasCount - loc.Entradas.Count);
            loc.Confidence = loc.DirectasSinEtiqueta > 0
                ? loc.EtiquetasPendientes > 0 ? "mixta" : "directa"
                : "alta";
            loc.Status = loc.EtiquetasPendientes > 0
                         && (loc.EtiquetasCargadas > 0 || loc.EtiquetasSalida > 0 || loc.BultosSinEtiqueta > 0)
                ? "parcial"
                : "ocupada";

            loc.Clases = loc.Clases.OrderByDescending(x => x.Bultos).ToList();
            loc.ClasePrincipal = loc.Clases.Count > 0 ? loc.Clases[0].Clase : "";
        }

        return new WarehouseOccupancyResponse
        {
            Success = true,
            WarehouseId = cleanWarehouseId,
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Locations = locations
        };
    }

    private static async Task EnsureTableAsync(IDbConnection conn)
    {
        await conn.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS warehouse_layouts (
                id INT NOT NULL AUTO_INCREMENT,
                warehouse_id VARCHAR(64) NOT NULL,
                layout_json LONGTEXT NOT NULL,
                version INT NOT NULL DEFAULT 1,
                updated_by VARCHAR(80) NOT NULL DEFAULT '',
                updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (id),
                UNIQUE KEY ux_warehouse_layouts_warehouse_id (warehouse_id)
            )
            """);
    }

    private static string NormalizeWarehouseId(string warehouseId)
    {
        string clean = (warehouseId ?? "").Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(clean) ? "MAQUILA" : clean;
    }

    private static JsonObject BuildDefaultLayout(string warehouseId)
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["version"] = 1,
            ["warehouseId"] = warehouseId,
            ["name"] = warehouseId == "MAQUILA" ? "Bodega Maquila" : warehouseId,
            ["canvas"] = new JsonObject
            {
                ["width"] = 1200,
                ["height"] = 640,
                ["snapSize"] = 10
            },
            ["structures"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "outer-wall",
                    ["label"] = "Perimetro",
                    ["type"] = "wall",
                    ["shape"] = "rect",
                    ["x"] = 55,
                    ["y"] = 58,
                    ["width"] = 1090,
                    ["height"] = 516,
                    ["stroke"] = "#5a5b5e",
                    ["strokeWidth"] = 5
                },
                new JsonObject
                {
                    ["id"] = "wall-left-top",
                    ["label"] = "Muro izquierdo superior",
                    ["type"] = "wall",
                    ["shape"] = "line",
                    ["points"] = Points(272, 58, 272, 252),
                    ["stroke"] = "#5a5b5e",
                    ["strokeWidth"] = 5
                },
                new JsonObject
                {
                    ["id"] = "wall-left-bottom",
                    ["label"] = "Muro izquierdo inferior",
                    ["type"] = "wall",
                    ["shape"] = "line",
                    ["points"] = Points(272, 356, 272, 574),
                    ["stroke"] = "#5a5b5e",
                    ["strokeWidth"] = 5
                },
                new JsonObject
                {
                    ["id"] = "wall-middle",
                    ["label"] = "Muro central",
                    ["type"] = "wall",
                    ["shape"] = "line",
                    ["points"] = Points(558, 58, 558, 574),
                    ["stroke"] = "#5a5b5e",
                    ["strokeWidth"] = 5
                },
                new JsonObject
                {
                    ["id"] = "wall-right-top",
                    ["label"] = "Muro derecho superior",
                    ["type"] = "wall",
                    ["shape"] = "line",
                    ["points"] = Points(842, 58, 842, 225),
                    ["stroke"] = "#5a5b5e",
                    ["strokeWidth"] = 5
                },
                new JsonObject
                {
                    ["id"] = "wall-right-bottom",
                    ["label"] = "Muro derecho inferior",
                    ["type"] = "wall",
                    ["shape"] = "line",
                    ["points"] = Points(842, 353, 842, 574),
                    ["stroke"] = "#5a5b5e",
                    ["strokeWidth"] = 5
                },
                new JsonObject
                {
                    ["id"] = "office",
                    ["label"] = "OFICINA",
                    ["type"] = "office",
                    ["shape"] = "rect",
                    ["x"] = 974,
                    ["y"] = 334,
                    ["width"] = 171,
                    ["height"] = 240,
                    ["stroke"] = "#5a5b5e",
                    ["strokeWidth"] = 5,
                    ["fill"] = "#f8fafc"
                }
            },
            ["zones"] = new JsonArray()
        };
    }

    private static JsonArray Points(params int[] values)
    {
        var points = new JsonArray();
        foreach (int value in values)
        {
            points.Add(value);
        }

        return points;
    }

    private static string OperationTypeLabel(string? tipop) => (tipop ?? "").Trim().ToUpperInvariant() switch
    {
        "D" => "Directa",
        "C" => "Consolidada",
        _ => ""
    };

    private sealed class WarehouseLayoutRow
    {
        public string WarehouseId { get; set; } = string.Empty;
        public string LayoutJson { get; set; } = string.Empty;
        public int Version { get; set; }
        public string UpdatedBy { get; set; } = string.Empty;
        public DateTime? UpdatedAt { get; set; }
    }

    private sealed class OccupancyRow
    {
        public string Ubicacion { get; set; } = string.Empty;
        public string Creferencia { get; set; } = string.Empty;
        public string Ref { get; set; } = string.Empty;
        public string Tipop { get; set; } = string.Empty;
        public int Linea { get; set; }
        public string ClienteId { get; set; } = string.Empty;
        public string Cliente { get; set; } = string.Empty;
        public string FechaArribo { get; set; } = string.Empty;
        public decimal CantidadDetalle { get; set; }
        public long EtiquetasTotal { get; set; }
        public long EtiquetasPendientes { get; set; }
        public long EtiquetasCargadas { get; set; }
        public long EtiquetasSalida { get; set; }
        public long EtiquetasEliminar { get; set; }
        public string Descripcion { get; set; } = string.Empty;
        public string Clase { get; set; } = string.Empty;
    }
}
