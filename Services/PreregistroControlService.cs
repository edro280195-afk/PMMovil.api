using System.Data;
using System.Text;
using Dapper;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public partial class PreregistroService
{
    // Algunos eventos de garita no guardaban id_cuenta. La creación conserva
    // esa asociación incluso después de purgar el pre-registro a las 72 horas.
    private const string AuditWhere = """
        FROM preregistro_auditoria a
        WHERE (@IdCuenta IS NULL OR a.id_cuenta = @IdCuenta OR EXISTS (
            SELECT 1 FROM preregistro_auditoria owner
            WHERE owner.folio = a.folio AND owner.id_cuenta = @IdCuenta))
          AND (@Evento = '' OR a.evento = @Evento)
          AND (@Folio = '' OR a.folio = @Folio)
          AND (@Desde IS NULL OR a.fecha >= @Desde)
          AND (@HastaExclusive IS NULL OR a.fecha < @HastaExclusive)
        """;

    private static object AuditParameters(GaritaAuditoriaFiltro filter) => new
    {
        filter.IdCuenta,
        Evento = filter.Evento?.Trim() ?? "",
        Folio = filter.Folio?.Trim() ?? "",
        Desde = filter.Desde?.Date,
        HastaExclusive = filter.Hasta?.Date.AddDays(1),
        Take = filter.TamanoPagina,
        Skip = (filter.Pagina - 1) * filter.TamanoPagina
    };

    /// <summary>Página y conteos del mismo conjunto filtrado, en una sola lectura.</summary>
    public async Task<GaritaAuditoriaPagina> ObtenerAuditoriaPaginaAsync(GaritaAuditoriaFiltro filter, CancellationToken ct)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        using var result = await conn.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) AS Total,
                   COALESCE(SUM(a.evento = 'PLACAS_NO_COINCIDEN'), 0) AS PlacasNoCoinciden
            {AuditWhere};
            SELECT a.id AS Id, a.id_cuenta AS IdCuenta, a.fecha AS Fecha,
                   a.evento AS Evento, a.usuario AS Usuario, a.folio AS Folio,
                   a.ip AS Ip, a.detalle AS Detalle
            {AuditWhere}
            ORDER BY a.fecha DESC, a.id DESC LIMIT @Take OFFSET @Skip;
            """, AuditParameters(filter), cancellationToken: ct));
        var page = await result.ReadSingleAsync<GaritaAuditoriaPagina>();
        page.Items = (await result.ReadAsync<GaritaAuditoriaItem>()).AsList();
        page.Pagina = filter.Pagina;
        page.TamanoPagina = filter.TamanoPagina;
        return page;
    }

    /// <summary>Exporta todas las coincidencias de los filtros, sin limitarse a la página visible.</summary>
    public async Task<byte[]> ExportarAuditoriaAsync(GaritaAuditoriaFiltro filter, CancellationToken ct)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        var rows = await conn.QueryAsync<GaritaAuditoriaItem>(new CommandDefinition($"""
            SELECT a.id AS Id, a.id_cuenta AS IdCuenta, a.fecha AS Fecha,
                   a.evento AS Evento, a.usuario AS Usuario, a.folio AS Folio,
                   a.ip AS Ip, a.detalle AS Detalle
            {AuditWhere} ORDER BY a.fecha DESC, a.id DESC
            """, AuditParameters(filter), commandTimeout: 60, cancellationToken: ct));
        var csv = new StringBuilder("\uFEFFFecha,Evento,Usuario,Folio,Cuenta,IP,Detalle\r\n");
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            csv.AppendLine(string.Join(",", new[] { row.Fecha.ToString("yyyy-MM-dd HH:mm:ss"), row.Evento,
                row.Usuario, row.Folio, row.IdCuenta?.ToString(), row.Ip, row.Detalle }.Select(EscapeAuditCsv)));
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static string EscapeAuditCsv(string? value)
    {
        var text = value ?? "";
        // Un dato capturado por un usuario nunca debe convertirse en fórmula de Excel.
        if (text.TrimStart().StartsWithAnyFormulaCharacter()) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Une auditoría, señales y sellos reales de caseta sin modificar datos operativos.</summary>
    public async Task<PreregistroFicha?> ObtenerFichaAsync(string folio, CancellationToken ct)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        var record = await conn.QuerySingleOrDefaultAsync<PreregistroAdminItem>(new CommandDefinition("""
            SELECT folio, nunidad, placas, operador, transporu AS NombreLinea, idbodega AS IdBodega,
                   CASE WHEN estatus = 'VIGENTE' AND fexpira < NOW() THEN 'EXPIRADO' ELSE estatus END AS Estatus,
                   fcrea AS FechaCrea, fexpira AS FechaExpira, eta_minutos AS EtaMinutos, eta_km AS EtaKm,
                   fubicacion AS FechaUbicacion, fuso AS FechaUso, casetaid AS CasetaId, usuario_uso AS UsuarioUso
            FROM caseta_preregistro WHERE folio = @folio AND fpurga >= NOW()
            """, new { folio }, cancellationToken: ct));
        var events = (await conn.QueryAsync<GaritaTimelineEvento>(new CommandDefinition("""
            SELECT CONCAT('audit-', id) AS Id, fecha AS Fecha, evento AS Evento, usuario AS Usuario, detalle AS Detalle
            FROM preregistro_auditoria WHERE folio = @folio ORDER BY fecha, id
            """, new { folio }, cancellationToken: ct))).AsList();
        if (record is null && events.Count == 0) return null;

        var points = await ObtenerRecorridoAsync(folio, 2000, ct);
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM caseta_preregistro_ubicacion WHERE folio = @folio AND fpurga >= NOW()",
            new { folio }, cancellationToken: ct));
        if (record is not null && !events.Any(e => e.Evento == PreregistroEvento.CreaPrereg))
            events.Add(new() { Id = "creacion", Fecha = record.FechaCrea, Evento = PreregistroEvento.CreaPrereg });
        if (record?.Estatus == PreregistroEstatus.Expirado)
            events.Add(new() { Id = "expiracion", Fecha = record.FechaExpira, Evento = "EXPIRADO" });
        var first = points.LastOrDefault();
        var last = points.FirstOrDefault();
        if (first is not null)
            events.Add(new() { Id = "senal-inicial", Fecha = first.Fecha, Evento = "UBICACION", Detalle = "Primera señal disponible del recorrido." });
        if (last is not null && last != first)
            events.Add(new() { Id = "senal-reciente", Fecha = last.Fecha, Evento = "UBICACION", Detalle = $"Última señal disponible. {total} señales conservadas." });
        string? casetaId = record?.CasetaId;
        // USA_QR conserva el enlace cuando el registro temporal ya se purgó.
        casetaId ??= events.LastOrDefault(e => e.Evento == PreregistroEvento.UsaQr && e.Detalle?.StartsWith("casetaid=") == true)?.Detalle?[9..].Trim();
        if (!string.IsNullOrWhiteSpace(casetaId))
        {
            using IDbConnection traffic = _db.CreateConnection();
            var stamps = await traffic.QueryAsync<GaritaTimelineEvento>(new CommandDefinition("""
                SELECT 'caseta-arribo' AS Id, farribo AS Fecha, 'ENTRADA_CASETA' AS Evento,
                       usucrea AS Usuario, casetaid AS Detalle
                FROM caseta WHERE casetaid = @casetaId AND YEAR(farribo) >= 2000
                UNION ALL
                SELECT 'caseta-salida', fsalida, 'SALIDA_CASETA', ususalida, relacion
                FROM caseta WHERE casetaid = @casetaId AND YEAR(fsalida) >= 2000
                """, new { casetaId }, cancellationToken: ct));
            events.AddRange(stamps);
        }
        return new PreregistroFicha { Folio = folio, Registro = record, CasetaId = casetaId,
            Eventos = events.OrderBy(e => e.Fecha).ThenBy(e => e.Id).ToList(), Recorrido = points, TotalSenales = total };
    }
}

internal static class AuditCsvExtensions
{
    internal static bool StartsWithAnyFormulaCharacter(this string value) =>
        value.Length > 0 && "=+-@\t\r\n".Contains(value[0]);
}
