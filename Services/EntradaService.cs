using System.Data;
using System.Text.RegularExpressions;

using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Data;
using PMMovil.Api.Helpers;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public class EntradaService
{
    // En Development, todos los avisos de este servicio (recepción en bodega,
    // reimpresión de etiquetas, etc.) se redirigen únicamente a esta casilla,
    // sin importar si el destinatario original venía escrito en código
    // (ej. CorreosReimpresion) o traído de base de datos (ej. tabla correoscl).
    private const string CorreoPruebasDesarrollo = "desarrollo3@grupopmm.com";
    private const string CorreoCalidad = "calidad@grupopmm.com";

    private readonly BaseRepository _db;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;
    private readonly ILogger<EntradaService> _logger;
    private readonly IFotoMejoraCola _mejora;

    public EntradaService(BaseRepository db, IConfiguration config, IHostEnvironment env, ILogger<EntradaService> logger, IFotoMejoraCola mejora)
    {
        _db = db;
        _config = config;
        _env = env;
        _logger = logger;
        _mejora = mejora;
    }

    public async Task<ConsecutivoResult> ObtenerConsecutivoAsync()
    {
        var result = new ConsecutivoResult { Success = false };

        using IDbConnection conn = _db.CreateConnection();
        conn.Open();

        using var tran = conn.BeginTransaction();
        try
        {
            var numeroObj = await conn.ExecuteScalarAsync(
                "SELECT numero FROM consecutivo WHERE formulario = '01C' FOR UPDATE",
                transaction: tran);

            if (numeroObj == null || numeroObj is DBNull)
            {
                result.Message = "No se encontró el consecutivo para formulario '01C'.";
                tran.Rollback();
                return result;
            }

            int numero = Convert.ToInt32(numeroObj);

            if (numero < 0)
            {
                result.Message = "El consecutivo tiene un valor inválido.";
                tran.Rollback();
                return result;
            }

            string year = DateTime.Now.Year.ToString()[^2..];
            int originalNumero = numero;
            int skipCount = 0;
            const int maxSkips = 1000;

            while (skipCount < maxSkips)
            {
                int current = originalNumero + skipCount;
                string referencia = $"01C{year}{current}0000";

                int existe = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM traf WHERE creferencia = @ref",
                    new { @ref = referencia },
                    transaction: tran);

                if (existe == 0)
                {
                    numero = current;
                    break;
                }

                skipCount++;
            }

            if (skipCount >= maxSkips)
            {
                result.Message = "No se pudo generar un consecutivo disponible después de 1000 intentos.";
                tran.Rollback();
                return result;
            }

            await conn.ExecuteAsync(
                "UPDATE consecutivo SET numero = numero + @inc WHERE formulario = '01C'",
                new { inc = 1 + skipCount },
                transaction: tran);

            DebugLog.Write("OBTENER_CONSECUTIVO", new { numero, skipCount });

            tran.Commit();

            result.Success = true;
            result.Numero = numero.ToString();
        }
        catch
        {
            tran.Rollback();
            throw;
        }

        return result;
    }

    public async Task<CatalogosEntradaResult> ObtenerCatalogosAsync()
    {
        var result = new CatalogosEntradaResult
        {
            Bodegas = new List<CatalogoEntradaItem>
   {
  new() { Value = "1", Text = "MAQUILA" },
  new() { Value = "2", Text = "ATLANTA" },
  new() { Value = "3", Text = "BODEGA 3" },
  new() { Value = "4", Text = "TRADE CTR" }
   }
        };

        using IDbConnection conn = _db.CreateConnection();

        using (var dr = await conn.QueryMultipleAsync(@"
   SELECT clase_hazmat AS Value, CONCAT(clase_hazmat, ' - ', cdescribe) AS Text FROM clase_hazmat WHERE inactivo = 0;
   SELECT cidmanejo AS Value, cdescmanejo AS Text FROM manejo WHERE inactivo = 0;
   SELECT nequipo AS Value, nequipo AS Text FROM caseta WHERE estatus = 'DISPONIBLE' AND tiporegistro = 'CAJA';
   SELECT claveusuario AS Value, CONCAT(TRIM(nombre),' ',TRIM(apaterno)) AS Text FROM usuarios WHERE recibe_ent = 1 AND inactivo = 0;
   SELECT cidubica AS Value, cdescribe AS Text FROM ubicas;
   SELECT cclaseid AS Value, cdescribe AS Text FROM clases WHERE inactivo = 0;"))
        {
            result.ClasesHazmat = (await dr.ReadAsync<CatalogoEntradaItem>()).ToList();
            result.Manejos = (await dr.ReadAsync<CatalogoEntradaItem>()).ToList();
            result.CajasArribo = (await dr.ReadAsync<CatalogoEntradaItem>()).ToList();
            result.Recibidores = (await dr.ReadAsync<CatalogoEntradaItem>()).ToList();
            result.Ubicaciones = (await dr.ReadAsync<CatalogoEntradaItem>()).ToList();
            result.Clases = (await dr.ReadAsync<CatalogoEntradaItem>()).ToList();
        }

        return result;
    }

    public async Task<object> BuscarClienteAsync(string codigo)
    {
        using IDbConnection conn = _db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync(
        "SELECT cidcte AS Codigo, cnombre AS Nombre FROM clientes WHERE cidcte = @cod AND inactivo = 0 LIMIT 1",
        new { cod = codigo });

        if (row == null)
            return new { success = false, codigo = "", nombre = "" };

        return new { success = true, codigo = (string)row.Codigo, nombre = (string)row.Nombre };
    }

    public async Task<object> BuscarTransporteAsync(string codigo)
    {
        using IDbConnection conn = _db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync(
        "SELECT cidtranspor AS Codigo, cnombre AS Nombre FROM transpor WHERE cidtranspor = @cod AND inactivo = 0 LIMIT 1",
        new { cod = codigo });

        if (row == null)
            return new { success = false, codigo = "", nombre = "" };

        return new { success = true, codigo = (string)row.Codigo, nombre = (string)row.Nombre };
    }

    public async Task<object> BuscarRefAsync(string cref)
    {
        using IDbConnection conn = _db.CreateConnection();

        var encabezado = await conn.QueryFirstOrDefaultAsync(@"
     SELECT t.creferencia, t.cclienteidtraf, c.cnombre AS nom_cliente,
      t.fcreatrafico, t.cidmanejo, t.cnocaja, t.cnoguia,
      t.cidtransportraf, tr.cnombre AS nom_transporte,
      t.thoraarribo, t.linbond, t.cinbondno,
      t.mobserv, t.lhazmat, t.bodega, t.planta,
      t.nbultosin, t.nsubpeso, t.ntotpeso, t.crecibio, t.cmarcas1, t.cmarcas2,
      t3.ccasetaid,
      t3.danada AS t3_danada,
      t3.danada_descripcion,
      t3.madera_c_sello, t3.madera_s_sello, t3.aserrin, t3.nosello
    FROM traf t
    LEFT JOIN traf3 t3 ON t3.fk_idtraf = t.idtraf
    LEFT JOIN clientes c ON t.cclienteidtraf = c.cidcte
    LEFT JOIN transpor tr ON t.cidtransportraf = tr.cidtranspor
    WHERE t.creferencia LIKE CONCAT('%', @cref, '%')
    ORDER BY t.fcreatrafico DESC LIMIT 1",
        new { cref });

        if (encabezado == null)
            return new { success = false, message = "Referencia no encontrada" };

        string referenciaCompleta = encabezado.creferencia;
        string idCliente = encabezado.cclienteidtraf;

        string obsOriginal = (string)encabezado.mobserv ?? "";
        string justObs = obsOriginal;
        int idxPipe = justObs.IndexOf(" | ");
        if (idxPipe >= 0)
            justObs = justObs[..idxPipe];

        bool tieneTraf3 = encabezado.t3_danada != null
        || encabezado.madera_c_sello != null;

        int maderaSelloCant = 0, maderaSinSelloCant = 0, aserrinCant = 0, noSelloCant = 0;
        bool danada, maderaSello, maderaSinSello, aserrin, noSello;

        if (tieneTraf3)
        {
            danada = Convert.ToInt32(encabezado.t3_danada ?? 0) == 1;
            maderaSello = Convert.ToInt32(encabezado.madera_c_sello ?? 0) > 0;
            maderaSelloCant = Convert.ToInt32(encabezado.madera_c_sello ?? 0);
            maderaSinSello = Convert.ToInt32(encabezado.madera_s_sello ?? 0) > 0;
            maderaSinSelloCant = Convert.ToInt32(encabezado.madera_s_sello ?? 0);
            aserrin = Convert.ToInt32(encabezado.aserrin ?? 0) > 0;
            aserrinCant = Convert.ToInt32(encabezado.aserrin ?? 0);
            noSello = Convert.ToInt32(encabezado.nosello ?? 0) > 0;
            noSelloCant = Convert.ToInt32(encabezado.nosello ?? 0);
        }
        else
        {
            danada = obsOriginal.Contains("| DAÑADA");
            maderaSello = obsOriginal.Contains("MaderaSello:");
            maderaSinSello = obsOriginal.Contains("MaderaSinSello:");
            aserrin = obsOriginal.Contains("Aserrin:");
            noSello = obsOriginal.Contains("NoSello:");

            if (maderaSello) int.TryParse(Regex.Match(obsOriginal, @"MaderaSello:(\d+)").Groups[1].Value, out maderaSelloCant);
            if (maderaSinSello) int.TryParse(Regex.Match(obsOriginal, @"MaderaSinSello:(\d+)").Groups[1].Value, out maderaSinSelloCant);
            if (aserrin) int.TryParse(Regex.Match(obsOriginal, @"Aserrin:(\d+)").Groups[1].Value, out aserrinCant);
            if (noSello) int.TryParse(Regex.Match(obsOriginal, @"NoSello:(\d+)").Groups[1].Value, out noSelloCant);
        }

        // danada_descripcion solo existe en traf3 (columna nueva) — no hay fallback legacy en mobserv.
        string danadaDescripcion = (string?)encabezado.danada_descripcion ?? "";

        var header = new
        {
            creferencia = referenciaCompleta,
            cclienteidtraf = idCliente,
            nom_cliente = (string)encabezado.nom_cliente ?? "",
            dfechaarribo = encabezado.fcreatrafico != null
       ? ((DateTime)encabezado.fcreatrafico).ToString("yyyy-MM-ddTHH:mm")
       : "",
            cidmanejo = (string)encabezado.cidmanejo ?? "",
            cnocaja = (string)encabezado.cnocaja ?? "",
            cnoguia = (string)encabezado.cnoguia ?? "",
            cidtransportraf = (string)encabezado.cidtransportraf ?? "",
            nom_transporte = (string)encabezado.nom_transporte ?? "",
            casetaId = (string)encabezado.ccasetaid ?? "",
            linbond = Convert.ToBoolean(encabezado.linbond),
            cinbondno = (string)encabezado.cinbondno ?? "",
            mobserv = justObs,
            lhazmat = Convert.ToBoolean(encabezado.lhazmat),
            bodega = (string)encabezado.bodega ?? "",
            planta = (string)encabezado.planta ?? "",
            nbultosin = Convert.ToDecimal(encabezado.nbultosin ?? 0),
            nsubpeso = Convert.ToDecimal(encabezado.nsubpeso ?? 0),
            ntotpeso = Convert.ToDecimal(encabezado.ntotpeso ?? 0),
            crecibio = (string)encabezado.crecibio ?? "",
            cmarcas1 = (string)encabezado.cmarcas1 ?? "",
            cmarcas2 = (string)encabezado.cmarcas2 ?? "",
            danada,
            danadaDescripcion,
            maderaSello,
            maderaSelloCant,
            maderaSinSello,
            maderaSinSelloCant,
            aserrin,
            aserrinCant,
            noSello,
            noSelloCant
        };

        var detalles = (await conn.QueryAsync(@"
   SELECT cclase AS Clase, ncantidad AS Cantidad, cdescripcion AS Descripcion,
     cobservaciones AS Atados, ubicacion AS Ubicacion
   FROM trafdet WHERE creferencia = @cref ORDER BY nlineno ASC",
        new { cref = referenciaCompleta })).ToList();

        string? clienteCarpeta = await conn.QueryFirstOrDefaultAsync<string>(
        "SELECT carpeta FROM clientes WHERE cidcte = @clienteId LIMIT 1",
        new { clienteId = idCliente });

        clienteCarpeta ??= "SIN_CARPETA";

        string basePath = _config["PhotoSettings:BasePath"] ?? @"F:\Trafico\fotos";
        string carpetaDoc = Path.Combine(basePath, clienteCarpeta, referenciaCompleta);

        var fotosMeta = (await conn.QueryAsync(
        "SELECT nlineno, cextension FROM fotos WHERE creferencia = @cref ORDER BY nlineno ASC",
        new { cref = referenciaCompleta })).ToList();

        var fotos = new List<string>();
        foreach (var f in fotosMeta)
        {
            string ext = ((string)f.cextension).TrimStart('.');
            string ref4 = ExtractRef4(referenciaCompleta);
            string nombreArchivo = $"foto_{ref4}_{f.nlineno}.{ext}";
            fotos.Add(Path.Combine(carpetaDoc, nombreArchivo));
        }

        return new { success = true, encabezado = header, detalles, fotos };
    }

    public async Task<object> BuscarCajasAsync()
    {
        using IDbConnection conn = _db.CreateConnection();
        var rows = await conn.QueryAsync(@"
   SELECT nequipo  AS Id,
     nequipo  AS Nombre,
     COALESCE(tipoop,   '') AS TipoOp,
     COALESCE(transpore,'') AS Transpore
   FROM caseta
   WHERE estatus = 'DISPONIBLE' AND tiporegistro = 'CAJA'
   ORDER BY nequipo");

        var items = rows.Select(r => new
        {
            Id = (string)r.Id,
            Nombre = (string)r.Nombre,
            Extra = string.Join(" · ", new[]
      {
  (string)r.TipoOp,
  (string)r.Transpore
 }.Where(s => !string.IsNullOrWhiteSpace(s)))
        }).ToList();

        return new { success = true, items };
    }

    public async Task<CasetaScanResult> BuscarPorCasetaAsync(string codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            return new CasetaScanResult { Success = false, Message = "Código inválido." };

        // Retrofit codifica el slash del CasetaId dentro del path como %2F.
        // Aceptamos tanto el valor ya decodificado como el que llegue crudo.
        string codigoNormalizado = Uri.UnescapeDataString(codigo).Trim();
        string casetaId = codigoNormalizado;
        string numCaja = "";

        // El CasetaId real puede contener guiones (por ejemplo: E-17914/26).
        // Primero se busca el código completo y, si viene un payload compuesto,
        // se usa el último guion como separador entre CasetaId y caja.
        int hyphenIdx = codigoNormalizado.LastIndexOf('-');
        if (hyphenIdx > 0 && codigoNormalizado.Length > hyphenIdx + 1)
        {
            casetaId = codigoNormalizado[..hyphenIdx];
            numCaja = codigoNormalizado[(hyphenIdx + 1)..];
        }

        using IDbConnection conn = _db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync(@"
   SELECT c.CasetaId, c.nequipo AS NumCaja,
     c.cclienteid AS ClienteId,
     COALESCE(cl.cnombre, '') AS NombreCliente,
     COALESCE(c.idtranspore, '') AS TransporteId,
     COALESCE(NULLIF(TRIM(c.transpore), ''), NULLIF(TRIM(tr.cnombre), ''), '') AS NombreTransporte,
     c.placas, c.operador, c.planta, c.sellos,
     DATE_FORMAT(c.farribo, '%d/%m/%Y %H:%i') AS FechaArribo
   FROM caseta c
   LEFT JOIN clientes cl ON cl.cidcte = c.cclienteid
   LEFT JOIN transpor tr ON tr.cidtranspor = c.idtranspore
   WHERE c.CasetaId = @codigoNormalizado
      OR c.nequipo = @codigoNormalizado
      OR (c.CasetaId = @casetaId AND c.nequipo = @numCaja)
    LIMIT 1", new { casetaId, numCaja, codigoNormalizado });

        if (row == null)
            return new CasetaScanResult { Success = false, Message = "No se encontró registro en caseta." };

        string existeRef = await conn.ExecuteScalarAsync<string>(@"
    SELECT t.creferencia
    FROM traf3 t3
    INNER JOIN traf t ON t.idtraf = t3.fk_idtraf
    WHERE t3.ccasetaid = @casetaId AND t3.ccasetaid <> ''
    LIMIT 1",
        new { casetaId = (string)row.CasetaId });

        return new CasetaScanResult
        {
            Success = true,
            CasetaId = (string)row.CasetaId,
            NumCaja = (string)row.NumCaja,
            ClienteId = (string)row.ClienteId,
            NombreCliente = (string)row.NombreCliente,
            TransporteId = (string)row.TransporteId,
            NombreTransporte = (string)row.NombreTransporte,
            Placas = (string)row.placas,
            Operador = (string)row.operador,
            Planta = (string)row.planta,
            Sellos = (string)row.sellos,
            FechaArribo = (string)row.FechaArribo,
            EntradaExistente = existeRef != null,
            ReferenciaExistente = existeRef
        };
    }

    public async Task<object> ListarClientesAsync(string? filtro)
    {
        using IDbConnection conn = _db.CreateConnection();
        var items = (await conn.QueryAsync(@"
   SELECT cidcte AS Id, cnombre AS Nombre
   FROM clientes
   WHERE (cidcte LIKE @f OR cnombre LIKE @f) AND inactivo = 0
   LIMIT 50",
        new { f = "%" + (filtro ?? "") + "%" })).ToList();

        return new { success = true, items };
    }

    public async Task<object> ListarTransportesAsync(string? filtro)
    {
        using IDbConnection conn = _db.CreateConnection();
        var items = (await conn.QueryAsync(@"
   SELECT cidtranspor AS Id, cnombre AS Nombre
   FROM transpor
   WHERE (cidtranspor LIKE @f OR cnombre LIKE @f) AND inactivo = 0
   LIMIT 50",
        new { f = "%" + (filtro ?? "") + "%" })).ToList();

        return new { success = true, items };
    }

    /// <summary>
    /// Cliente/transporte más usados recientemente en Entrada Bodega (traf), para la
    /// sección "Frecuentes" del buscador — misma idea que CasetaService.ObtenerFrecuentesEntradaAsync
    /// pero contra traf en vez de caseta.
    /// </summary>
    public async Task<List<EntradaFrequentItem>> ObtenerFrecuentesEntradaAsync(
        string tipo,
        int limite = 8,
        int dias = 90)
    {
        using IDbConnection conn = _db.CreateConnection();
        int take = Math.Clamp(limite, 1, 20);
        DateTime fromDate = DateTime.Now.AddDays(-Math.Clamp(dias, 1, 365));

        return tipo.Trim().ToLowerInvariant() switch
        {
            "cliente" => (await conn.QueryAsync<EntradaFrequentItem>(@"
SELECT
    t.cclienteidtraf AS Id,
    COALESCE(NULLIF(cl.cnombre, ''), t.cclienteidtraf) AS Nombre,
    COUNT(*) AS Uso,
    MAX(t.dfechaarribo) AS UltimoUso
FROM traf t
LEFT JOIN clientes cl ON cl.cidcte = t.cclienteidtraf
WHERE t.dfechaarribo >= @fromDate
  AND NULLIF(TRIM(t.cclienteidtraf), '') IS NOT NULL
GROUP BY t.cclienteidtraf, cl.cnombre
ORDER BY Uso DESC, UltimoUso DESC
LIMIT @take", new { fromDate, take })).ToList(),

            "transporte" => (await conn.QueryAsync<EntradaFrequentItem>(@"
SELECT
    t.cidtransportraf AS Id,
    COALESCE(NULLIF(tr.cnombre, ''), t.cidtransportraf) AS Nombre,
    COUNT(*) AS Uso,
    MAX(t.dfechaarribo) AS UltimoUso
FROM traf t
LEFT JOIN transpor tr ON tr.cidtranspor = t.cidtransportraf
WHERE t.dfechaarribo >= @fromDate
  AND NULLIF(TRIM(t.cidtransportraf), '') IS NOT NULL
GROUP BY t.cidtransportraf, tr.cnombre
ORDER BY Uso DESC, UltimoUso DESC
LIMIT @take", new { fromDate, take })).ToList(),

            _ => throw new ArgumentException("Tipo de frecuente no válido.")
        };
    }

    /// <summary><c>traf.crecibio</c> es varchar(30).</summary>
    public const int LongitudMaximaRecibio = 30;

    /// <summary>
    /// El texto que se guarda en <c>traf.crecibio</c>: el nombre completo en mayúsculas, como lo tienen las entradas del
    /// legacy («GLADYS RODRIGUEZ»), y nunca la clave del usuario si hay nombre. Orden de preferencia: el nombre de
    /// <c>usuarios</c> (nombre + apellido paterno), lo que mandó el cliente y, al final, la clave. Se recorta a la
    /// longitud de la columna: con el modo estricto de MySQL un nombre más largo haría fallar todo el guardado.
    /// </summary>
    public static string NombreRecibio(string? nombreDeUsuarios, string? recibioEnviado, string? claveUsuario)
    {
        string elegido = new[] { nombreDeUsuarios, recibioEnviado, claveUsuario }
            .Select(texto => Regex.Replace((texto ?? string.Empty).Trim(), @"\s+", " "))
            .FirstOrDefault(texto => texto.Length > 0) ?? string.Empty;

        elegido = elegido.ToUpperInvariant();
        return elegido.Length <= LongitudMaximaRecibio ? elegido : elegido[..LongitudMaximaRecibio].TrimEnd();
    }

    private static async Task<string> ResolverRecibioAsync(
        IDbConnection conn, IDbTransaction tran, string? usuarioActual, string? recibioEnviado)
    {
        string? nombre = null;
        if (!string.IsNullOrWhiteSpace(usuarioActual))
        {
            nombre = await conn.ExecuteScalarAsync<string?>(@"
   SELECT TRIM(CONCAT_WS(' ', NULLIF(TRIM(nombre), ''), NULLIF(TRIM(apaterno), '')))
   FROM usuarios
   WHERE TRIM(claveusuario) = @usuario
   LIMIT 1",
                new { usuario = usuarioActual.Trim() },
                transaction: tran);
        }

        return NombreRecibio(nombre, recibioEnviado, usuarioActual);
    }

    /// <summary>
    /// El número de caja que va en <c>traf.cnocaja</c>. Si la entrada está ligada a una caja de caseta
    /// (<c>traf3.ccasetaid</c>), manda el <c>nequipo</c> de esa caja; si no, o si la caja de caseta no trae número
    /// (un registro de tractocamión), se queda con lo que capturó la app.
    /// </summary>
    private static async Task<string> ResolverNumeroCajaAsync(
        IDbConnection conn, IDbTransaction tran, string? casetaId, string? cajaEnviada)
    {
        string enviada = (cajaEnviada ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(casetaId)) return enviada;

        string? nequipo = await conn.ExecuteScalarAsync<string?>(
            "SELECT NULLIF(TRIM(nequipo), '') FROM caseta WHERE casetaid = @casetaId LIMIT 1",
            new { casetaId = casetaId.Trim() },
            transaction: tran);

        return string.IsNullOrWhiteSpace(nequipo) ? enviada : nequipo.Trim();
    }

    public async Task<ResultadoOperacion> GuardarRecepcionAsync(GuardarRecepcionRequest datos, string usuarioActual)
    {
        var resultado = new ResultadoOperacion { Success = false };

        if (string.IsNullOrWhiteSpace(datos.Ref4))
        {
            resultado.Message = "La referencia (consecutivo) es obligatoria.";
            return resultado;
        }

        if (string.IsNullOrWhiteSpace(datos.Manejo))
        {
            resultado.Message = "El tipo de manejo es obligatorio.";
            return resultado;
        }

        // «Recibió» ya no se captura: es el usuario que guarda la entrada. Se resuelve más abajo contra `usuarios`
        // para guardar su nombre y no su clave.

        if (datos.Detalles == null || datos.Detalles.Count == 0)
        {
            resultado.Message = "Debes agregar al menos un artículo de mercancía.";
            return resultado;
        }

        if (datos.Hazmat && string.IsNullOrWhiteSpace(datos.Clase))
        {
            resultado.Message = "Si declaras HAZMAT, la clase es obligatoria.";
            return resultado;
        }

        if (datos.Danada && string.IsNullOrWhiteSpace(datos.DanadaDescripcion))
        {
            resultado.Message = "Si declaras mercancía dañada, describe el daño.";
            return resultado;
        }

        if (datos.Danada && (datos.FotosDanio == null || datos.FotosDanio.Count == 0))
        {
            resultado.Message = "Si declaras mercancía dañada, agrega al menos una foto del daño.";
            return resultado;
        }

        if (datos.PesoLbs < 0 || datos.PesoKgs < 0)
        {
            resultado.Message = "El peso no puede ser negativo.";
            return resultado;
        }

        string referencia = $"{datos.Ref1}{datos.Ref2}{datos.Ref3}{datos.Ref4}{datos.Ref5}{datos.Ref6}";
        DateTime fechaRecepcion = DateTime.TryParse(datos.Fecha, out var parsed) ? parsed : DateTime.Now;
        var advertencias = new List<string>();

        DebugLog.Write("GUARDAR_ENTRADA_INICIO", new
        {
            usuario = usuarioActual,
            ref1 = datos.Ref1,
            ref2 = datos.Ref2,
            ref3 = datos.Ref3,
            ref4 = datos.Ref4,
            ref5 = datos.Ref5,
            ref6 = datos.Ref6,
            referencia
        });

        using IDbConnection conn = _db.CreateConnection();
        conn.Open();
        using var tran = conn.BeginTransaction();

        try
        {
            int existeReferencia = await conn.ExecuteScalarAsync<int>(
           "SELECT COUNT(1) FROM traf WHERE creferencia = @referencia",
           new { referencia },
           transaction: tran);

            if (existeReferencia > 0)
            {
                DebugLog.Write("GUARDAR_ENTRADA_YA_EXISTE", new { referencia, existe = existeReferencia });
                tran.Rollback();
                resultado.Message = "La referencia ya existe. Busca la entrada para reimprimir o inicia una nueva.";
                return resultado;
            }

            if (!string.IsNullOrWhiteSpace(datos.CasetaId))
            {
                string? referenciaCaseta = await conn.ExecuteScalarAsync<string?>(@"
    SELECT t.creferencia
    FROM traf3 t3
    INNER JOIN traf t ON t.idtraf = t3.fk_idtraf
    WHERE t3.ccasetaid = @casetaId
      AND t3.ccasetaid <> ''
    LIMIT 1",
                    new { casetaId = datos.CasetaId.Trim() },
                    transaction: tran);

                if (!string.IsNullOrWhiteSpace(referenciaCaseta))
                {
                    DebugLog.Write("GUARDAR_ENTRADA_CASETA_YA_EXISTE", new
                    {
                        casetaId = datos.CasetaId,
                        referencia = referenciaCaseta
                    });
                    tran.Rollback();
                    resultado.Message = $"El CasetaId ya está ligado a la entrada {referenciaCaseta}. Busca esa entrada para continuar.";
                    return resultado;
                }
            }

            // Quién recibió: el nombre del usuario que guarda (nombre + apellido paterno, como lo guarda el legacy),
            // no su clave. Y el número de caja sale de la caja de caseta (`nequipo`) cuando la entrada está ligada a una.
            string recibioGuardar = await ResolverRecibioAsync(conn, tran, usuarioActual, datos.Recibio);
            string cajaGuardar = await ResolverNumeroCajaAsync(conn, tran, datos.CasetaId, datos.CajaArribo);

            string obs = datos.Observaciones ?? "";
            if (datos.Danada) obs += " | DAÑADA";
            if (datos.MaderaSello) obs += $" | MaderaSello:{datos.MaderaSelloCant}";
            if (datos.MaderaSinSello) obs += $" | MaderaSinSello:{datos.MaderaSinSelloCant}";
            if (datos.Aserrin) obs += $" | Aserrin:{datos.AserrinCant}";
            if (datos.NoSello) obs += $" | NoSello:{datos.NoSelloCant}";

            if (datos.PesoLbs > 44500)
            {
                obs += " | PESO_EXCEDIDO:MAYOR_A_44500_LBS";
                advertencias.Add("El peso supera 44,500 LBS.");
            }

            int idtraf = await conn.ExecuteScalarAsync<int>(@"
    INSERT INTO traf (
   creferencia, ref, cclienteidtraf, dfechaarribo, cidmanejo,
   cnocaja, cnoguia, cidtransportraf, thoraarribo,
   linbond, cinbondno, fcreatrafico, usucreatrafico,
   mobserv, lhazmat, bodega, planta, tipop, estatus,
   nbultosin, nsubpeso, ntotpeso, crecibio, cmarcas1, cmarcas2,
   cusertrafico, tdatetimetrafico
    ) VALUES (
   @referencia, @ref4, @codCliente, @fecha, @manejo,
   @cajaArribo, @guia, @codTransporte, @hora,
   @inBond, @inBondVal, NOW(), @usuario,
   @observaciones, @hazmat, '01', '1', 'C', 0,
   @totalRecibido, @pesoLbs, @pesoKgs, @recibio, @cmarcas1, @cmarcas2,
   @usuario, NOW()
    );
   SELECT LAST_INSERT_ID();", new
            {
                referencia,
                ref4 = (datos.Ref4 ?? "") + (datos.Ref5 ?? ""),
                codCliente = datos.CodCliente ?? "",
                fecha = fechaRecepcion,
                manejo = datos.Manejo ?? "",
                cajaArribo = cajaGuardar,
                guia = datos.Guia ?? "",
                codTransporte = datos.CodTransporte ?? "",
                hora = fechaRecepcion,
                inBond = datos.InBond ? 1 : 0,
                inBondVal = datos.InBondVal ?? "0",
                usuario = usuarioActual,
                observaciones = obs,
                hazmat = datos.Hazmat ? 1 : 0,
                totalRecibido = datos.TotalRecibido,
                pesoLbs = datos.PesoLbs,
                pesoKgs = datos.PesoKgs,
                recibio = recibioGuardar,
                cmarcas1 = datos.Hazmat ? (datos.UN ?? "") : "",
                cmarcas2 = datos.Hazmat ? (datos.Clase ?? "") : ""
            }, transaction: tran);

            await conn.ExecuteAsync(@"
    INSERT INTO traf3 (
   fk_idtraf, creferencia, ccasetaid,
   madera_c_sello, madera_s_sello, aserrin, nosello, danada, danada_descripcion,
   hazmat_un, hazmat_clase,
   detkg, detlbs
    ) VALUES (
   @idtraf, @referencia, @casetaId,
   @maderaCSello, @maderaSSello, @aserrin, @noSello, @danada, @danadaDescripcion,
   @hazmatUn, @hazmatClase,
   @detkg, @detlbs
    );", new
            {
                idtraf,
                referencia,
                casetaId = string.IsNullOrWhiteSpace(datos.CasetaId)
           ? (object)DBNull.Value
           : datos.CasetaId,
                maderaCSello = datos.MaderaSello ? datos.MaderaSelloCant : 0,
                maderaSSello = datos.MaderaSinSello ? datos.MaderaSinSelloCant : 0,
                aserrin = datos.Aserrin ? datos.AserrinCant : 0,
                noSello = datos.NoSello ? datos.NoSelloCant : 0,
                danada = datos.Danada ? 1 : 0,
                danadaDescripcion = datos.Danada ? (datos.DanadaDescripcion ?? "") : (object)DBNull.Value,
                hazmatUn = datos.Hazmat ? (datos.UN ?? "") : "",
                hazmatClase = datos.Hazmat ? (datos.Clase ?? "") : "",
                detkg = datos.PesoKgs,
                detlbs = datos.PesoLbs
            }, transaction: tran);

            if (datos.Hazmat && datos.Clase == "6.1")
            {
                await conn.ExecuteAsync(@"
 INSERT INTO bloqueo_procesos (referencia, motivo, usuario, fecha, proceso)
 VALUES (@referencia, 'HAZMAT 6.1', @usuario, NOW(), 'ENTRADA_BODEGA')",
               new { referencia, usuario = usuarioActual }, transaction: tran);

                advertencias.Add("Entrada bloqueada por HAZMAT clase 6.1. Requiere autorización.");
            }

            if (datos.Detalles.Count > 0)
            {
                int lineNo = 1;
                foreach (var det in datos.Detalles)
                {
                    await conn.ExecuteAsync(@"
 INSERT INTO trafdet (creferencia, nlineno, cclase, ncantidad, cdescripcion, cobservaciones, usucrea, fcrea, usumodi, fmodi, ubicacion)
 VALUES (@referencia, @lineno, @clase, @cant, @desc, @atados, @usr, NOW(), '', NULL, @ubic)",
                    new
                    {
                        referencia,
                        lineno = lineNo++,
                        clase = det.Clase ?? "",
                        cant = det.Cantidad,
                        desc = det.Descripcion ?? "",
                        atados = det.Atados ?? "",
                        usr = usuarioActual,
                        ubic = det.Ubicacion ?? ""
                    }, transaction: tran);
                }
            }

            if (datos.Fotos.Count > 0)
            {
                string? clienteCarpeta = await conn.QueryFirstOrDefaultAsync<string>(
               "SELECT COALESCE(NULLIF(carpeta,''), cidcte) FROM clientes WHERE cidcte = @clienteId LIMIT 1",
               new { clienteId = datos.CodCliente ?? "" },
               transaction: tran);

                clienteCarpeta = string.IsNullOrWhiteSpace(clienteCarpeta) ? "SIN_CARPETA" : clienteCarpeta.Trim();
                string basePath = _config["PhotoSettings:BasePath"] ?? @"F:\Trafico\fotos";
                string carpetaDoc = Path.Combine(basePath, clienteCarpeta);
                Directory.CreateDirectory(carpetaDoc);

                for (int i = 0; i < datos.Fotos.Count; i++)
                {
                    string? extension = await SaveEntradaPhotoAsync(datos.Fotos[i], carpetaDoc, referencia, i + 1, mejora: _mejora);
                    if (string.IsNullOrWhiteSpace(extension)) continue;

                    await conn.ExecuteAsync(@"
 INSERT INTO fotos (nlineno, creferencia, ntipodocto, cextension, usucrea, fcrea)
 VALUES (@nlineno, @referencia, 100, @ext, @usuario, NOW())",
                    new
                    {
                        nlineno = i + 1,
                        referencia,
                        ext = extension,
                        usuario = usuarioActual
                    }, transaction: tran);
                }
            }

            // Fotos exclusivas del daño (ntipodocto=103) — misma carpeta, numeracion propia.
            if (datos.FotosDanio.Count > 0)
            {
                string? clienteCarpetaDanio = await conn.QueryFirstOrDefaultAsync<string>(
               "SELECT COALESCE(NULLIF(carpeta,''), cidcte) FROM clientes WHERE cidcte = @clienteId LIMIT 1",
               new { clienteId = datos.CodCliente ?? "" },
               transaction: tran);

                clienteCarpetaDanio = string.IsNullOrWhiteSpace(clienteCarpetaDanio) ? "SIN_CARPETA" : clienteCarpetaDanio.Trim();
                string basePathDanio = _config["PhotoSettings:BasePath"] ?? @"F:\Trafico\fotos";
                string carpetaDocDanio = Path.Combine(basePathDanio, clienteCarpetaDanio);
                Directory.CreateDirectory(carpetaDocDanio);

                for (int i = 0; i < datos.FotosDanio.Count; i++)
                {
                    string? extension = await SaveEntradaPhotoAsync(datos.FotosDanio[i], carpetaDocDanio, referencia, i + 1, ntipodocto: 103, mejora: _mejora);
                    if (string.IsNullOrWhiteSpace(extension)) continue;

                    await conn.ExecuteAsync(@"
 INSERT INTO fotos (nlineno, creferencia, ntipodocto, cextension, usucrea, fcrea)
 VALUES (@nlineno, @referencia, 103, @ext, @usuario, NOW())",
                    new
                    {
                        nlineno = i + 1,
                        referencia,
                        ext = extension,
                        usuario = usuarioActual
                    }, transaction: tran);
                }
            }

            tran.Commit();

            DebugLog.Write("GUARDAR_ENTRADA_OK", new { idtraf, referencia });

            resultado.Success = true;
            resultado.RecepcionId = int.TryParse(datos.Ref4, out int parsedId) ? parsedId : 0;

            // Notificación por correo desactivada a pedido del usuario (2026-08-12):
            // al guardar, la app regresa directo al listado sin ese paso.
            // EnviarNotificacionRecibidoAsync(referencia, datos, usuarioActual) queda
            // disponible más abajo por si se quiere reactivar.

            string msg = $"Recepción guardada con éxito. Ref: {referencia}";
            if (advertencias.Count > 0)
                msg += " | " + string.Join(" ", advertencias);

            resultado.Message = msg;
        }
        catch (Exception ex)
        {
            DebugLog.Write("GUARDAR_ENTRADA_ERROR", new { referencia, msg = ex.Message });
            tran.Rollback();
            throw;
        }

        return resultado;
    }

    public async Task<ResultadoOperacion> ActualizarRecepcionAsync(string crefOriginal, GuardarRecepcionRequest datos, string usuarioActual)
    {
        var resultado = new ResultadoOperacion { Success = false };

        if (string.IsNullOrWhiteSpace(crefOriginal))
        {
            resultado.Message = "La referencia original es obligatoria.";
            return resultado;
        }

        DebugLog.Write("ACTUALIZAR_ENTRADA_INICIO", new
        {
            usuario = usuarioActual,
            crefOriginal,
            ref4 = datos.Ref4
        });

        using IDbConnection conn = _db.CreateConnection();
        conn.Open();
        using var tran = conn.BeginTransaction();

        try
        {
            int? idtrafNullable = await conn.ExecuteScalarAsync<int?>(
                "SELECT idtraf FROM traf WHERE creferencia = @cref",
                new { cref = crefOriginal },
                transaction: tran);

            if (idtrafNullable == null)
            {
                tran.Rollback();
                resultado.Message = $"La referencia {crefOriginal} no existe. No se puede actualizar.";
                return resultado;
            }

            if (datos.Danada && string.IsNullOrWhiteSpace(datos.DanadaDescripcion))
            {
                tran.Rollback();
                resultado.Message = "Si declaras mercancía dañada, describe el daño.";
                return resultado;
            }

            int idtraf = idtrafNullable.Value;
            DateTime fechaRecepcion = DateTime.TryParse(datos.Fecha, out var parsed) ? parsed : DateTime.Now;

            // Quien recibió es quien guardó la entrada: editarla después no se lo cambia a quien la edita. Solo si
            // quedó vacía se completa con el usuario actual. El número de caja sigue a la caja de caseta ligada.
            string? recibioPrevio = await conn.ExecuteScalarAsync<string?>(
                "SELECT crecibio FROM traf WHERE idtraf = @idtraf",
                new { idtraf },
                transaction: tran);
            string recibioGuardar = string.IsNullOrWhiteSpace(recibioPrevio)
                ? await ResolverRecibioAsync(conn, tran, usuarioActual, datos.Recibio)
                : recibioPrevio.Trim();
            string cajaGuardar = await ResolverNumeroCajaAsync(conn, tran, datos.CasetaId, datos.CajaArribo);

            string obs = datos.Observaciones ?? "";
            if (datos.Danada) obs += " | DAÑADA";
            if (datos.MaderaSello) obs += $" | MaderaSello:{datos.MaderaSelloCant}";
            if (datos.MaderaSinSello) obs += $" | MaderaSinSello:{datos.MaderaSinSelloCant}";
            if (datos.Aserrin) obs += $" | Aserrin:{datos.AserrinCant}";
            if (datos.NoSello) obs += $" | NoSello:{datos.NoSelloCant}";
            if (datos.PesoLbs > 44500) obs += " | PESO_EXCEDIDO:MAYOR_A_44500_LBS";

            await conn.ExecuteAsync(@"
                UPDATE traf SET
                    cclienteidtraf   = @codCliente,
                    dfechaarribo     = @fecha,
                    cidmanejo        = @manejo,
                    cnocaja          = @cajaArribo,
                    cnoguia          = @guia,
                    cidtransportraf  = @codTransporte,
                    thoraarribo      = @hora,
                    linbond          = @inBond,
                    cinbondno        = @inBondVal,
                    mobserv          = @observaciones,
                    lhazmat          = @hazmat,
                    nbultosin        = @totalRecibido,
                    nsubpeso         = @pesoLbs,
                    ntotpeso         = @pesoKgs,
                    crecibio         = @recibio,
                    cmarcas1         = @cmarcas1,
                    cmarcas2         = @cmarcas2,
                    cusertrafico     = @usuario,
                    tdatetimetrafico = NOW()
                WHERE idtraf = @idtraf",
                new
                {
                    idtraf,
                    codCliente = datos.CodCliente ?? "",
                    fecha = fechaRecepcion,
                    manejo = datos.Manejo ?? "",
                    cajaArribo = cajaGuardar,
                    guia = datos.Guia ?? "",
                    codTransporte = datos.CodTransporte ?? "",
                    hora = fechaRecepcion,
                    inBond = datos.InBond ? 1 : 0,
                    inBondVal = datos.InBondVal ?? "0",
                    observaciones = obs,
                    hazmat = datos.Hazmat ? 1 : 0,
                    totalRecibido = datos.TotalRecibido,
                    pesoLbs = datos.PesoLbs,
                    pesoKgs = datos.PesoKgs,
                    recibio = recibioGuardar,
                    cmarcas1 = datos.Hazmat ? (datos.UN ?? "") : "",
                    cmarcas2 = datos.Hazmat ? (datos.Clase ?? "") : "",
                    usuario = usuarioActual
                },
                transaction: tran);

            // traf3 no tiene UNIQUE por entrada (solo la clave primaria idtraf3), así que el antiguo «INSERT … ON DUPLICATE KEY
            // UPDATE» nunca encontraba el duplicado y agregaba una fila nueva en cada edición. Ahora se busca por referencia
            // (columna indexada, igual que LabelCaptureService y RevisionService): si ya hay filas se actualizan todas para no
            // dejar una con el dato y otra sin él, y solo si no hay ninguna se inserta. Se cuenta antes en lugar de mirar
            // las filas afectadas: con UseAffectedRows un UPDATE que no cambia nada devuelve 0 y se insertaría de más.
            var datosTraf3 = new
            {
                idtraf,
                cref = crefOriginal,
                casetaId = string.IsNullOrWhiteSpace(datos.CasetaId)
                    ? (object)DBNull.Value
                    : datos.CasetaId,
                maderaCSello = datos.MaderaSello ? datos.MaderaSelloCant : 0,
                maderaSSello = datos.MaderaSinSello ? datos.MaderaSinSelloCant : 0,
                aserrin = datos.Aserrin ? datos.AserrinCant : 0,
                noSello = datos.NoSello ? datos.NoSelloCant : 0,
                danada = datos.Danada ? 1 : 0,
                danadaDescripcion = datos.Danada ? (datos.DanadaDescripcion ?? "") : (object)DBNull.Value,
                hazmatUn = datos.Hazmat ? (datos.UN ?? "") : "",
                hazmatClase = datos.Hazmat ? (datos.Clase ?? "") : "",
                detkg = datos.PesoKgs,
                detlbs = datos.PesoLbs
            };

            int filasTraf3 = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM traf3 WHERE creferencia = @cref",
                new { cref = crefOriginal },
                transaction: tran);

            if (filasTraf3 > 0)
            {
                await conn.ExecuteAsync(@"
                    UPDATE traf3 SET
                        ccasetaid          = @casetaId,
                        madera_c_sello     = @maderaCSello,
                        madera_s_sello     = @maderaSSello,
                        aserrin            = @aserrin,
                        nosello            = @noSello,
                        danada             = @danada,
                        danada_descripcion = @danadaDescripcion,
                        hazmat_un          = @hazmatUn,
                        hazmat_clase       = @hazmatClase,
                        detkg              = @detkg,
                        detlbs             = @detlbs
                    WHERE creferencia = @cref",
                    datosTraf3,
                    transaction: tran);
            }
            else
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO traf3 (
                        fk_idtraf, creferencia, ccasetaid,
                        madera_c_sello, madera_s_sello, aserrin, nosello, danada, danada_descripcion,
                        hazmat_un, hazmat_clase,
                        detkg, detlbs
                    ) VALUES (
                        @idtraf, @cref, @casetaId,
                        @maderaCSello, @maderaSSello, @aserrin, @noSello, @danada, @danadaDescripcion,
                        @hazmatUn, @hazmatClase,
                        @detkg, @detlbs
                    )",
                    datosTraf3,
                    transaction: tran);
            }

            await conn.ExecuteAsync(
                "DELETE FROM trafdet WHERE creferencia = @cref",
                new { cref = crefOriginal },
                transaction: tran);

            if (datos.Detalles != null && datos.Detalles.Count > 0)
            {
                int lineNo = 1;
                foreach (var det in datos.Detalles)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO trafdet (creferencia, nlineno, cclase, ncantidad, cdescripcion, cobservaciones, usucrea, fcrea, usumodi, fmodi, ubicacion)
                        VALUES (@cref, @lineno, @clase, @cant, @desc, @atados, @usr, NOW(), '', NULL, @ubic)",
                        new
                        {
                            cref = crefOriginal,
                            lineno = lineNo++,
                            clase = det.Clase ?? "",
                            cant = det.Cantidad,
                            desc = det.Descripcion ?? "",
                            atados = det.Atados ?? "",
                            usr = usuarioActual,
                            ubic = det.Ubicacion ?? ""
                        },
                        transaction: tran);
                }
            }

            tran.Commit();

            DebugLog.Write("ACTUALIZAR_ENTRADA_OK", new { idtraf, cref = crefOriginal });

            resultado.Success = true;
            resultado.Message = $"Recepción actualizada correctamente. Ref: {crefOriginal}";
        }
        catch (Exception ex)
        {
            DebugLog.Write("ACTUALIZAR_ENTRADA_ERROR", new { cref = crefOriginal, msg = ex.Message });
            tran.Rollback();
            throw;
        }

        return resultado;
    }

    public async Task<object> GuardarFotosEntradaAsync(string referencia, List<string> fotos, string usuarioActual)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();

            string? clienteCarpeta = await conn.QueryFirstOrDefaultAsync<string>(@"
  SELECT COALESCE(NULLIF(cl.carpeta,''), cl.cidcte)
  FROM traf t
  JOIN clientes cl ON cl.cidcte = t.cclienteidtraf
  WHERE t.creferencia = @cref
  LIMIT 1", new { cref = referencia });

            clienteCarpeta = string.IsNullOrWhiteSpace(clienteCarpeta) ? "SIN_CARPETA" : clienteCarpeta.Trim();

            string basePath = _config["PhotoSettings:BasePath"] ?? @"F:\Trafico\fotos";
            string carpetaDoc = Path.Combine(basePath, clienteCarpeta);

            try
            {
                Directory.CreateDirectory(carpetaDoc);
            }
            catch (Exception exDir)
            {
                return new { success = false, message = $"No se pudo crear la carpeta: {carpetaDoc} — {exDir.Message}" };
            }

            int nextLine = await conn.ExecuteScalarAsync<int>(
        "SELECT COALESCE(MAX(nlineno), 0) + 1 FROM fotos WHERE creferencia = @cref AND ntipodocto = 100",
        new { cref = referencia });

            int guardadas = 0;
            var errores = new List<string>();

            for (int i = 0; i < fotos.Count; i++)
            {
                try
                {
                    string? ext = await SaveEntradaPhotoAsync(fotos[i], carpetaDoc, referencia, nextLine, mejora: _mejora);
                    if (string.IsNullOrWhiteSpace(ext))
                    {
                        errores.Add($"Foto {i + 1}: imagen vacía o formato inválido.");
                        continue;
                    }

                    await conn.ExecuteAsync(@"
 INSERT INTO fotos (nlineno, creferencia, ntipodocto, cextension, usucrea, fcrea)
 VALUES (@nlineno, @referencia, 100, @ext, @usuario, NOW())",
                    new { nlineno = nextLine, referencia, ext, usuario = usuarioActual });

                    nextLine++;
                    guardadas++;
                }
                catch (Exception exFoto)
                {
                    errores.Add($"Foto {i + 1}: {exFoto.Message}");
                }
            }

            if (guardadas == 0)
            {
                string detalle = errores.Count > 0
               ? string.Join(" | ", errores)
               : "Ninguna imagen se pudo procesar.";
                return new { success = false, message = $"Ruta destino: {carpetaDoc} — {detalle}" };
            }

            string aviso = errores.Count > 0 ? $" | Errores: {string.Join(", ", errores)}" : "";
            return new { success = true, message = $"{guardadas} foto(s) guardada(s) en {carpetaDoc}{aviso}" };
        }
        catch (Exception ex)
        {
            return new { success = false, message = $"Error inesperado al guardar fotos: {ex.Message}" };
        }
    }

    /// <summary>
    /// Fotos exclusivas del daño (ntipodocto=103) subidas por separado — mismo flujo que
    /// GuardarFotosEntradaAsync (ntipodocto=100), usado cuando se agregan fotos de daño
    /// después del guardado inicial (edición) o si la cola offline las reintenta.
    /// </summary>
    public async Task<object> GuardarFotosDanioEntradaAsync(string referencia, List<string> fotos, string usuarioActual)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();

            string? clienteCarpeta = await conn.QueryFirstOrDefaultAsync<string>(@"
  SELECT COALESCE(NULLIF(cl.carpeta,''), cl.cidcte)
  FROM traf t
  JOIN clientes cl ON cl.cidcte = t.cclienteidtraf
  WHERE t.creferencia = @cref
  LIMIT 1", new { cref = referencia });

            clienteCarpeta = string.IsNullOrWhiteSpace(clienteCarpeta) ? "SIN_CARPETA" : clienteCarpeta.Trim();

            string basePath = _config["PhotoSettings:BasePath"] ?? @"F:\Trafico\fotos";
            string carpetaDoc = Path.Combine(basePath, clienteCarpeta);

            try
            {
                Directory.CreateDirectory(carpetaDoc);
            }
            catch (Exception exDir)
            {
                return new { success = false, message = $"No se pudo crear la carpeta: {carpetaDoc} — {exDir.Message}" };
            }

            int nextLine = await conn.ExecuteScalarAsync<int>(
        "SELECT COALESCE(MAX(nlineno), 0) + 1 FROM fotos WHERE creferencia = @cref AND ntipodocto = 103",
        new { cref = referencia });

            int guardadas = 0;
            var errores = new List<string>();

            for (int i = 0; i < fotos.Count; i++)
            {
                try
                {
                    string? ext = await SaveEntradaPhotoAsync(fotos[i], carpetaDoc, referencia, nextLine, ntipodocto: 103, mejora: _mejora);
                    if (string.IsNullOrWhiteSpace(ext))
                    {
                        errores.Add($"Foto {i + 1}: imagen vacía o formato inválido.");
                        continue;
                    }

                    await conn.ExecuteAsync(@"
 INSERT INTO fotos (nlineno, creferencia, ntipodocto, cextension, usucrea, fcrea)
 VALUES (@nlineno, @referencia, 103, @ext, @usuario, NOW())",
                    new { nlineno = nextLine, referencia, ext, usuario = usuarioActual });

                    nextLine++;
                    guardadas++;
                }
                catch (Exception exFoto)
                {
                    errores.Add($"Foto {i + 1}: {exFoto.Message}");
                }
            }

            if (guardadas == 0)
            {
                string detalle = errores.Count > 0
               ? string.Join(" | ", errores)
               : "Ninguna imagen se pudo procesar.";
                return new { success = false, message = $"Ruta destino: {carpetaDoc} — {detalle}" };
            }

            string aviso = errores.Count > 0 ? $" | Errores: {string.Join(", ", errores)}" : "";
            return new { success = true, message = $"{guardadas} foto(s) de daño guardada(s) en {carpetaDoc}{aviso}" };
        }
        catch (Exception ex)
        {
            return new { success = false, message = $"Error inesperado al guardar fotos de daño: {ex.Message}" };
        }
    }

    public async Task<object> NotificarReimpresionAsync(string referencia, string motivo, string usuarioActual)
    {
        string? destinatarios = _config["CorreosReimpresion"] ?? _config.GetSection("AppSettings")["CorreosReimpresion"];

        if (string.IsNullOrEmpty(destinatarios))
        {
            return new { success = true, message = "Reimpresión autorizada." };
        }

        string fechaHora = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");

        string body = $@"
   <div style='font-family: Arial, sans-serif; color: #333; line-height: 1.6;'>
  <h2 style='color: #4A90E2;'>Notificación de Reimpresión de Etiquetas</h2>
  <p>Se ha solicitado la reimpresión de etiquetas para la siguiente entrada:</p>
  <table style='border-collapse: collapse; width: 100%; max-width: 600px;'>
 <tr><td style='padding: 8px; border: 1px solid #ddd; font-weight: bold; background-color: #f9f9f9; width: 30%;'>Entrada</td>
 <td style='padding: 8px; border: 1px solid #ddd;'>{referencia}</td></tr>
 <tr><td style='padding: 8px; border: 1px solid #ddd; font-weight: bold; background-color: #f9f9f9;'>Usuario</td>
 <td style='padding: 8px; border: 1px solid #ddd;'>{usuarioActual}</td></tr>
 <tr><td style='padding: 8px; border: 1px solid #ddd; font-weight: bold; background-color: #f9f9f9;'>Fecha y Hora</td>
 <td style='padding: 8px; border: 1px solid #ddd;'>{fechaHora}</td></tr>
 <tr><td style='padding: 8px; border: 1px solid #ddd; font-weight: bold; background-color: #f9f9f9;'>Motivo</td>
 <td style='padding: 8px; border: 1px solid #ddd; color: #d9534f; font-weight: bold;'>{motivo}</td></tr>
  </table>
   </div>";

        await SmtpDirectoAsync(
        destinatarios.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).ToList(),
        $"[{referencia}] - Reimpresión de Etiquetas",
        body);

        return new { success = true, message = "Notificación enviada" };
    }

    private static string ExtractRef4(string referencia)
    {
        if (string.IsNullOrWhiteSpace(referencia))
            return "";

        string[] parts = referencia.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4)
            return parts[3];

        string clean = referencia.Trim();
        if (clean.Length > 9 && clean.StartsWith("01C", StringComparison.OrdinalIgnoreCase))
            return clean[5..^4];

        string digits = Regex.Replace(clean, @"\D", "");
        return digits.Length > 8 ? digits[4..^4] : "";
    }

    private static async Task<string?> SaveEntradaPhotoAsync(string image, string targetDirectory, string creferencia, int lineNo, int ntipodocto = 100, IFotoMejoraCola? mejora = null)
    {
        if (string.IsNullOrWhiteSpace(image))
            return null;

        string payload = image.Trim();
        string extension = "jpg";

        if (payload.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            int extStart = "data:image/".Length;
            int extEnd = payload.IndexOf(';', extStart);
            if (extEnd > extStart)
                extension = NormalizeImageExtension(payload[extStart..extEnd]);
        }

        int commaIndex = payload.IndexOf(',');
        if (commaIndex >= 0)
            payload = payload[(commaIndex + 1)..];

        payload = payload.Trim().Replace(" ", "").Replace("\n", "").Replace("\r", "");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }

        string fileName = PhotoFileNameResolver.Build(
            creferencia ?? string.Empty,
            ntipodocto,
            lineNo,
            extension);
        string rutaFoto = Path.Combine(targetDirectory, fileName);
        await File.WriteAllBytesAsync(rutaFoto, bytes);

        // Se responde con la original y la mejora corre después en segundo plano (la cola ignora lo que no sea JPG).
        mejora?.Encolar(rutaFoto, ntipodocto == 103 ? "entrada-danio" : "entrada");

        return extension;
    }

    private static string NormalizeImageExtension(string raw)
    {
        string ext = raw.Trim().TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "jpeg" => "jpg",
            "jpg" => "jpg",
            "png" => "png",
            "webp" => "webp",
            _ => "jpg"
        };
    }

    private async Task EnviarNotificacionRecibidoAsync(string referencia, GuardarRecepcionRequest datos, string usuario)
    {
        string? destinatarios;

        using (IDbConnection conn = _db.CreateConnection())
        {
            destinatarios = await conn.QueryFirstOrDefaultAsync<string>(
           "SELECT cemail FROM correoscl WHERE cidcte = @cliente AND inactivo = 0 LIMIT 1",
           new { cliente = datos.CodCliente ?? "" });
        }

        if (string.IsNullOrWhiteSpace(destinatarios))
            return;

        string bodega = "MAQUILA";

        string detallesHtml = "";
        if (datos.Detalles != null)
        {
            foreach (var d in datos.Detalles)
            {
                detallesHtml += $@"
 <tr>
 <td style='padding:6px;border:1px solid #ddd;text-align:center'>{d.Cantidad}</td>
 <td style='padding:6px;border:1px solid #ddd'>{d.Descripcion}</td>
 <td style='padding:6px;border:1px solid #ddd;text-align:center'>{d.Ubicacion}</td>
 </tr>";
            }
        }

        string fecha = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
        string body = $@"
   <html><body style='font-family:Arial;color:#333;'>
   <h2 style='color:#003d8c;'>Recepción en Bodega</h2>
   <p>Se ha registrado la recepción de la siguiente mercancía:</p>
   <table style='border-collapse:collapse;width:100%;max-width:600px;'>
  <tr><td style='padding:6px;border:1px solid #ddd;font-weight:bold;background:#f5f5f5;width:30%'>Referencia</td>
 <td style='padding:6px;border:1px solid #ddd'>{referencia}</td></tr>
  <tr><td style='padding:6px;border:1px solid #ddd;font-weight:bold;background:#f5f5f5'>Cliente</td>
 <td style='padding:6px;border:1px solid #ddd'>{datos.NomCliente}</td></tr>
  <tr><td style='padding:6px;border:1px solid #ddd;font-weight:bold;background:#f5f5f5'>Bodega</td>
 <td style='padding:6px;border:1px solid #ddd'>{bodega}</td></tr>
  <tr><td style='padding:6px;border:1px solid #ddd;font-weight:bold;background:#f5f5f5'>Fecha</td>
 <td style='padding:6px;border:1px solid #ddd'>{fecha}</td></tr>
  <tr><td style='padding:6px;border:1px solid #ddd;font-weight:bold;background:#f5f5f5'>Total Piezas</td>
 <td style='padding:6px;border:1px solid #ddd'>{datos.TotalRecibido}</td></tr>
   </table>
   <h3 style='color:#003d8c;margin-top:20px;'>Detalle de Mercancía</h3>
   <table style='border-collapse:collapse;width:100%;max-width:600px;'>
  <tr style='background:#003d8c;color:#fff;'>
 <th style='padding:8px;border:1px solid #003d8c'>Cant</th>
 <th style='padding:8px;border:1px solid #003d8c'>Descripción</th>
 <th style='padding:8px;border:1px solid #003d8c'>Ubicación</th>
  </tr>
  {detallesHtml}
   </table>
   <p style='margin-top:20px;font-size:0.85em;color:#666;'>
  Este correo se genera automáticamente al registrar la recepción en bodega.
   </p>
   </body></html>";

        await SmtpDirectoAsync(
        destinatarios.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).ToList(),
        $"[{referencia}] Recepción en Bodega - {datos.NomCliente}",
        body,
        copias: [CorreoCalidad],
        forzarDestinatariosDePrueba: true);
    }

    private async Task SmtpDirectoAsync(
        List<string> to,
        string subject,
        string bodyHtml,
        IEnumerable<string>? copias = null,
        bool forzarDestinatariosDePrueba = false)
    {
        if (_env.IsDevelopment() || forzarDestinatariosDePrueba)
        {
            if (to != null && to.Count > 0)
            {
                _logger.LogInformation("EntradaService.SmtpDirectoAsync: entorno Development, se redirige el envío de '{Subject}' (destinatarios originales: {Original}) a {Destino}.", subject, string.Join(", ", to), CorreoPruebasDesarrollo);
            }
            to = new List<string> { CorreoPruebasDesarrollo };
        }

        string server = _config["Smtp:Host"] ?? _config["AppSettings:SmtpServer"] ?? "localhost";
        string portStr = _config["Smtp:Port"] ?? _config["AppSettings:SmtpPort"] ?? "25";
        string user = _config["Smtp:User"] ?? _config["AppSettings:SmtpUser"] ?? "";
        string pass = _config["Smtp:Password"] ?? _config["AppSettings:SmtpPass"] ?? "";
        string from = _config["Smtp:From"] ?? _config["AppSettings:SmtpFrom"] ?? "noreply@grupopmm.com";

        int.TryParse(portStr, out int port);

        using var msg = new System.Net.Mail.MailMessage();
        msg.From = new System.Net.Mail.MailAddress(from, "PMM Bodega");
        foreach (var email in to)
            msg.To.Add(email);
        foreach (var copia in copias ?? Enumerable.Empty<string>())
            msg.CC.Add(copia);

        msg.Subject = subject;
        msg.IsBodyHtml = true;
        msg.Body = bodyHtml;

        using var smtp = new System.Net.Mail.SmtpClient(server, port);
        smtp.Timeout = 15_000;
        if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
            smtp.Credentials = new System.Net.NetworkCredential(user, pass);

        await smtp.SendMailAsync(msg);
    }
}
