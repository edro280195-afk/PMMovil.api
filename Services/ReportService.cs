using ClosedXML.Excel;

using Dapper;

using MySqlConnector;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public class ReportService
{
    private readonly IConfiguration _config;
    private readonly ILogger<ReportService> _logger;
    private readonly IModulePermissionService _permissionService;

    private readonly string _csReportes;

    public ReportService(
        IConfiguration config,
        ILogger<ReportService> logger,
        IModulePermissionService permissionService)
    {
        _config = config;
        _logger = logger;
        _permissionService = permissionService;
        _csReportes = config.GetConnectionString("ReportesDb")
                     ?? config.GetConnectionString("pmmreportes")
                     ?? config.GetConnectionString("reportes")
                     ?? config.GetConnectionString("Default")
                     ?? throw new InvalidOperationException("Connection string 'ReportesDb' no configurada.");
    }


    public async Task<List<ReporteInfo>> ObtenerCatalogoAsync(
        string usuario,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(usuario)) return [];

        var fullAccess = await _permissionService.HasFullAccessAsync(usuario, cancellationToken);
        using var con = new MySqlConnection(_csReportes);

        const string sql = @"
            SELECT DISTINCT
                r.id_reporte,
                r.nombre,
                r.descripcion,
                GROUP_CONCAT(q.fields_where SEPARATOR ';') AS all_fields
            FROM reporte r
            INNER JOIN query q ON q.id_reporte = r.id_reporte
            WHERE @fullAccess = 1
               OR EXISTS (
                    SELECT 1
                    FROM usuario_reporte ur
                    WHERE ur.id_reporte = r.id_reporte
                      AND TRIM(ur.id_user) = @usuario
               )
            GROUP BY r.id_reporte, r.nombre, r.descripcion
            ORDER BY r.nombre";

        var rows = await con.QueryAsync(new CommandDefinition(
            sql,
            new
            {
                fullAccess = fullAccess ? 1 : 0,
                usuario = usuario.Trim()
            },
            cancellationToken: cancellationToken));
        var result = new List<ReporteInfo>();

        foreach (var row in rows)
        {
            var allFields = ((string?)row.all_fields ?? "").ToUpperInvariant();
            result.Add(new ReporteInfo
            {
                IdReporte = row.id_reporte,
                Nombre = row.nombre,
                Descripcion = row.descripcion ?? "",
                TieneRangoFechas = allFields.Contains("FECHA"),
                TieneTipoFecha = allFields.Contains("TIPOFECHA") || allFields.Contains("FECHA:"),
                TieneCliente = allFields.Contains("CLIENTE"),
                TieneBodega = allFields.Contains("BODEGA"),
                TieneProveedor = allFields.Contains("PROVEEDOR"),
                TieneExtension = allFields.Contains("EXTENSION"),
                TieneFacturo = allFields.Contains("FACTURO"),
                TieneNaviera = allFields.Contains("NAVIERA"),
                TieneModelo = allFields.Contains("MODELO"),
                TieneConsigna = allFields.Contains("CONSIGNA"),
                TienePatente = allFields.Contains("PATENTE"),
                TieneAduana = allFields.Contains("ADUANA"),
                TieneClavePedimento = allFields.Contains("CVEPTO"),
                TieneTipoOp = allFields.Contains("TIPOOP"),
                TienePedimento = allFields.Contains("PEDIMENTO"),
                TieneTrafico = allFields.Contains("TRAFICO"),
                TieneFraccion = allFields.Contains("FRACCION"),
                TieneOrdenamiento = allFields.Contains("ORDENAMIENTO"),
                TieneOtro = allFields.Contains("OTRO"),
                TieneAcumulado = allFields.Contains("ACUMULADO"),
                TieneInactiva = allFields.Contains("INACTIVA"),
                TieneTipoDocumento = allFields.Contains("TIPODOCUMENTO"),
            });
        }

        return result;
    }

    public async Task<ReporteInfo?> ObtenerInfoAsync(
        string idReporte,
        string usuario,
        CancellationToken cancellationToken = default)
    {
        var catalogo = await ObtenerCatalogoAsync(usuario, cancellationToken);
        return catalogo.FirstOrDefault(r => r.IdReporte == idReporte);
    }


    public async Task<List<FiltroOption>> ObtenerClientesAsync()
    {
        using var con = new MySqlConnection(_csReportes);
        var rows = await con.QueryAsync(
            """
            SELECT DISTINCT TRIM(cidcte) AS value, TRIM(cnombre) AS label
            FROM clientes
            WHERE COALESCE(inactivo, 0) = 0
              AND COALESCE(TRIM(cidcte), '') <> ''
              AND COALESCE(TRIM(cnombre), '') <> ''
            ORDER BY label
            """);
        return rows.Select(r => new FiltroOption { Value = r.value, Label = r.label }).ToList();
    }

    public async Task<List<FiltroOption>> ObtenerBodegasAsync()
    {
        using var con = new MySqlConnection(_csReportes);
        var rows = await con.QueryAsync(
            """
            SELECT DISTINCT TRIM(cidbodega) AS value, TRIM(cdescripcion) AS label
            FROM almacens
            WHERE COALESCE(inactivo, 0) = 0
              AND COALESCE(TRIM(cidbodega), '') <> ''
              AND COALESCE(TRIM(cdescripcion), '') <> ''
            ORDER BY label
            """);
        return rows.Select(r => new FiltroOption { Value = r.value, Label = r.label }).ToList();
    }


    public async Task<ReporteResult> GenerarAsync(GenerarReporteRequest req, string usuario)
    {
        var info = await ObtenerInfoAsync(req.IdReporte, usuario);
        if (info == null)
            return Error(req.IdReporte, "Reporte no encontrado.");

        var queries = await ObtenerQueriesAsync(req.IdReporte);
        if (queries.Count == 0)
            return Error(req.IdReporte, "El reporte no tiene queries configuradas.");

        var tablas = new List<TablaReporte>();
        foreach (var q in queries.OrderBy(x => x.Orden))
        {
            try
            {
                var tabla = await EjecutarQueryAsync(q, req);
                tablas.Add(tabla);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al ejecutar query {Id} del reporte {Reporte}", q.Id, req.IdReporte);
                return Error(req.IdReporte, $"Error al ejecutar query '{q.Titulo}': {ex.Message}");
            }
        }

        return new ReporteResult
        {
            Success = true,
            IdReporte = req.IdReporte,
            Nombre = info.Nombre,
            FechaGeneracion = DateTime.Now,
            Tablas = tablas
        };
    }


    public async Task<byte[]> ExportarExcelAsync(GenerarReporteRequest req, string usuario)
    {
        var result = await GenerarAsync(req, usuario);
        if (!result.Success || result.Tablas.Count == 0)
            throw new InvalidOperationException(result.Message ?? "Sin datos");

        using var wb = new XLWorkbook();
        wb.Properties.Author = "PMMovil";

        foreach (var tabla in result.Tablas)
        {
            var sheetName = tabla.Titulo.Length > 31
                ? tabla.Titulo[..31]
                : tabla.Titulo;

            var ws = wb.Worksheets.Add(sheetName);

            for (var c = 0; c < tabla.Columnas.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = tabla.Columnas[c];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0x1E, 0x40, 0xAF);
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            for (var r = 0; r < tabla.Filas.Count; r++)
            {
                var fila = tabla.Filas[r];
                for (var c = 0; c < tabla.Columnas.Count; c++)
                {
                    var col = tabla.Columnas[c];
                    var val = fila.TryGetValue(col, out var v) ? v : null;
                    var cell = ws.Cell(r + 2, c + 1);

                    if (val == null) cell.Value = "";
                    else if (val is DateTime dt) cell.Value = dt;
                    else if (val is decimal dec) cell.Value = (double)dec;
                    else if (val is double dbl) cell.Value = dbl;
                    else if (val is long lng) cell.Value = lng;
                    else if (val is int i) cell.Value = i;
                    else cell.Value = val.ToString() ?? "";

                    if (r % 2 == 1)
                        cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0xF8, 0xFA, 0xFC);
                }
            }

            ws.Columns().AdjustToContents(1, 50);

            ws.SheetView.FreezeRows(1);
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }


    private async Task<List<QueryDefinicion>> ObtenerQueriesAsync(string idReporte)
    {
        using var con = new MySqlConnection(_csReportes);
        const string sql = @"
            SELECT q.id, b.constring AS connection_string,
                   q.select, q.caption, q.where, q.group_order,
                   q.fields_where, q.fecha_esp, q.titulo, q.orden
            FROM query q
            INNER JOIN dbs b ON b.id_base = q.id_base
            WHERE q.id_reporte = @idReporte
            ORDER BY q.orden";

        var rows = await con.QueryAsync(sql, new { idReporte });

        return rows.Select(r => new QueryDefinicion
        {
            Id = (int)r.id,
            ConnectionString = r.connection_string ?? "",
            Select = r.@select ?? "",
            Caption = r.caption ?? "",
            Where = r.@where ?? "",
            GroupOrder = r.group_order ?? "",
            FieldsWhere = r.fields_where ?? "",
            FechaEsp = r.fecha_esp ?? 0,
            Titulo = r.titulo ?? $"tabla_{r.orden}",
            Orden = (int)r.orden
        }).ToList();
    }

    private async Task<TablaReporte> EjecutarQueryAsync(QueryDefinicion q, GenerarReporteRequest req)
    {
        var fieldMap = ParseFieldMap(q.FieldsWhere);

        var (whereClause, parameters) = BuildWhere(req, fieldMap, q.Where);

        string sql;
        if (!string.IsNullOrWhiteSpace(whereClause))
        {
            sql = $"{q.Select} {whereClause} {q.GroupOrder}".Trim();
        }
        else
        {
            var fixedWhere = q.Where.TrimStart().StartsWith("AND ", StringComparison.OrdinalIgnoreCase)
    ? "WHERE " + q.Where[4..]
    : q.Where;
            sql = $"{q.Select} {fixedWhere} {q.GroupOrder}".Trim();
        }

        _logger.LogDebug("Ejecutando query reporte {Titulo}: {Sql}", q.Titulo, sql[..Math.Min(200, sql.Length)]);

        var cs = q.ConnectionString.StartsWith("Server=", StringComparison.OrdinalIgnoreCase)
    ? q.ConnectionString
    : _config.GetConnectionString(q.ConnectionString)
      ?? _csReportes;

        using var con = new MySqlConnection(cs);

        if (q.FechaEsp > 0)
            await con.ExecuteAsync("SET lc_time_names = 'es_ES';");

        var rows = (await con.QueryAsync(sql, parameters as object)).ToList();

        var captionCols = q.Caption.Split(',')
    .Select(c => c.Trim())
    .Where(c => c.Length > 0)
    .ToList();

        var filas = new List<Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            var dict = (IDictionary<string, object?>)row;
            var fila = new Dictionary<string, object?>();
            var keys = dict.Keys.ToList();

            for (var i = 0; i < keys.Count; i++)
            {
                var colName = i < captionCols.Count ? captionCols[i] : keys[i];
                fila[colName] = dict[keys[i]];
            }
            filas.Add(fila);
        }

        var columnas = filas.Count > 0
    ? filas[0].Keys.ToList()
    : captionCols;

        return new TablaReporte
        {
            Titulo = q.Titulo,
            Columnas = columnas,
            Filas = filas,
            TotalFilas = filas.Count
        };
    }

    private (string whereClause, DynamicParameters parameters) BuildWhere(
GenerarReporteRequest req,
Dictionary<string, string> fieldMap,
string fixedWhere)
    {
        var conditions = new List<string>();
        var p = new DynamicParameters();

        if (!string.IsNullOrEmpty(req.IdCliente) && req.IdCliente != "TODOS")
        {
            var field = GetField(fieldMap, "CLIENTE");
            if (!string.IsNullOrEmpty(field))
            {
                conditions.Add($"({field} = @cliente)");
                p.Add("cliente", req.IdCliente);
            }
        }

        if (!string.IsNullOrEmpty(req.FechaInicio) && !string.IsNullOrEmpty(req.FechaFin))
        {
            var field = GetFechaField(fieldMap, req.TipoFecha);
            if (!string.IsNullOrEmpty(field))
            {
                conditions.Add($"({field} BETWEEN @fechaIni AND @fechaFin)");
                p.Add("fechaIni", req.FechaInicio);
                p.Add("fechaFin", req.FechaFin);
            }
        }

        if (req.TiposOp.Count > 0)
        {
            var field = GetField(fieldMap, "TIPOOP");
            if (!string.IsNullOrEmpty(field))
            {
                conditions.Add($"(TRIM({field}) IN @tiposOp)");
                p.Add("tiposOp", req.TiposOp);
            }
        }

        if (!string.IsNullOrEmpty(req.Bodega) && req.Bodega != "TODAS")
        {
            var field = GetField(fieldMap, "BODEGA");
            if (!string.IsNullOrEmpty(field))
            {
                conditions.Add($"(TRIM({field}) = @bodega)");
                p.Add("bodega", req.Bodega);
            }
        }

        AddSimpleFilter(conditions, p, req.Proveedor, "TODOS", "PROVEEDOR", "proveedor", fieldMap);
        AddSimpleFilter(conditions, p, req.Extension, "TODAS", "EXTENSION", "extension", fieldMap);
        AddSimpleFilter(conditions, p, req.Facturo, "TODOS", "FACTURO", "facturo", fieldMap);
        AddSimpleFilter(conditions, p, req.Naviera, "TODAS", "NAVIERA", "naviera", fieldMap);
        AddSimpleFilter(conditions, p, req.Modelo, "TODOS", "MODELO", "modelo", fieldMap);
        AddSimpleFilter(conditions, p, req.Consigna, "TODOS", "CONSIGNA", "consigna", fieldMap);
        AddSimpleFilter(conditions, p, req.Patente, "TODAS", "PATENTE", "patente", fieldMap);
        AddSimpleFilter(conditions, p, req.Aduana, "TODAS", "ADUANA", "aduana", fieldMap);
        AddSimpleFilter(conditions, p, req.ClavePedimento, "TODAS", "CVEPTO", "cvePdto", fieldMap);
        AddSimpleFilter(conditions, p, req.Pedimento, null, "PEDIMENTO", "pedimento", fieldMap);
        AddSimpleFilter(conditions, p, req.Trafico, null, "TRAFICO", "trafico", fieldMap);
        AddSimpleFilter(conditions, p, req.Fraccion, null, "FRACCION", "fraccion", fieldMap);
        AddSimpleFilter(conditions, p, req.TipoDocumento, "TODOS", "TIPODOCUMENTO", "tipoDoc", fieldMap);

        if (req.Inactiva.HasValue)
        {
            var field = GetField(fieldMap, "INACTIVA");
            if (!string.IsNullOrEmpty(field))
            {
                conditions.Add($"({field} = @inactiva)");
                p.Add("inactiva", req.Inactiva.Value ? 1 : 0);
            }
        }

        if (!string.IsNullOrEmpty(req.OtroValor) && !string.IsNullOrEmpty(req.OtroCampo))
        {
            var field = GetField(fieldMap, "OTRO");
            if (!string.IsNullOrEmpty(field))
            {
                conditions.Add($"(TRIM({field}) = @otroValor)");
                p.Add("otroValor", req.OtroValor);
            }
        }

        if (conditions.Count == 0)
            return ("", p);

        var dynamicWhere = "WHERE " + string.Join(" AND ", conditions);

        if (!string.IsNullOrWhiteSpace(fixedWhere))
        {
            var fixed2 = fixedWhere.TrimStart();
            if (fixed2.StartsWith("AND ", StringComparison.OrdinalIgnoreCase))
                dynamicWhere += " " + fixed2;
            else if (!fixed2.StartsWith("WHERE ", StringComparison.OrdinalIgnoreCase))
                dynamicWhere += " AND " + fixed2;
        }

        return (dynamicWhere, p);
    }

    private static string GetField(Dictionary<string, string> fieldMap, string key)
    {
        return fieldMap.TryGetValue(key, out var f) ? f : "";
    }

    private static string GetFechaField(Dictionary<string, string> fieldMap, string? tipoFecha)
    {
        if (string.IsNullOrEmpty(tipoFecha))
            return GetField(fieldMap, "FECHA");

        if (fieldMap.TryGetValue("FECHA", out var campoFecha))
        {
            if (campoFecha.Contains(':'))
            {
                var partes = campoFecha.Split(',');
                foreach (var p in partes)
                {
                    if (p.Contains(tipoFecha, StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = p.IndexOf(':') + 1;
                        if (idx > 0) return p[idx..];
                    }
                }
            }
            return campoFecha;
        }
        return "";
    }

    private static void AddSimpleFilter(
        List<string> conditions, DynamicParameters p,
        string? value, string? todosValue,
        string fieldKey, string paramName,
        Dictionary<string, string> fieldMap)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (!string.IsNullOrEmpty(todosValue) && value == todosValue) return;

        var field = GetField(fieldMap, fieldKey);
        if (string.IsNullOrEmpty(field)) return;

        conditions.Add($"(TRIM({field}) = @{paramName})");
        p.Add(paramName, value);
    }

    private static Dictionary<string, string> ParseFieldMap(string fieldsWhere)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(fieldsWhere)) return map;

        var parts = fieldsWhere.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var idx = part.IndexOf(':');
            if (idx < 0) continue;
            var key = part[..idx].Trim().ToUpperInvariant();
            var value = part[(idx + 1)..].Trim();
            map[key] = value;
        }
        return map;
    }

    private static ReporteResult Error(string idReporte, string msg) =>
        new() { Success = false, IdReporte = idReporte, Message = msg };
}
