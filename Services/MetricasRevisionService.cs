using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using ClosedXML.Excel;
using Dapper;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Fila ya derivada: agrega los campos que la vista necesita (EsSubdivision,
/// TieneFactura, FueraVentana, Extraordinaria, OrigenApp) a partir de una
/// <see cref="MetricaRevisionRow"/> cruda. Separado de la fila cruda para que
/// las reglas de negocio (ventana, subdivisión, extraordinaria) vivan en un
/// solo lugar y no se repitan entre Kpis/Tendencia/Adopción/Evidencia.
/// </summary>
internal sealed class FilaDerivada
{
    public required MetricaRevisionRow Row { get; init; }
    public bool TieneFactura { get; init; }
    public bool FueraVentana { get; init; }
    public bool EsSubdivision { get; init; }
    public bool Extraordinaria { get; init; }
    public bool? OrigenApp { get; init; } // null = no aplica (pendiente/no-requiere/subdivisión)
    public int? TiempoMin { get; init; }
}

/// <summary>
/// Estatus de <c>traf.status</c> (catálogo real de <c>pmmtraf.status</c>,
/// verificado por lectura directa 15-sep-2026): 1 pendiente, 2 revisada,
/// 3 no requiere, 4 "entrada revisada" (excluido, igual que el legado),
/// 5 destrucción, 6 salida especial, 7 salida exportación.
/// </summary>
public static class EstatusRevisionCodigo
{
    public const int Pendiente = 1;
    public const int Revisada = 2;
    public const int NoRequiere = 3;
    public const int EntradaRevisada = 4;
    public const int Destruccion = 5;
    public const int SalidaEspecial = 6;
    public const int SalidaExportacion = 7;
}

public class MetricasRevisionService
{
    private readonly BaseRepository _db;

    public MetricasRevisionService(BaseRepository db)
    {
        _db = db;
    }

    /// <summary>
    /// Trae las filas crudas de <c>traf</c> en un rango de arribo, con los
    /// mismos filtros de columna simple (planta/cliente/revisor) que ya se
    /// pueden resolver en SQL. Lo que requiere campos derivados (origen,
    /// subdivisión, ventana, cross-filter) se aplica después, en memoria —
    /// mismo patrón que <c>MonitorController.GetVelocidadRevisores</c>.
    /// </summary>
    private async Task<List<MetricaRevisionRow>> ObtenerFilasAsync(
        IDbConnection conn, DateTime desde, DateTime hastaInclusive,
        string? planta, string? cliente, string? revisor)
    {
        string plantaWhere = planta switch
        {
            "ATLANTA" => " AND t.planta = '2'",
            "IMEX" => " AND t.planta <> '2'",
            _ => ""
        };
        string clienteWhere = string.IsNullOrWhiteSpace(cliente) ? "" : " AND cl.cnombre LIKE @clienteLike";
        // El nombre "canónico" (ver comentario junto al SELECT) es el mismo
        // que se compara aquí, para que filtrar por revisor no dependa de si
        // esa fila trae el usuario de traftiemposrevision o el de fact.
        string revisorWhere = string.IsNullOrWhiteSpace(revisor) ? "" : " AND COALESCE(NULLIF(TRIM(CONCAT(TRIM(ur.nombre), ' ', TRIM(ur.apaterno))), ''), tr.usurevisa, fact.usurev) = @revisor";

        string sql = @"
            SELECT
                t.creferencia AS Creferencia,
                t.dfechaarribo AS Arribo,
                t.cclienteidtraf AS ClienteId,
                cl.cnombre AS Cliente,
                t.cidmanejo AS Manejo,
                IF(t.planta = '2', 'ATLANTA', 'IMEX') AS Planta,
                t.status AS Status,
                fact.fechaFactura AS FechaFactura,
                fact.total AS TotalFacturas,
                tr.finiciorevision AS FInicioRevision,
                tr.ffinrevision AS FFinRevision,
                COALESCE(NULLIF(TRIM(CONCAT(TRIM(ur.nombre), ' ', TRIM(ur.apaterno))), ''), tr.usurevisa, fact.usurev) AS Revisor,
                -- IFNULL obligatorio: si esta creferencia no tiene NINGUNA fila en
                -- traftiemposrevision (revision manual pura), el LEFT JOIN deja
                -- tr.TieneFotoApp en NULL, y 'NULL = 1' da NULL en MySQL (no 0) --
                -- Dapper no puede mapear eso a un bool no-nullable.
                (IFNULL(tr.TieneFotoApp, 0) = 1) AS TieneSesionApp,
                -- Igual que el reporte legado REP-S-0025: finicia_revision/ffin_revision
                -- de traf3 se llenan por CUALQUIER metodo (app o manual), a diferencia
                -- de traftiemposrevision que es exclusiva de la app. Sirve para saber
                -- si ESTA referencia (aunque sea subdivision) tiene su propia revision
                -- registrada, en vez de asumir que toda subdivision hereda del padre
                -- sin trabajo propio (Eduardo, 17-sep-2026).
                (YEAR(t3.ffin_revision) >= 2000) AS TieneTiemposPropios,
                fact.observaciones AS Observaciones,
                t3.detlbs AS Peso,
                tdet.cantidad AS Cantidad
            FROM traf t
            LEFT JOIN clientes cl ON cl.cidcte = t.cclienteidtraf
            LEFT JOIN traf3 t3 ON t3.creferencia = t.creferencia
            LEFT JOIN (SELECT creferencia, SUM(ncantidad) AS cantidad FROM trafdet GROUP BY creferencia) tdet ON tdet.creferencia = t.creferencia
            LEFT JOIN (
                SELECT cref_agrupada,
                       MAX(fcrea) AS fechaFactura,
                       COUNT(DISTINCT cnofact) AS total,
                       SUBSTRING_INDEX(GROUP_CONCAT(DISTINCT NULLIF(TRIM(usurev), '')), ',', 1) AS usurev,
                       SUBSTRING_INDEX(GROUP_CONCAT(DISTINCT NULLIF(TRIM(obsrev2), '')), ',', 1) AS observaciones
                FROM fact
                GROUP BY cref_agrupada
            ) fact ON fact.cref_agrupada = t.creferencia
            LEFT JOIN (
                -- traftiemposrevision registra una fila CADA VEZ que alguien abre
                -- la entrada en la app, la haya revisado o solo la haya mirado.
                -- Una fila de 'solo mirar' deja finiciorevision en NULL (no arranca
                -- un timer nuevo) pero SI graba su propio ffinrevision -- tomar
                -- MAX(ffinrevision) sobre todas las filas inflaba el tiempo con
                -- aperturas posteriores de dias despues (caso real Eduardo,
                -- 17-sep-2026: 01C263594110000, Kevin revisa en 5 min, luego el
                -- mismo Kevin y despues Eduardo la abren 4 veces mas solo a
                -- mirarla, y el ultimo vistazo -dos dias despues- se contaba como
                -- el fin de la revision). Por eso ffinrevision/usurevisa solo se
                -- toman de filas que TAMBIEN tengan finiciorevision real -- esas
                -- son las que de verdad representan trabajo, no solo apertura.
                SELECT creferencia,
                       MIN(CASE WHEN YEAR(finiciorevision) >= 2000 THEN finiciorevision END) AS finiciorevision,
                       MAX(CASE WHEN YEAR(finiciorevision) >= 2000 THEN ffinrevision END) AS ffinrevision,
                       SUBSTRING_INDEX(GROUP_CONCAT(DISTINCT CASE WHEN YEAR(finiciorevision) >= 2000 THEN NULLIF(TRIM(usurevisa), '') END ORDER BY ffinrevision DESC), ',', 1) AS usurevisa,
                       -- traftiemposrevision trae varias filas placeholder por
                       -- creferencia (reintentos/eventos internos de la app sin
                       -- ftomadocumento real) -- contar solo si la fila existe
                       -- sobrecontaba el origen APP casi 4x (462 vs 145 reales en
                       -- lo que va de septiembre). Solo cuenta si de verdad se
                       -- tomo una foto (Eduardo, 17-sep-2026).
                       MAX(CASE WHEN YEAR(ftomadocumento) >= 2000 THEN 1 ELSE 0 END) AS TieneFotoApp
                FROM traftiemposrevision
                GROUP BY creferencia
            ) tr ON tr.creferencia = t.creferencia
            -- Resuelve el usuario (de traftiemposrevision o de fact, cualquiera
            -- que traiga la fila) contra el catalogo real de usuarios: sin esto,
            -- la MISMA persona aparecia dos veces, una vez como 'alarumbe'
            -- (clave de traftiemposrevision) y otra como 'ARMANDO LARUMBE'
            -- (nombre libre en fact.usurev), reportado por Eduardo 17-sep-2026.
            LEFT JOIN usuarios ur ON TRIM(ur.claveusuario) = TRIM(COALESCE(tr.usurevisa, fact.usurev))
            WHERE t.tipop = 'c'
              AND t.estatus <> '9'
              AND t.status <> @entradaRevisada
              AND t.dfechaarribo BETWEEN @desde AND @hasta"
              + plantaWhere + clienteWhere + revisorWhere;

        var rows = await conn.QueryAsync<MetricaRevisionRow>(sql, new
        {
            desde = desde.ToString("yyyy-MM-dd 00:00:00"),
            hasta = hastaInclusive.ToString("yyyy-MM-dd 23:59:59"),
            entradaRevisada = EstatusRevisionCodigo.EntradaRevisada,
            clienteLike = "%" + cliente + "%",
            revisor
        });
        return rows.ToList();
    }

    /// <summary>
    /// Regla de ventana confirmada por Eduardo (16-sep-2026): a tiempo si la
    /// factura se capturó antes del día de arribo, o el mismo día antes de
    /// las 17:00. Fuera de esa ventana y aun así revisada = EXTRAORDINARIA.
    /// </summary>
    private static bool CalcularFueraVentana(DateTime arribo, DateTime? fechaFactura)
    {
        if (!fechaFactura.HasValue) return false;
        DateTime arriboDia = arribo.Date;
        DateTime facturaDia = fechaFactura.Value.Date;
        if (facturaDia < arriboDia) return false;
        if (facturaDia == arriboDia && fechaFactura.Value.TimeOfDay <= new TimeSpan(17, 0, 0)) return false;
        return true;
    }

    /// <summary>
    /// Subdivisión: el cliente pide separar bultos de una entrada ya
    /// dispuesta y mandarlos aparte — genera una referencia hija con sufijo
    /// distinto de "0000" (confirmado por Eduardo 16-sep-2026).
    /// Verificado a fondo contra 90 días reales (17-sep-2026): 162/162 tienen
    /// entrada padre, y el 100% de esos padres ya está en un estatus
    /// "resuelto" — 107 REVISADA, 16 NO REQUIERE REVISION FISICA y 39 SALIDA
    /// ESPECIAL. Nunca un padre PENDIENTE. Los 16/39 no contradicen la regla:
    /// una subdivisión también puede pedirse sobre mercancía que nunca
    /// necesitó revisión física, y SALIDA ESPECIAL es simplemente el estatus
    /// al que pasa el padre una vez que ya se dispuso (se sobreescribe sobre
    /// el mismo renglón, no es un historial). Regla cerrada, sin casos sueltos.
    /// </summary>
    private static bool CalcularEsSubdivision(string creferencia)
        => creferencia.Length >= 4 && creferencia[^4..] != "0000";

    private static FilaDerivada Derivar(MetricaRevisionRow row)
    {
        bool tieneFactura = row.FechaFactura.HasValue;
        bool fueraVentana = tieneFactura && CalcularFueraVentana(row.Arribo, row.FechaFactura);
        bool esSubdivision = CalcularEsSubdivision(row.Creferencia);
        // Confirmado por Eduardo (17-sep-2026) al ver datos reales: una factura
        // tardía por sí sola NO hace extraordinaria una revisión — eso es el
        // flujo normal esperando la factura. Solo es EXTRAORDINARIA si, a pesar
        // de estar fuera de ventana, alguien terminó la revisión física ESE
        // MISMO DÍA de arribo (evidencia real de que se apuraron por una
        // urgencia). Antes de este ajuste, en datos de julio 2026 salía el
        // 100% de las revisadas como "extraordinaria" — la regla anterior
        // confundía atraso normal de facturación con urgencia real.
        bool extraordinaria = row.Status == EstatusRevisionCodigo.Revisada && fueraVentana && !esSubdivision
            && row.FFinRevision.HasValue && row.FFinRevision.Value.Date == row.Arribo.Date;
        // ORIGEN ya no se excluye en bloque para toda subdivision: si la
        // subdivision tiene su propia finicia_revision/ffin_revision en traf3
        // (se re-revisó esa referencia hija en concreto, no solo heredó la
        // disposición del padre), sí se le atribuye APP/MANUAL propio
        // (Eduardo, 17-sep-2026, caso real 01C263591200100).
        bool? origenApp = (row.Status == EstatusRevisionCodigo.Revisada && row.TieneTiemposPropios) ? row.TieneSesionApp : (bool?)null;
        // Mismo acotamiento que ya usa MonitorController.GetVelocidadRevisores:
        // el histórico trae sesiones que alguien dejó abiertas (se han visto
        // duraciones de miles de minutos) y duraciones negativas/cero. Fuera de
        // [1,240] min se descarta la DURACIÓN (no la revisión en sí, que sigue
        // contando en Kpis) para no reventar los promedios.
        int? tiempoMinCrudo = (row.FInicioRevision.HasValue && row.FFinRevision.HasValue)
            ? (int)Math.Round((row.FFinRevision.Value - row.FInicioRevision.Value).TotalMinutes)
            : null;
        int? tiempoMin = (tiempoMinCrudo.HasValue && tiempoMinCrudo.Value >= 1 && tiempoMinCrudo.Value <= 240)
            ? tiempoMinCrudo
            : null;

        return new FilaDerivada
        {
            Row = row,
            TieneFactura = tieneFactura,
            FueraVentana = fueraVentana,
            EsSubdivision = esSubdivision,
            Extraordinaria = extraordinaria,
            OrigenApp = origenApp,
            TiempoMin = tiempoMin
        };
    }

    /// <summary>
    /// Mismas llaves de criterio que usa el mockup HTML y que usará Angular al
    /// hacer clic en una tarjeta/segmento — un solo lugar define qué significa
    /// cada una, para que el cross-filter nunca se desincronice entre vistas.
    /// </summary>
    private static bool CumpleCriterio(FilaDerivada f, string? criterio)
    {
        if (string.IsNullOrEmpty(criterio)) return true;
        int status = f.Row.Status;
        return criterio switch
        {
            "total" => true,
            "noRequiere" => status == EstatusRevisionCodigo.NoRequiere,
            "requiere" => status != EstatusRevisionCodigo.NoRequiere
                          && status != EstatusRevisionCodigo.SalidaExportacion
                          && status != EstatusRevisionCodigo.Destruccion
                          && status != EstatusRevisionCodigo.SalidaEspecial,
            "conFactura" => f.TieneFactura,
            "fueraVentana" => f.TieneFactura && f.FueraVentana,
            "comentarios" => !string.IsNullOrWhiteSpace(f.Row.Observaciones),
            "revisadas" => status == EstatusRevisionCodigo.Revisada,
            "pendientes" => status == EstatusRevisionCodigo.Pendiente,
            "extraordinarias" => f.Extraordinaria,
            "viaApp" => f.OrigenApp == true,
            "viaManual" => f.OrigenApp == false,
            "subdivisiones" => f.EsSubdivision,
            _ => true
        };
    }

    private static IEnumerable<FilaDerivada> AplicarOrigenYCrossFilter(
        IEnumerable<FilaDerivada> filas, string? origen, string? crossFilter)
    {
        IEnumerable<FilaDerivada> q = filas;
        if (origen == "app") q = q.Where(f => !f.EsSubdivision && f.OrigenApp == true);
        else if (origen == "manual") q = q.Where(f => !f.EsSubdivision && f.OrigenApp == false);
        if (!string.IsNullOrEmpty(crossFilter)) q = q.Where(f => CumpleCriterio(f, crossFilter));
        return q;
    }

    private static KpisDto CalcularKpis(List<FilaDerivada> filas)
    {
        int total = filas.Count;
        int noRequiere = filas.Count(f => f.Row.Status == EstatusRevisionCodigo.NoRequiere);
        int expo = filas.Count(f => f.Row.Status == EstatusRevisionCodigo.SalidaExportacion);
        int destruccion = filas.Count(f => f.Row.Status == EstatusRevisionCodigo.Destruccion);
        int salidaEspecial = filas.Count(f => f.Row.Status == EstatusRevisionCodigo.SalidaEspecial);
        int requiere = total - noRequiere - expo - destruccion - salidaEspecial;
        int conFactura = filas.Count(f => f.TieneFactura);
        int fueraVentana = filas.Count(f => f.TieneFactura && f.FueraVentana);
        int conComentarios = filas.Count(f => !string.IsNullOrWhiteSpace(f.Row.Observaciones));
        int revisadas = filas.Count(f => f.Row.Status == EstatusRevisionCodigo.Revisada);
        int pendientes = filas.Count(f => f.Row.Status == EstatusRevisionCodigo.Pendiente);
        int extraordinarias = filas.Count(f => f.Extraordinaria);
        int subdivisiones = filas.Count(f => f.EsSubdivision);
        int viaApp = filas.Count(f => f.OrigenApp == true);
        int viaManual = filas.Count(f => f.OrigenApp == false);
        int baseApp = viaApp + viaManual;

        int facturaYRevisada = filas.Count(f => f.TieneFactura && f.Row.Status == EstatusRevisionCodigo.Revisada);
        int dentroVentana = conFactura - fueraVentana;

        return new KpisDto
        {
            Total = total,
            NoRequiere = noRequiere,
            Requiere = requiere,
            ConFactura = conFactura,
            FueraVentana = fueraVentana,
            ConComentarios = conComentarios,
            Revisadas = revisadas,
            Pendientes = pendientes,
            Extraordinarias = extraordinarias,
            Subdivisiones = subdivisiones,
            ViaApp = viaApp,
            ViaManual = viaManual,
            PctFacturaRevisada = conFactura > 0 ? Math.Round(100.0 * facturaYRevisada / conFactura, 1) : 0,
            PctVentana = conFactura > 0 ? Math.Round(100.0 * dentroVentana / conFactura, 1) : 0,
            PctApp = baseApp > 0 ? Math.Round(100.0 * viaApp / baseApp, 1) : 0
        };
    }

    private static AntiguedadDto CalcularAntiguedad(List<FilaDerivada> filas, DateTime ahora)
    {
        var pendientes = filas.Where(f => f.Row.Status == EstatusRevisionCodigo.Pendiente);
        var dto = new AntiguedadDto();
        foreach (var f in pendientes)
        {
            double horas = (ahora - f.Row.Arribo).TotalHours;
            if (horas < 4) dto.Menos4h++;
            else if (horas < 24) dto.Entre4y24h++;
            else dto.Mas24h++;
        }
        return dto;
    }

    private static List<RevisorStatDto> CalcularRevisores(List<FilaDerivada> filas)
    {
        return filas
            .Where(f => f.Row.Status == EstatusRevisionCodigo.Revisada && !string.IsNullOrWhiteSpace(f.Row.Revisor))
            .GroupBy(f => f.Row.Revisor!)
            .Select(g =>
            {
                var conTiempo = g.Where(f => f.TiempoMin.HasValue).ToList();
                var baseApp = g.Where(f => !f.EsSubdivision).ToList();
                double? pctApp = baseApp.Count > 0 ? Math.Round(100.0 * baseApp.Count(f => f.OrigenApp == true) / baseApp.Count, 1) : null;
                return new RevisorStatDto
                {
                    Usuario = g.Key,
                    Cantidad = g.Count(),
                    TiempoPromedioMin = conTiempo.Count > 0 ? Math.Round(conTiempo.Average(f => f.TiempoMin!.Value), 1) : 0,
                    PctApp = pctApp
                };
            })
            .OrderByDescending(r => r.Cantidad)
            .ToList();
    }

    public async Task<MetricasResumenResponse> ObtenerResumenAsync(
        DateTime fechaInicio, DateTime fechaFin, string? planta, string? cliente,
        string? revisor, string? origen, string? crossFilter)
    {
        using IDbConnection conn = _db.CreateConnection();

        // Rango principal (el que el usuario eligió en el filtro de fecha).
        List<MetricaRevisionRow> filasRango = await ObtenerFilasAsync(conn, fechaInicio, fechaFin, planta, cliente, revisor);
        List<FilaDerivada> derivadasRango = AplicarOrigenYCrossFilter(filasRango.Select(Derivar), origen, crossFilter).ToList();

        // Ventana rodante de 14 días para tendencia/adopción — independiente
        // del rango elegido, pero con los MISMOS filtros de planta/cliente/
        // revisor/origen/cross-filter, para que "seleccionar algo en un
        // gráfico filtre a los demás" (pedido explícito de Eduardo, estilo
        // Power BI) también alcance a estas dos gráficas.
        DateTime hoy = DateTime.Today;
        DateTime desde14 = hoy.AddDays(-13);
        List<MetricaRevisionRow> filas14 = await ObtenerFilasAsync(conn, desde14, hoy, planta, cliente, revisor);
        List<FilaDerivada> derivadas14 = AplicarOrigenYCrossFilter(filas14.Select(Derivar), origen, crossFilter).ToList();

        var tendencia = new List<TrendPointDto>();
        var adopcion = new List<AdopcionPointDto>();
        for (int i = 13; i >= 0; i--)
        {
            DateTime dia = hoy.AddDays(-i);
            var filasDia = derivadas14.Where(f => f.Row.Arribo.Date == dia.Date).ToList();
            if (i <= 6)
            {
                tendencia.Add(new TrendPointDto
                {
                    Fecha = dia.ToString("yyyy-MM-dd"),
                    Recibidas = filasDia.Count,
                    Revisadas = filasDia.Count(f => f.Row.Status == EstatusRevisionCodigo.Revisada)
                });
            }
            adopcion.Add(new AdopcionPointDto
            {
                Fecha = dia.ToString("yyyy-MM-dd"),
                ViaApp = filasDia.Count(f => f.OrigenApp == true),
                ViaManual = filasDia.Count(f => f.OrigenApp == false)
            });
        }

        return new MetricasResumenResponse
        {
            FilasConsideradas = derivadasRango.Count,
            Kpis = CalcularKpis(derivadasRango),
            Tendencia = tendencia,
            Adopcion = adopcion,
            Antiguedad = CalcularAntiguedad(derivadasRango, DateTime.Now),
            Revisores = CalcularRevisores(derivadasRango)
        };
    }

    public async Task<EvidenciaResponse> ObtenerEvidenciaAsync(
        DateTime fechaInicio, DateTime fechaFin, string? planta, string? cliente,
        string? revisor, string? origen, string? crossFilter, int limite = 1000)
    {
        using IDbConnection conn = _db.CreateConnection();
        List<MetricaRevisionRow> filas = await ObtenerFilasAsync(conn, fechaInicio, fechaFin, planta, cliente, revisor);
        List<FilaDerivada> derivadas = AplicarOrigenYCrossFilter(filas.Select(Derivar), origen, crossFilter)
            .OrderByDescending(f => f.Row.Arribo)
            .ToList();

        bool truncado = derivadas.Count > limite;
        var items = derivadas.Take(limite).Select(f => new EvidenciaRowDto
        {
            Creferencia = f.Row.Creferencia,
            Cliente = f.Row.Cliente,
            Arribo = f.Row.Arribo,
            FechaFactura = f.Row.FechaFactura,
            TieneFactura = f.TieneFactura,
            FueraVentana = f.FueraVentana,
            Manejo = f.Row.Manejo,
            Estatus = f.Row.Status switch
            {
                EstatusRevisionCodigo.NoRequiere => "NO REQUIERE REVISION FISICA",
                EstatusRevisionCodigo.SalidaExportacion => "SALIDA EXPORTACION",
                EstatusRevisionCodigo.Revisada => "REVISADA FISICAMENTE",
                EstatusRevisionCodigo.Destruccion => "DESTRUCCION",
                EstatusRevisionCodigo.SalidaEspecial => "SALIDA ESPECIAL",
                _ => "PENDIENTE REVISION FISICA"
            },
            Origen = f.EsSubdivision ? "SUBDIVISION" : f.OrigenApp == true ? "APP" : f.OrigenApp == false ? "MANUAL" : null,
            Revisor = f.Row.Revisor,
            FechaInicioRevision = f.Row.FInicioRevision,
            FechaFinRevision = f.Row.FFinRevision,
            TiempoMin = f.TiempoMin,
            Extraordinaria = f.Extraordinaria,
            EsSubdivision = f.EsSubdivision
        }).ToList();

        return new EvidenciaResponse { Total = derivadas.Count, Truncado = truncado, Items = items };
    }

    /// <summary>
    /// Mismas 20 columnas, mismo orden, que TIEMPOS DE REVISION DE MERCANCIA
    /// (S-0025) — verificadas contra el query real de REP-S-0025 en la BD
    /// "reportes" (15-sep-2026). No se renombra ni se reordena nada de eso:
    /// Calidad ya conoce ese formato. Lo nuevo (ORIGEN, SUBDIVISION,
    /// EXTRAORDINARIA) se agrega solo al final, como columnas extra.
    /// Pedido explícito de Eduardo (16-sep-2026): "no hay que cambiarles nada
    /// porque se van a asustar".
    /// </summary>
    private async Task<List<FilaDerivada>> ObtenerFilasParaExportarAsync(
        DateTime fechaInicio, DateTime fechaFin, string? planta, string? cliente,
        string? revisor, string? origen, string? crossFilter)
    {
        using IDbConnection conn = _db.CreateConnection();
        List<MetricaRevisionRow> filas = await ObtenerFilasAsync(conn, fechaInicio, fechaFin, planta, cliente, revisor);
        return AplicarOrigenYCrossFilter(filas.Select(Derivar), origen, crossFilter)
            .OrderByDescending(f => f.Row.Arribo)
            .ToList();
    }

    private static string EstatusTextoExport(FilaDerivada f) => f.Row.Status switch
    {
        EstatusRevisionCodigo.NoRequiere => "NO REQUIERE REVISION FISICA",
        EstatusRevisionCodigo.SalidaExportacion => "SALIDA EXPORTACION",
        EstatusRevisionCodigo.Revisada => "REVISADA FISICAMENTE",
        EstatusRevisionCodigo.Destruccion => "DESTRUCCION",
        EstatusRevisionCodigo.SalidaEspecial => "SALIDA ESPECIAL",
        _ => "PENDIENTE REVISION FISICA"
    };

    public async Task<string> ExportarCsvAsync(
        DateTime fechaInicio, DateTime fechaFin, string? planta, string? cliente,
        string? revisor, string? origen, string? crossFilter)
    {
        List<FilaDerivada> derivadas = await ObtenerFilasParaExportarAsync(fechaInicio, fechaFin, planta, cliente, revisor, origen, crossFilter);

        static string Csv(string? v) => v is null ? "" : "\"" + v.Replace("\"", "\"\"") + "\"";
        static string Fdt(DateTime? d) => d.HasValue ? d.Value.ToString("dd/MM/yy HH:mm:ss") : "-/-/-";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", new[]
        {
            "ARRIBO","ENTRADA","TRAFICO","FACTURA","FECHA FACTURA","TOTAL","FECHA AGRUPADO","CLAVE","NOMBRE",
            "MANEJO","PESO","CANTIDAD","PLANTA","FINICIA_REVISION","FFIN_REVISION","TIEMPO","TIEMPO TRANSCURRIDO","REVISO",
            "ESTATUS REVISION","OBSERVACIONES DE REVISION",
            "ORIGEN","SUBDIVISION","EXTRAORDINARIA"
        }));

        foreach (FilaDerivada f in derivadas)
        {
            var r = f.Row;
            string estatus = r.Status switch
            {
                EstatusRevisionCodigo.NoRequiere => "NO REQUIERE REVISION FISICA",
                EstatusRevisionCodigo.SalidaExportacion => "SALIDA EXPORTACION",
                EstatusRevisionCodigo.Revisada => "REVISADA FISICAMENTE",
                EstatusRevisionCodigo.Destruccion => "DESTRUCCION",
                EstatusRevisionCodigo.SalidaEspecial => "SALIDA ESPECIAL",
                _ => "PENDIENTE REVISION FISICA"
            };
            string origenCol = f.EsSubdivision ? "" : (f.OrigenApp == true ? "APP" : f.OrigenApp == false ? "MANUAL" : "");
            int diasTranscurridos = Math.Max(0, (int)Math.Round(((r.FFinRevision ?? DateTime.Now) - r.Arribo).TotalDays));
            string tiempo = f.TiempoMin.HasValue ? $"{f.TiempoMin.Value / 60:00}:{f.TiempoMin.Value % 60:00}" : "00:00";

            sb.AppendLine(string.Join(",", new[]
            {
                Fdt(r.Arribo), Csv(r.Creferencia), "", Csv(r.TotalFacturas > 0 ? r.Creferencia : ""),
                r.FechaFactura.HasValue ? Fdt(r.FechaFactura) : "-/-/-",
                r.TotalFacturas.ToString(), "", Csv(r.ClienteId), Csv(r.Cliente),
                Csv(r.Manejo), r.Peso?.ToString() ?? "", r.Cantidad?.ToString() ?? "", Csv(r.Planta),
                Fdt(r.FInicioRevision), Fdt(r.FFinRevision), tiempo, diasTranscurridos.ToString(),
                Csv(r.Revisor), Csv(estatus), Csv(r.Observaciones),
                origenCol, f.EsSubdivision ? "SI" : "NO", f.Extraordinaria ? "SI" : "NO"
            }));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Mismo contenido y mismas 20+3 columnas que <see cref="ExportarCsvAsync"/>,
    /// pero como .xlsx real (ClosedXML, ya usado en <c>ReportService.cs</c>) en
    /// vez de texto separado por comas. Pedido de Eduardo (17-sep-2026): el
    /// CSV se presta a que Excel en configuración regional en español lo abra
    /// mal (usa punto y coma como separador de listas, no coma) y a que las
    /// fechas/números lleguen como texto plano en vez de celdas reales.
    /// </summary>
    public async Task<byte[]> ExportarXlsxAsync(
        DateTime fechaInicio, DateTime fechaFin, string? planta, string? cliente,
        string? revisor, string? origen, string? crossFilter)
    {
        List<FilaDerivada> derivadas = await ObtenerFilasParaExportarAsync(fechaInicio, fechaFin, planta, cliente, revisor, origen, crossFilter);

        using var wb = new XLWorkbook();
        wb.Properties.Author = "PMMovil";
        var ws = wb.Worksheets.Add("Metricas Revision");

        string[] columnas =
        {
            "ARRIBO","ENTRADA","TRAFICO","FACTURA","FECHA FACTURA","TOTAL","FECHA AGRUPADO","CLAVE","NOMBRE",
            "MANEJO","PESO","CANTIDAD","PLANTA","FINICIA_REVISION","FFIN_REVISION","TIEMPO","TIEMPO TRANSCURRIDO","REVISO",
            "ESTATUS REVISION","OBSERVACIONES DE REVISION","ORIGEN","SUBDIVISION","EXTRAORDINARIA"
        };
        for (int c = 0; c < columnas.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = columnas[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0x00, 0x33, 0x8D);
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        const string fmtFecha = "dd/mm/yy hh:mm";
        int fila = 2;
        foreach (FilaDerivada f in derivadas)
        {
            var r = f.Row;
            int diasTranscurridos = Math.Max(0, (int)Math.Round(((r.FFinRevision ?? DateTime.Now) - r.Arribo).TotalDays));
            string tiempo = f.TiempoMin.HasValue ? $"{f.TiempoMin.Value / 60:00}:{f.TiempoMin.Value % 60:00}" : "00:00";
            string origenCol = f.EsSubdivision ? "" : (f.OrigenApp == true ? "APP" : f.OrigenApp == false ? "MANUAL" : "");

            int col = 1;
            ws.Cell(fila, col++).Value = r.Arribo; ws.Cell(fila, col - 1).Style.DateFormat.Format = fmtFecha;
            ws.Cell(fila, col++).Value = r.Creferencia;
            ws.Cell(fila, col++).Value = "";
            ws.Cell(fila, col++).Value = r.TotalFacturas > 0 ? r.Creferencia : "";
            if (r.FechaFactura.HasValue) { ws.Cell(fila, col).Value = r.FechaFactura.Value; ws.Cell(fila, col).Style.DateFormat.Format = fmtFecha; }
            else ws.Cell(fila, col).Value = "-/-/-";
            col++;
            ws.Cell(fila, col++).Value = r.TotalFacturas;
            ws.Cell(fila, col++).Value = "";
            ws.Cell(fila, col++).Value = r.ClienteId ?? "";
            ws.Cell(fila, col++).Value = r.Cliente ?? "";
            ws.Cell(fila, col++).Value = r.Manejo ?? "";
            ws.Cell(fila, col++).Value = r.Peso.HasValue ? (double)r.Peso.Value : 0;
            ws.Cell(fila, col++).Value = r.Cantidad.HasValue ? (double)r.Cantidad.Value : 0;
            ws.Cell(fila, col++).Value = r.Planta;
            if (r.FInicioRevision.HasValue) { ws.Cell(fila, col).Value = r.FInicioRevision.Value; ws.Cell(fila, col).Style.DateFormat.Format = fmtFecha; }
            else ws.Cell(fila, col).Value = "-/-/-";
            col++;
            if (r.FFinRevision.HasValue) { ws.Cell(fila, col).Value = r.FFinRevision.Value; ws.Cell(fila, col).Style.DateFormat.Format = fmtFecha; }
            else ws.Cell(fila, col).Value = "-/-/-";
            col++;
            ws.Cell(fila, col++).Value = tiempo;
            ws.Cell(fila, col++).Value = diasTranscurridos;
            ws.Cell(fila, col++).Value = r.Revisor ?? "";
            ws.Cell(fila, col++).Value = EstatusTextoExport(f);
            ws.Cell(fila, col++).Value = r.Observaciones ?? "";
            ws.Cell(fila, col++).Value = origenCol;
            ws.Cell(fila, col++).Value = f.EsSubdivision ? "SI" : "NO";
            ws.Cell(fila, col++).Value = f.Extraordinaria ? "SI" : "NO";

            if (fila % 2 == 0) ws.Row(fila).Style.Fill.BackgroundColor = XLColor.FromArgb(0xF8, 0xFA, 0xFE);
            fila++;
        }

        ws.Columns().AdjustToContents(1, 60);
        ws.SheetView.FreezeRows(1);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
