using System.Data;

using Dapper;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Laboratorio biometrico facial. Guarda sujetos de prueba, sus plantillas
/// (embeddings de SFace) y cada intento de reconocimiento con las condiciones
/// en que ocurrio, para poder contestar con datos si el reconocimiento facial
/// aguanta una bodega real.
///
/// El matching NO se hace aqui: el dispositivo descarga el banco y compara
/// localmente. Asi la prueba refleja como funcionaria de verdad (sin red, sin
/// GPU en el servidor) y de paso mide el tiempo de inferencia en el equipo.
///
/// Vive sobre ReportesDb -- la misma base donde AuthService lee usuarios, para
/// que las claveusuario coincidan sin brincar de conexion.
/// </summary>
public class BiometriaLabService
{
    private readonly BaseRepository _repository;
    private readonly ILogger<BiometriaLabService> _logger;

    public BiometriaLabService(BaseRepository repository, ILogger<BiometriaLabService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<List<BioLabSujeto>> GetSujetosAsync()
    {
        using IDbConnection conn = _repository.CreateReportesConnection();
        var rows = await conn.QueryAsync<BioLabSujeto>(@"
SELECT s.id            AS Id,
       s.claveusuario  AS ClaveUsuario,
       s.nombre        AS Nombre,
       s.notas         AS Notas,
       s.activo        AS Activo,
       COUNT(p.id)     AS Plantillas
FROM bio_lab_sujeto s
LEFT JOIN bio_lab_plantilla p ON p.sujeto_id = s.id
GROUP BY s.id, s.claveusuario, s.nombre, s.notas, s.activo
ORDER BY s.nombre");
        return rows.ToList();
    }

    /// <summary>
    /// Alta idempotente: si la clave ya existe solo refresca el nombre. Asi el
    /// operador puede re-enrolar sin tropezarse con la llave unica.
    /// </summary>
    public async Task<BioLabResult> UpsertSujetoAsync(BioLabSujetoRequest request)
    {
        string clave = (request.ClaveUsuario ?? "").Trim();
        string nombre = (request.Nombre ?? "").Trim();
        if (string.IsNullOrWhiteSpace(clave))
        {
            return new BioLabResult { Success = false, Message = "La clave de usuario es obligatoria." };
        }
        if (string.IsNullOrWhiteSpace(nombre))
        {
            nombre = clave;
        }

        using IDbConnection conn = _repository.CreateReportesConnection();
        await conn.ExecuteAsync(@"
INSERT INTO bio_lab_sujeto (claveusuario, nombre, notas, activo)
VALUES (@clave, @nombre, @notas, 1)
ON DUPLICATE KEY UPDATE nombre = @nombre, notas = @notas, activo = 1",
            new { clave, nombre, notas = request.Notas });

        int id = await conn.ExecuteScalarAsync<int>(
            "SELECT id FROM bio_lab_sujeto WHERE claveusuario = @clave", new { clave });

        return new BioLabResult { Success = true, Id = id, Message = "Sujeto listo." };
    }

    public async Task<BioLabResult> AddPlantillaAsync(BioLabPlantillaRequest request)
    {
        byte[] vector;
        try
        {
            vector = Convert.FromBase64String(request.VectorBase64 ?? "");
        }
        catch (FormatException)
        {
            return new BioLabResult { Success = false, Message = "El vector no es Base64 valido." };
        }

        int esperado = request.Dims * sizeof(float);
        if (vector.Length != esperado)
        {
            return new BioLabResult
            {
                Success = false,
                Message = $"El vector mide {vector.Length} bytes y se esperaban {esperado} ({request.Dims} floats)."
            };
        }

        using IDbConnection conn = _repository.CreateReportesConnection();
        await conn.ExecuteAsync(@"
INSERT INTO bio_lab_plantilla
    (sujeto_id, modelo, dims, vector, luz_lux, luma, nitidez, rostro_px,
     score_deteccion, yaw, roll, dispositivo, camara)
VALUES
    (@SujetoId, @Modelo, @Dims, @vector, @LuzLux, @Luma, @Nitidez, @RostroPx,
     @ScoreDeteccion, @Yaw, @Roll, @Dispositivo, @Camara)",
            new
            {
                request.SujetoId,
                request.Modelo,
                request.Dims,
                vector,
                request.LuzLux,
                request.Luma,
                request.Nitidez,
                request.RostroPx,
                request.ScoreDeteccion,
                request.Yaw,
                request.Roll,
                request.Dispositivo,
                request.Camara
            });

        long id = await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
        return new BioLabResult { Success = true, Id = id, Message = "Plantilla guardada." };
    }

    public async Task<BioLabBanco> GetBancoAsync()
    {
        using IDbConnection conn = _repository.CreateReportesConnection();

        var filas = await conn.QueryAsync(@"
SELECT p.id           AS Id,
       p.sujeto_id    AS SujetoId,
       s.claveusuario AS ClaveUsuario,
       s.nombre       AS Nombre,
       p.dims         AS Dims,
       p.modelo       AS Modelo,
       p.vector       AS Vector
FROM bio_lab_plantilla p
INNER JOIN bio_lab_sujeto s ON s.id = p.sujeto_id
WHERE s.activo = 1
ORDER BY s.nombre, p.id");

        var plantillas = new List<BioLabPlantilla>();
        foreach (var fila in filas)
        {
            plantillas.Add(new BioLabPlantilla
            {
                Id = (long)fila.Id,
                SujetoId = (int)fila.SujetoId,
                ClaveUsuario = (string)(fila.ClaveUsuario ?? ""),
                Nombre = (string)(fila.Nombre ?? ""),
                Dims = Convert.ToInt32(fila.Dims),
                Modelo = (string)(fila.Modelo ?? ""),
                VectorBase64 = Convert.ToBase64String((byte[])fila.Vector)
            });
        }

        return new BioLabBanco
        {
            Success = true,
            Plantillas = plantillas,
            Sujetos = await GetSujetosAsync()
        };
    }

    public async Task<BioLabResult> AddPruebaAsync(BioLabPruebaRequest r)
    {
        float? margen = (r.SimTop1.HasValue && r.SimTop2.HasValue)
            ? r.SimTop1.Value - r.SimTop2.Value
            : null;

        using IDbConnection conn = _repository.CreateReportesConnection();
        await conn.ExecuteAsync(@"
INSERT INTO bio_lab_prueba
    (sujeto_esperado_id, sujeto_top1_id, sim_top1, sujeto_top2_id, sim_top2,
     margen, umbral, veredicto, luz_lux, luma, nitidez, rostro_px,
     score_deteccion, yaw, roll, ms_deteccion, ms_embedding, ms_total,
     escenario, pantalla_flash, exposicion, dispositivo, camara)
VALUES
    (@SujetoEsperadoId, @SujetoTop1Id, @SimTop1, @SujetoTop2Id, @SimTop2,
     @margen, @Umbral, @Veredicto, @LuzLux, @Luma, @Nitidez, @RostroPx,
     @ScoreDeteccion, @Yaw, @Roll, @MsDeteccion, @MsEmbedding, @MsTotal,
     @Escenario, @PantallaFlash, @Exposicion, @Dispositivo, @Camara)",
            new
            {
                r.SujetoEsperadoId,
                r.SujetoTop1Id,
                r.SimTop1,
                r.SujetoTop2Id,
                r.SimTop2,
                margen,
                r.Umbral,
                r.Veredicto,
                r.LuzLux,
                r.Luma,
                r.Nitidez,
                r.RostroPx,
                r.ScoreDeteccion,
                r.Yaw,
                r.Roll,
                r.MsDeteccion,
                r.MsEmbedding,
                r.MsTotal,
                r.Escenario,
                r.PantallaFlash,
                r.Exposicion,
                r.Dispositivo,
                r.Camara
            });

        long id = await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
        return new BioLabResult { Success = true, Id = id };
    }

    /// <summary>
    /// Corte por tramos de luz y por escenario. Los tramos estan pensados para
    /// bodega: menos de 10 lux es penumbra real (pasillo sin lampara), 10-50 es
    /// una nave mal iluminada de noche, 50-200 oficina normal, y de ahi para
    /// arriba ya es luz de sobra.
    /// </summary>
    public async Task<BioLabResumen> GetResumenAsync()
    {
        using IDbConnection conn = _repository.CreateReportesConnection();

        var porLuz = (await conn.QueryAsync<BioLabTramoLuz>(@"
SELECT CASE
         WHEN luz_lux IS NULL THEN 'sin sensor'
         WHEN luz_lux <   10  THEN '0-10 lux (penumbra)'
         WHEN luz_lux <   50  THEN '10-50 lux (nave de noche)'
         WHEN luz_lux <  200  THEN '50-200 lux (oficina)'
         WHEN luz_lux < 1000  THEN '200-1000 lux (bien iluminado)'
         ELSE '1000+ lux (exterior)'
       END                                                        AS Tramo,
       COUNT(*)                                                   AS Total,
       SUM(veredicto = 'ACIERTO')                                 AS Aciertos,
       SUM(veredicto = 'FALSO_POSITIVO')                          AS FalsosPositivos,
       SUM(veredicto = 'RECHAZO')                                 AS Rechazos,
       SUM(veredicto = 'SIN_ROSTRO')                              AS SinRostro,
       AVG(NULLIF(sim_top1, 0))                                   AS SimPromedio,
       MIN(CASE WHEN veredicto = 'ACIERTO' THEN sim_top1 END)     AS SimMinima,
       AVG(margen)                                                AS MargenPromedio
FROM bio_lab_prueba
GROUP BY Tramo
ORDER BY MIN(COALESCE(luz_lux, -1))")).ToList();

        var porEscenario = (await conn.QueryAsync<BioLabTramoLuz>(@"
SELECT COALESCE(NULLIF(escenario, ''), 'sin etiqueta')            AS Tramo,
       COUNT(*)                                                   AS Total,
       SUM(veredicto = 'ACIERTO')                                 AS Aciertos,
       SUM(veredicto = 'FALSO_POSITIVO')                          AS FalsosPositivos,
       SUM(veredicto = 'RECHAZO')                                 AS Rechazos,
       SUM(veredicto = 'SIN_ROSTRO')                              AS SinRostro,
       AVG(NULLIF(sim_top1, 0))                                   AS SimPromedio,
       MIN(CASE WHEN veredicto = 'ACIERTO' THEN sim_top1 END)     AS SimMinima,
       AVG(margen)                                                AS MargenPromedio
FROM bio_lab_prueba
GROUP BY Tramo
ORDER BY Total DESC")).ToList();

        var totales = await conn.QueryFirstOrDefaultAsync<BioLabResumen>(@"
SELECT COUNT(*)                          AS TotalPruebas,
       SUM(veredicto = 'ACIERTO')        AS Aciertos,
       SUM(veredicto = 'FALSO_POSITIVO') AS FalsosPositivos,
       SUM(veredicto = 'RECHAZO')        AS Rechazos,
       SUM(veredicto = 'SIN_ROSTRO')     AS SinRostro,
       AVG(ms_total)                     AS MsPromedio
FROM bio_lab_prueba") ?? new BioLabResumen();

        totales.Success = true;
        totales.PorLuz = porLuz;
        totales.PorEscenario = porEscenario;
        return totales;
    }

    /// <summary>Deja el banco en blanco para volver a empezar una corrida.</summary>
    public async Task<BioLabResult> ResetAsync(bool incluirSujetos)
    {
        using IDbConnection conn = _repository.CreateReportesConnection();
        await conn.ExecuteAsync("DELETE FROM bio_lab_prueba");
        if (incluirSujetos)
        {
            // El ON DELETE CASCADE de la llave foranea se lleva las plantillas.
            await conn.ExecuteAsync("DELETE FROM bio_lab_sujeto");
        }
        _logger.LogWarning("Laboratorio biometrico reiniciado. IncluirSujetos={Incluir}", incluirSujetos);
        return new BioLabResult { Success = true, Message = "Laboratorio reiniciado." };
    }
}
