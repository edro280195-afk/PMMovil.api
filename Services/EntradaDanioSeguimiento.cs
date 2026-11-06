using System.Data;

using Dapper;

using Microsoft.Extensions.Logging;

using PMMovil.Api.Data;

namespace PMMovil.Api.Services;

/// <summary>
/// Cuántas fotos de DAÑO espera el servidor para una entrada, para poder decir «faltan N por llegar» en el enlace público
/// mientras la cola de la app las sube.
///
/// Las fotos de daño NO tienen tabla propia: viven en <c>fotos</c> con <c>ntipodocto = 103</c>, que en
/// <c>tipodocumentos</c> es «DAÑADA» (formulario PREOP04), el apartado de daño del sistema legado; es el mismo criterio que
/// ya usa <c>FotoController</c> para listarlas. Lo único que se guarda aparte es el número esperado, en la columna
/// <c>traf3.danada_fotos_esperadas</c> (script <c>20261106_entrada_danio_esperadas.sql</c>). Todo lo que toca esa columna es
/// TOLERANTE a que el script aún no se haya corrido: nunca debe romper un guardado ni una página; sin la columna,
/// simplemente no hay aviso de «faltan».
/// </summary>
public static class EntradaDanioSeguimiento
{
    private const string AvisoScript = "Falta correr Database/Migrations/20261106_entrada_danio_esperadas.sql";

    /// <summary>
    /// Cuántas fotos de daño espera el servidor tras guardar o actualizar. <c>null</c> = no se sabe (cliente viejo que no
    /// avisa nada y no mandó fotos dentro del guardado) o sin daño declarado: en ambos casos no hay nada que prometer.
    /// </summary>
    public static int? EsperadasAlGuardar(bool danada, int fotosInline, int? fotosPendientes, int yaGuardadas = 0)
    {
        if (!danada) return null;
        if (fotosPendientes is null && fotosInline == 0) return null;
        return Math.Max(0, yaGuardadas) + Math.Max(0, fotosInline) + Math.Max(0, fotosPendientes ?? 0);
    }

    /// <summary>Fotos de daño que todavía no llegan. Nunca negativo; sin daño o sin dato, 0.</summary>
    public static int Faltantes(bool danada, int? esperadas, int llegadas)
        => !danada || esperadas is null ? 0 : Math.Max(0, esperadas.Value - Math.Max(0, llegadas));

    /// <summary>Fotos de daño (ntipodocto 103) que ya hay en <c>fotos</c> para la entrada.</summary>
    public static async Task<int> ContarFotosDanioAsync(BaseRepository db, string cref)
    {
        using IDbConnection conn = db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM fotos WHERE creferencia = @cref AND ntipodocto = 103",
            new { cref = cref.Trim() });
    }

    public static async Task RegistrarEsperadasAsync(BaseRepository db, ILogger logger, string cref, int? esperadas)
    {
        if (string.IsNullOrWhiteSpace(cref)) return;
        try
        {
            using IDbConnection conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE traf3 SET danada_fotos_esperadas = @esperadas WHERE creferencia = @cref",
                new { esperadas, cref = cref.Trim() });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo registrar cuántas fotos de daño espera {Cref}. {Aviso}", cref, AvisoScript);
        }
    }

    public static async Task<int?> LeerEsperadasAsync(BaseRepository db, ILogger logger, string cref)
    {
        try
        {
            using IDbConnection conn = db.CreateConnection();
            return await conn.QueryFirstOrDefaultAsync<int?>(
                "SELECT danada_fotos_esperadas FROM traf3 WHERE creferencia = @cref LIMIT 1",
                new { cref = cref.Trim() });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo leer cuántas fotos de daño espera {Cref}. {Aviso}", cref, AvisoScript);
            return null;
        }
    }

    /// <summary>
    /// Se borró una foto de daño (103): baja en uno lo que se esperaba, para que el enlace no diga «faltan N» por una foto
    /// que alguien eliminó a propósito. Solo si la entrada tenía un número esperado.
    /// </summary>
    public static async Task AjustarAlBorrarFotoAsync(BaseRepository db, ILogger logger, string cref)
    {
        if (string.IsNullOrWhiteSpace(cref)) return;
        try
        {
            using IDbConnection conn = db.CreateConnection();
            await conn.ExecuteAsync(
                @"UPDATE traf3 SET danada_fotos_esperadas = GREATEST(danada_fotos_esperadas - 1, 0)
                  WHERE creferencia = @cref AND danada_fotos_esperadas IS NOT NULL",
                new { cref = cref.Trim() });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo ajustar cuántas fotos de daño espera {Cref}. {Aviso}", cref, AvisoScript);
        }
    }
}
