using System.Data;
using System.Text;

using Dapper;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public class PrevioService
{
    private readonly BaseRepository _db;
    private readonly ISmtpService _smtp;
    private readonly IConfiguration _config;
    private readonly ILogger<PrevioService> _logger;

    public PrevioService(BaseRepository db, ISmtpService smtp, IConfiguration config, ILogger<PrevioService> logger)
    {
        _db = db;
        _smtp = smtp;
        _config = config;
        _logger = logger;
    }


    public static string? ValidarReferencia(string texto)
    {
        if (texto.Length < 15 || texto.Length > 25)
            return "ALERTA, ESTO NO ES UN TRAFICO";

        char tipo = texto.Length > 2 ? texto[2] : ' ';
        if (tipo != 'I' && tipo != 'K' && tipo != 'E')
            return "ALERTA, ESTO NO ES UN TRAFICO";

        return null;
    }


    public async Task<BuscarTraficoResult> BuscarTraficoAsync(string texto)
    {
        var result = new BuscarTraficoResult();

        string? error = ValidarReferencia(texto);
        if (error != null)
        {
            result.Message = error;
            return result;
        }

        string cref = texto[..15];
        string cte = texto.Length >= 21 ? texto[15..21].Trim() : "";
        string tipo = texto.Length >= 24 ? texto[21..24].Trim() : "";

        using IDbConnection conn = _db.CreateConnection();

        var traf = await GetTraficoSimpleAsync(conn, cref, cte);
        if (traf == null || string.IsNullOrEmpty(traf.Creferencia))
        {
            result.Message = "ALERTA, TRAFICO NO EXISTE";
            return result;
        }

        result.Success = true;
        result.Trafico = traf;
        return result;
    }

    private async Task<PrevioTraficoInfo?> GetTraficoSimpleAsync(IDbConnection conn, string cref, string cte)
    {
        const string sql = @"
   SELECT
  traf.creferencia,
  traf.cclienteidtraf,
  traf.bodega,
  traf.tipop,
  traf.idtraf,
  GROUP_CONCAT(DISTINCT TRIM(furgon.carno)) AS cnocaja
   FROM traf
   LEFT JOIN furgon ON traf.creferencia = furgon.creferencia
   WHERE traf.creferencia = @cref AND traf.cclienteidtraf = @cte AND traf.estatus <> 9";

        var row = await conn.QueryFirstOrDefaultAsync<PrevioDbRow>(sql, new { cref, cte });
        if (row == null) return null;

        return new PrevioTraficoInfo
        {
            Creferencia = row.Creferencia ?? "",
            Cclienteidtraf = row.Cclienteidtraf ?? "",
            Bodega = row.Bodega ?? "",
            Tipop = row.Tipop ?? "",
            Cnocaja = row.Cnocaja ?? "",
            Idtraf = row.Idtraf
        };
    }


    public async Task<PrevioResult> EnviarNotificacionAsync(string referencia, string observaciones, string usuario)
    {
        var result = new PrevioResult();

        string? error = ValidarReferencia(referencia);
        if (error != null)
        {
            result.Message = error;
            return result;
        }

        string cref = referencia[..15];
        string cte = referencia.Length >= 21 ? referencia[15..21].Trim() : "";

        using IDbConnection conn = _db.CreateConnection();

        var datos = await GetDatosPrevioAsync(conn, cref);
        if (datos == null)
        {
            result.Message = "No se mando la Notificacion";
            return result;
        }

        datos.Observaciones = observaciones;
        datos.Cclienteidtraf = cte;

        await EnviarCorreoPrevioAsync(conn, datos, usuario);

        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        await conn.ExecuteAsync(
        "UPDATE traf_noti SET noti_previo = 1 WHERE creferencia = @cref",
        new { cref });

        await conn.ExecuteAsync(
        @"INSERT INTO logprevio(creferencia, usuario, fenvio, observaciones)
  VALUES(@cref, @usuario, @time, @obs)",
        new { cref, usuario, time = timeStr, obs = observaciones });

        result.Success = true;
        result.Message = "PROCESO FINALIZADO";
        return result;
    }


    private async Task<DatosPrevio?> GetDatosPrevioAsync(IDbConnection conn, string cref)
    {
        const string sql = @"
   SELECT
  traf.idtraf,
  traf.creferencia,
  traf.bodega,
  traf.tipop,
  traf.cclienteidtraf,
  clientes.hashtag,
  clientes.cidcte,
  clientes.cnombre,
  clientes.creferencia,
  GROUP_CONCAT(DISTINCT fact.cnofact SEPARATOR ', ') AS cnofact,
  '' AS proveedor,
  '' AS pedido,
  '' AS material
   FROM traf
   LEFT JOIN clientes ON clientes.cidcte = traf.cclienteidtraf
   LEFT JOIN fact ON fact.creferencia = traf.creferencia
   WHERE traf.creferencia = @cref
   GROUP BY traf.idtraf";

        var row = await conn.QueryFirstOrDefaultAsync<PrevioFullDbRow>(sql, new { cref });
        if (row == null) return null;

        return new DatosPrevio
        {
            Creferencia = row.Creferencia ?? "",
            Bodega = row.Bodega ?? "",
            Tipop = row.Tipop ?? "",
            Cclienteidtraf = row.Cclienteidtraf ?? "",
            Hash = row.Hashtag ?? "",
            IdCliente = row.Cidcte ?? "",
            Cliente = row.Cnombre ?? "",
            Factura = row.Cnofact ?? "",
            Proveedor = row.Proveedor ?? "",
            Pedido = row.Pedido ?? "",
            Material = row.Material ?? ""
        };
    }


    private async Task EnviarCorreoPrevioAsync(IDbConnection conn, DatosPrevio datos, string usuario)
    {
        string crefFormateado = FormatearReferencia(datos.Creferencia);

        string subject = $"Notificacion Previa - {crefFormateado}";
        string body = BuildBodyPrevio(datos, crefFormateado);

        var config = await GetCorreoConfigAsync("NOT-PREVIO");
        if (config != null)
        {
            if (!string.IsNullOrEmpty(config.Subject))
            {
                subject = config.Subject
               .Replace("[creferencia]", crefFormateado)
               .Replace("[pedido]", datos.Pedido);
            }
            if (!string.IsNullOrEmpty(config.Body))
            {
                body = config.Body
               .Replace("[bodega]", datos.Bodega)
               .Replace("[creferencia]", crefFormateado)
               .Replace("[link]", BuildFotoLink(datos.Creferencia))
               .Replace("[cliente]", datos.Cliente)
               .Replace("[cnofact]", datos.Factura)
               .Replace("[observaciones]", datos.Observaciones ?? "")
               .Replace("[material]", datos.Material)
               .Replace("[proveedor]", datos.Proveedor);
            }
        }

        var recipients = new List<string>();
        string bodegaLower = datos.Bodega?.ToLower() ?? "";

        if (bodegaLower.Contains("veracruz") || bodegaLower.Contains("coatzacoalcos") || bodegaLower.Contains("tuxpan"))
        {
            recipients.AddRange(new[]
            {
  "maribel@imexfwd.com", "mvlozano@imexfwd.com",
  "sherrera@imexfwd.com", "jadiaz@imexfwd.com",
  "desarrollo@imexfwd.com"
   });
        }
        else if (bodegaLower.Contains("laredo"))
        {
            if (datos.Tipop?.ToUpper() == "E" || datos.Tipop?.ToUpper() == "K")
            {
                recipients.AddRange(new[]
                {
 "silvia@imexfwd.com", "mariana.ortegon@imexfwd.com",
 "trafico.expo@imexfwd.com"
  });
            }

            var clientEmails = await GetCorreosClienteAsync(datos.Cclienteidtraf, datos.Tipop);
            recipients.AddRange(clientEmails);
        }
        else if (bodegaLower.Contains("manzanillo"))
        {
            recipients.AddRange(new[]
            {
  "oscar.galvan@imexfwd.com", "desarrollo2@imexfwd.com",
  "desarrollo@imexfwd.com", "auxiliar.mzo2@imexfwd.com"
   });
        }
        else
        {
            var clientEmails = await GetCorreosClienteAsync(datos.Cclienteidtraf, datos.Tipop);
            recipients.AddRange(clientEmails);
            recipients.Add("desarrollo@imexfwd.com");
        }

        await _smtp.EnviarAsync(recipients, subject, body);
    }


    public async Task<PrevioResult> EnviarAvisoAsync(NuevoAvisoRequest request, string usuario)
    {
        var result = new PrevioResult();

        DateTime fecha;
        if (!DateTime.TryParseExact(request.FechaArribo, "dd-MM-yyyy", null,
       System.Globalization.DateTimeStyles.None, out fecha) || fecha.Year <= 2000)
        {
            result.Message = "INFORMACION ERRONEA";
            return result;
        }

        if (!int.TryParse(request.Hora, out int hora)) hora = 12;
        if (!int.TryParse(request.Minuto, out int minuto)) minuto = 0;
        fecha = fecha.AddHours(hora).AddMinutes(minuto);

        using IDbConnection conn = _db.CreateConnection();

        string clienteNombre = "DESCONOCIDO";
        if (!string.IsNullOrEmpty(request.ClienteId) && request.ClienteId != "DESCONOCIDO")
        {
            var cli = await conn.QueryFirstOrDefaultAsync<string>(
           "SELECT cnombre FROM clientes WHERE cidcte = @id",
           new { id = request.ClienteId });
            if (cli != null) clienteNombre = cli;
        }

        await EnviarCorreoAvisoAsync(conn, request, fecha, clienteNombre, usuario);

        var exists = await conn.QueryFirstOrDefaultAsync<int>(
     @"SELECT COUNT(*) FROM aviso
  WHERE bodega='01' AND cvia='1' AND clienteid=@clienteId
  AND cnocaja=@caja AND lugar_arribo='09' AND tipop='E'",
     new { clienteId = request.ClienteId, caja = request.Caja });

        result.Success = true;
        result.Message = "PROCESO FINALIZADO";
        return result;
    }

    private async Task EnviarCorreoAvisoAsync(IDbConnection conn, NuevoAvisoRequest request,
   DateTime fecha, string clienteNombre, string usuario)
    {
        string subject = $"Nuevo Aviso - Caja: {request.Caja}";
        string body = BuildBodyAviso(request, fecha, clienteNombre);

        var config = await GetCorreoConfigAsync("NOT-PREV-NT");
        if (config != null)
        {
            if (!string.IsNullOrEmpty(config.Subject))
                subject = config.Subject.Replace("[cnocaja]", request.Caja);
            if (!string.IsNullOrEmpty(config.Body))
            {
                body = config.Body
               .Replace("[bodega]", "01")
               .Replace("[creferencia]", request.Caja)
               .Replace("[link]", BuildFotoLink(request.Caja))
               .Replace("[cliente]", $"{clienteNombre} - {request.ClienteId}")
               .Replace("[caja]", request.Caja)
               .Replace("[observaciones]", request.Observaciones ?? "")
               .Replace("[fecha]", fecha.ToString("dd/MM/yyyy HH:mm"))
               .Replace("[proveedor]", "");
            }
        }

        var recipients = new List<string>
   {
   "silvia@imexfwd.com", "mariana.ortegon@imexfwd.com",
   "trafico.expo@imexfwd.com", "hector.lopez@imexfwd.com"
   };

        var clientEmails = await GetCorreosClienteAsync(request.ClienteId, "E");
        recipients.AddRange(clientEmails);
        recipients.Add("notificacionespmmlaredotx@imexnotify.com");

        await _smtp.EnviarAsync(recipients, subject, body);
    }


    public async Task<List<ClienteExpoInfo>> GetClientesExportacionAsync(string? filtro)
    {
        using IDbConnection conn = _db.CreateConnection();

        string sql;
        if (string.IsNullOrEmpty(filtro))
        {
            sql = @"SELECT cidcte AS IdCliente, cnombre AS Nombre, carpeta AS Carpeta
 FROM clientes
 WHERE cteconta = 0 AND inactivo = 0 AND pedsaaim3e = 1
 ORDER BY cnombre";
            var rows = await conn.QueryAsync<ClienteExpoInfo>(sql);
            return rows.ToList();
        }
        else
        {
            sql = @"SELECT cidcte AS IdCliente, cnombre AS Nombre, carpeta AS Carpeta
 FROM clientes
 WHERE (cidcte LIKE @f OR cnombre LIKE @f)
   AND cteconta = 0 AND inactivo = 0 AND pedsaaim3e = 1
 ORDER BY cnombre";
            var rows = await conn.QueryAsync<ClienteExpoInfo>(sql, new { f = $"%{filtro}%" });
            return rows.ToList();
        }
    }


    private async Task<CorreoConfig?> GetCorreoConfigAsync(string idReporte)
    {
        using IDbConnection conn = _db.CreateReportesConnection();
        const string sql = @"
   SELECT subject, body
   FROM reportes.config_correo
   LEFT JOIN reportes.notificaciones ON reporte='AUTOMATICO'
   WHERE id_Reporte = @id";
        return await conn.QueryFirstOrDefaultAsync<CorreoConfig>(sql, new { id = idReporte });
    }

    private async Task<List<string>> GetCorreosClienteAsync(string? clienteId, string? tipop)
    {
        if (string.IsNullOrEmpty(clienteId)) return new List<string>();

        try
        {
            using IDbConnection conn = _db.CreateConnection();
            var emails = (await conn.QueryAsync<string>(
           @"SELECT cemail FROM correoscl
    WHERE inactivo = 0 AND not_salida_caja = 1
 AND (tipoop = '' OR tipoop LIKE @tipo)
 AND cidcte = @cliente",
           new { tipo = $"%{tipop}%", cliente = clienteId })).ToList();
            return emails;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al obtener correos del cliente {ClienteId}", clienteId);
            return new List<string>();
        }
    }

    private static string FormatearReferencia(string cref)
    {
        if (string.IsNullOrEmpty(cref) || cref.Length < 15) return cref;
        return $"{cref[..2]}-{cref[2..3]}-{cref[3..5]}-{cref[5..11]}-{cref[11..13]}-{cref[13..15]}";
    }

    private static string BuildFotoLink(string referencia)
    {
        return $"<a href='http://classifile002.classifile.mx/es/viewer_grid?mk=:WGpLOlFGTnViVEdIV1lseFNEZGlje2Q2WntXUlFscWhbRWlDWG1DaVRHWg&ctg=docs&keys={{\"ref\":\"{referencia}\",\"tipo\":\"102\"}}'>Fotos de Previo</a>";
    }

    private static string BuildBodyPrevio(DatosPrevio datos, string crefFormateado)
    {
        return $@"
   <h2>Notificacion Previa</h2>
   <p><b>Referencia:</b> {crefFormateado}</p>
   <p><b>Cliente:</b> {datos.Cliente}</p>
   <p><b>Bodega:</b> {datos.Bodega}</p>
   <p><b>Caja/Factura:</b> {datos.Factura}</p>
   <p><b>Proveedor:</b> {datos.Proveedor}</p>
   <p><b>Pedido:</b> {datos.Pedido}</p>
   <p><b>Material:</b> {datos.Material}</p>
   <p><b>Observaciones:</b> {datos.Observaciones}</p>
   <hr>
   <p>{BuildFotoLink(datos.Creferencia)}</p>";
    }

    private static string BuildBodyAviso(NuevoAvisoRequest request, DateTime fecha, string clienteNombre)
    {
        return $@"
   <h2>Nuevo Aviso - Caja sin Trafico</h2>
   <p><b>Caja:</b> {request.Caja}</p>
   <p><b>Cliente:</b> {clienteNombre} - {request.ClienteId}</p>
   <p><b>Fecha de Arribo:</b> {fecha:dd/MM/yyyy HH:mm}</p>
   <p><b>Observaciones:</b> {request.Observaciones}</p>
   <hr>
   <p>{BuildFotoLink(request.Caja)}</p>";
    }
}

internal class PrevioDbRow
{
    public string Creferencia { get; set; } = string.Empty;
    public string Cclienteidtraf { get; set; } = string.Empty;
    public string Bodega { get; set; } = string.Empty;
    public string Tipop { get; set; } = string.Empty;
    public string Cnocaja { get; set; } = string.Empty;
    public int Idtraf { get; set; }
}

internal class PrevioFullDbRow
{
    public int Idtraf { get; set; }
    public string Creferencia { get; set; } = string.Empty;
    public string Bodega { get; set; } = string.Empty;
    public string Tipop { get; set; } = string.Empty;
    public string Cclienteidtraf { get; set; } = string.Empty;
    public string Hashtag { get; set; } = string.Empty;
    public string Cidcte { get; set; } = string.Empty;
    public string Cnombre { get; set; } = string.Empty;
    public string CreferenciaCli { get; set; } = string.Empty;
    public string Cnofact { get; set; } = string.Empty;
    public string Proveedor { get; set; } = string.Empty;
    public string Pedido { get; set; } = string.Empty;
    public string Material { get; set; } = string.Empty;
}

internal class DatosPrevio
{
    public string Creferencia { get; set; } = string.Empty;
    public string Bodega { get; set; } = string.Empty;
    public string Tipop { get; set; } = string.Empty;
    public string Cclienteidtraf { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string IdCliente { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string Factura { get; set; } = string.Empty;
    public string Proveedor { get; set; } = string.Empty;
    public string Pedido { get; set; } = string.Empty;
    public string Material { get; set; } = string.Empty;
    public string Observaciones { get; set; } = string.Empty;
}

internal class CorreoConfig
{
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
