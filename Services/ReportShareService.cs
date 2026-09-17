using System.Data;

using Dapper;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public record ReportShareTarget(string Tipo, string? Referencia, int? IdReporte);

public interface IReportShareService
{
    Task<(string Token, DateTime Expira)> GenerarLinkVivoAsync(string creferencia, string usuario);
    Task<(string Token, DateTime Expira)> GenerarLinkCongeladoAsync(int idReporte, string usuario);
    Task<ReportShareTarget?> ResolverAsync(string token);
}

/// <summary>
/// Links temporales para "Ver reporte" (Monitor y Validación de Reportes).
/// El botón anterior hacía window.open() directo a un endpoint [Authorize] --
/// nunca llevaba el token, 401 siempre. Este token es de corta duración (20
/// min: pensado para abrirse al toque desde una sesión ya autenticada, no
/// para compartirse como "Seguimiento de entrada"). El token vive en
/// pmmutileria (tabla de soporte, no dato de negocio) -- el PDF en sí sigue
/// generándose/leyéndose de pmmtraf, sin tocar esa lógica.
/// </summary>
public class ReportShareService : IReportShareService
{
    private static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(20);

    private readonly BaseRepository _db;
    private readonly ILogger<ReportShareService> _logger;

    public ReportShareService(BaseRepository db, ILogger<ReportShareService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<(string Token, DateTime Expira)> GenerarLinkVivoAsync(string creferencia, string usuario)
    {
        return await InsertarAsync("vivo", creferencia, null, usuario);
    }

    public async Task<(string Token, DateTime Expira)> GenerarLinkCongeladoAsync(int idReporte, string usuario)
    {
        return await InsertarAsync("congelado", null, idReporte, usuario);
    }

    private async Task<(string Token, DateTime Expira)> InsertarAsync(string tipo, string? referencia, int? idReporte, string usuario)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();
        string token = Guid.NewGuid().ToString("N");
        DateTime expira = DateTime.Now.Add(Vigencia);

        await conn.ExecuteAsync(
            @"INSERT INTO reporte_links_temporales (token, tipo, referencia, id_reporte, usucrea, fexpira)
              VALUES (@token, @tipo, @referencia, @idReporte, @usuario, @expira)",
            new { token, tipo, referencia, idReporte, usuario, expira });

        _logger.LogInformation("Link de reporte generado (tipo={Tipo}) por {Usuario}, expira {Expira}", tipo, usuario, expira);
        return (token, expira);
    }

    public async Task<ReportShareTarget?> ResolverAsync(string token)
    {
        using IDbConnection conn = _db.CreatePmmutileriaConnection();

        dynamic? row = await conn.QueryFirstOrDefaultAsync<object>(
            @"SELECT tipo, referencia, id_reporte, fexpira
              FROM reporte_links_temporales
              WHERE token = @token
              LIMIT 1",
            new { token });

        if (row == null || DateTime.Now > (DateTime)row.fexpira)
        {
            return null;
        }

        return new ReportShareTarget(
            (string)row.tipo,
            (string?)row.referencia,
            row.id_reporte == null ? null : (int)row.id_reporte);
    }
}
