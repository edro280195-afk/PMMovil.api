using System.Data;
using System.Text;

using ClosedXML.Excel;

using Dapper;

using MailKit.Net.Smtp;
using MailKit.Security;

using MimeKit;

using PMMovil.Api.Data;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public class CartaPorteService
{
    // En Development, el correo de Carta Porte (con el Excel adjunto) se redirige
    // únicamente a esta casilla, sin importar el destinatario real traído de base
    // de datos (detalle.Usuario.Correo).
    private const string CorreoPruebasDesarrollo = "desarrollo3@grupopmm.com";

    private readonly BaseRepository _db;
    private readonly IConfiguration _config;
    private readonly ILogger<CartaPorteService> _logger;
    private readonly IHostEnvironment _env;

    public CartaPorteService(BaseRepository db, IConfiguration config, ILogger<CartaPorteService> logger, IHostEnvironment env)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _env = env;
    }


    public async Task<CartaPorteDetalle> GetCartaPorteAsync(string trafico, string origen, string usu)
    {
        var result = new CartaPorteDetalle();

        if (string.IsNullOrEmpty(trafico) || string.IsNullOrEmpty(origen) || string.IsNullOrEmpty(usu))
        {
            result.Message = "Datos incorrectos/incompletos";
            return result;
        }

        using IDbConnection conn = _db.CreateConnection();

        var trafRow = await GetTraficoAsync(conn, trafico);
        if (trafRow == null)
        {
            result.Message = "Este Pedimento no Existe";
            return result;
        }

        result.Trafico = trafRow;

        if (!string.IsNullOrEmpty(trafRow.Transfer))
        {
            var transfer = await GetTransferAsync(conn, trafRow.Transfer);
            result.Transfer = transfer;
        }

        if (!string.IsNullOrEmpty(trafRow.UbicaOrigen))
        {
            result.UbicacionOrigen = await GetUbicacionAsync(conn, trafRow.UbicaOrigen);
        }

        if (!string.IsNullOrEmpty(trafRow.UbicaDestino))
        {
            result.UbicacionDestino = await GetUbicacionAsync(conn, trafRow.UbicaDestino);
        }

        result.Partidas = await GetPartidasAsync(conn, trafico);

        var usuario = await GetUsuarioAsync(conn, usu);
        result.Usuario = usuario;

        result.Success = true;
        result.Message = "OK";
        return result;
    }


    public async Task<TransferirResult> TransferirAsync(string trafico, string origen, string usu)
    {
        var result = new TransferirResult();

        var detalle = await GetCartaPorteAsync(trafico, origen, usu);
        if (!detalle.Success || detalle.Trafico == null)
        {
            result.Message = detalle.Message;
            return result;
        }

        try
        {
            string rutaBase = _config["CartaPorte:RutaExcel"] ?? @"C:\CartasPorte\";
            if (!Directory.Exists(rutaBase))
                Directory.CreateDirectory(rutaBase);

            string archivo = Path.Combine(rutaBase, $"CartaPorte_{trafico}.xlsx");

            if (File.Exists(archivo))
                File.Delete(archivo);

            GenerarExcel(archivo, detalle);

            if (detalle.Usuario != null && !string.IsNullOrEmpty(detalle.Usuario.Correo))
            {
                await EnviarCorreoAsync(detalle, archivo);
            }

            result.Success = true;
            result.Message = "INFORMACION TRANSFERIDA";
            result.ArchivoGenerado = archivo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al transferir carta porte {Trafico}", trafico);
            result.Message = "Error al transferir: " + ex.Message;
        }

        return result;
    }


    private async Task<TraficoInfo?> GetTraficoAsync(IDbConnection conn, string cref)
    {
        const string sql = @"
   SELECT
  traf.creferencia,
  traf.cclienteidtraf,
  traf.bodega,
  traf.tipop,
  traf.caduana,
  traf.nnopatente,
  traf.pedimento,
  traf.ctamex,
  traf.ctaame,
  traf.ctatipo,
  traf.cidtransfer AS `Transfer`,
  traf.ubicac_origen AS UbicaOrigen,
  traf.ubicac_destino AS UbicaDestino,
  traf.placas,
  traf.vehiculo,
  traf.cidmanejo,
  traf.fcruce AS FechaCruce,
  clientes.cnombre AS Cliente,
  clientes.crfc AS Rfc,
  clientes.cregimen AS Regimen,
  GROUP_CONCAT(DISTINCT TRIM(furgon.carno)) AS Cnocaja
   FROM traf
   LEFT JOIN clientes ON clientes.cidcte = traf.cclienteidtraf
   LEFT JOIN furgon ON traf.creferencia = furgon.creferencia
   WHERE traf.creferencia = @cref
   GROUP BY traf.creferencia";

        return await conn.QueryFirstOrDefaultAsync<TraficoInfo>(sql, new { cref });
    }

    private async Task<TransferDbRow?> GetTransferRawAsync(IDbConnection conn, string clave)
    {
        const string sql = "SELECT * FROM tranfers WHERE cidtransfer = @clave";
        return await conn.QueryFirstOrDefaultAsync<TransferDbRow>(sql, new { clave });
    }

    private async Task<TransferInfo?> GetTransferAsync(IDbConnection conn, string clave)
    {
        var row = await GetTransferRawAsync(conn, clave);
        if (row == null) return null;

        return new TransferInfo
        {
            CidTransfer = row.Cidtransfer ?? "",
            Nombre = row.Cnombre ?? "",
            Rfc = row.Crfc ?? "PENDIENTE CAMPO",
            Calle = row.Cdireccion ?? "",
            NumExt = row.Cnext ?? "PENDIENTE LLENAR",
            NumInt = row.Cnint ?? "PENDIENTE LLENAR",
            CveColonia = row.Cvecolonia ?? "PENDIENTE LLENAR Y ENLAZAR",
            Colonia = row.Ccolonia ?? "",
            CodPostal = row.Ccodpostal ?? "",
            CveLocalidad = row.Cvecolonia ?? "PENDIENTE LLENAR Y ENLAZAR",
            Localidad = row.Clocalidad ?? "",
            CveMunicipio = row.Cciudad ?? "PENDIENTE LLENAR Y ENLAZAR",
            Municipio = row.Cciudad ?? "",
            CveEstado = row.Cestado ?? "",
            Estado = row.Cestado ?? "",
            Contacto = row.Ccontacto ?? "PENDIENTE LLENAR",
            Telefono = row.Ctelefono ?? "PENDIENTE LLENAR",
            Email = ParseEmail(row.Recep_cporte ?? row.Cemail ?? "")
        };
    }

    private async Task<UbicacionInfo?> GetUbicacionAsync(IDbConnection conn, string clave)
    {
        const string sql = "SELECT * FROM ubicacion_cpt WHERE cidubicacion = @clave";
        var row = await conn.QueryFirstOrDefaultAsync<UbicacionDbRow>(sql, new { clave });
        if (row == null) return null;

        return new UbicacionInfo
        {
            CidUbicacion = row.Cidubicacion ?? "",
            Nombre = row.Cnombre ?? "",
            Rfc = row.Crfc ?? "",
            Calle = row.Cdireccion ?? "",
            NumExt = row.Cnext ?? "",
            NumInt = row.Cnint ?? "",
            CveColonia = row.Cvecolonia ?? "",
            Colonia = row.Ccolonia ?? "",
            CodPostal = row.Ccodpostal ?? "",
            CveLocalidad = row.Cciudad_sat ?? "",
            Localidad = row.Clocalidad ?? "",
            CveMunicipio = row.Cciudad_sat ?? "",
            Municipio = row.Cmunicipio ?? "",
            CveEstado = row.Cestado ?? "",
            Estado = row.Cestado ?? "",
            Contacto = row.Ccontacto ?? "",
            Telefono = row.Ctelefono ?? "",
            Email = row.Cemail ?? ""
        };
    }

    private async Task<List<PartidaInfo>> GetPartidasAsync(IDbConnection conn, string cref)
    {
        const string sql = @"
   SELECT
  revdet.cpt_cve_bienes AS BienesTrans,
  revdet.cdescripcion AS DescBienes,
  revdet.ccantidad AS Cantidad,
  revdet.cpt_cve_unidad AS Unidad,
  CONCAT(IFNULL(unidad.nom_unidad,''), ' ', IFNULL(unidad.descripcion,'')) AS UnidadSat,
  revdet.cpt_cve_embalaje AS Embalaje,
  IFNULL(emb.cdescripcion, '') AS DescEmbalaje,
  revdet.npeso AS Peso,
  revdet.cfraccion AS Fraccion,
  revdet.cuuid AS Uuid,
  CASE WHEN revdet.lhazmat = '1' THEN 'SI' ELSE 'NO' END AS Hazmat,
  IFNULL(revdet.cpt_cve_materiapeligrosa, '') AS HazmatSat,
  revdet.nvalorpartidame AS ValorMerc,
  revdet.cmoneda AS Moneda,
  IFNULL(revdet.cpt_tipomateria, '') AS TipoMateria
   FROM fact
   INNER JOIN revdet ON fact.idfactura = revdet.idfactura
   LEFT JOIN pmmutileria.cpt_cve_unidadpeso unidad ON revdet.cpt_cve_unidad = unidad.cve_unidad
   LEFT JOIN pmmutileria.cpt_embalaje emb ON revdet.cpt_cve_embalaje = emb.cve_embalaje
   WHERE fact.creferencia = @cref AND revdet.norequerida = 0
   ORDER BY fact.cnofact, revdet.nlineno";

        var rows = (await conn.QueryAsync<PartidaDbRow>(sql, new { cref })).ToList();

        return rows.Select(r => new PartidaInfo
        {
            BienesTrans = r.BienesTrans ?? "",
            DescBienes = r.DescBienes ?? "",
            Cantidad = double.TryParse(r.Cantidad, out var cant) ? cant : 0,
            Unidad = r.Unidad ?? "",
            UnidadSat = r.UnidadSat ?? "",
            Embalaje = r.Embalaje ?? "",
            DescEmbalaje = r.DescEmbalaje ?? "",
            Peso = double.TryParse(r.Peso, out var peso) ? Math.Round(peso * 0.4536, 3) : 0,
            Fraccion = r.Fraccion ?? "",
            Uuid = r.Uuid ?? "",
            Hazmat = r.Hazmat ?? "NO",
            HazmatSat = r.HazmatSat ?? "",
            ValorMerc = double.TryParse(r.ValorMerc, out var val) ? val : 0,
            Moneda = r.Moneda ?? "",
            TipoMateria = r.TipoMateria ?? ""
        }).ToList();
    }

    private async Task<UsuarioCartaPorte?> GetUsuarioAsync(IDbConnection conn, string usu)
    {
        const string sql = @"
   SELECT claveusuario AS IdUsuario,
     CONCAT_WS(' ', TRIM(apaterno), TRIM(amaterno), TRIM(nombre)) AS Nombre,
     correo AS Correo
   FROM usuarios
   WHERE claveusuario = @usu AND inactivo = 0";

        return await conn.QueryFirstOrDefaultAsync<UsuarioCartaPorte>(sql, new { usu });
    }


    private void GenerarExcel(string archivo, CartaPorteDetalle detalle)
    {
        var t = detalle.Trafico!;
        var traf = detalle.Transfer;
        var origen = detalle.UbicacionOrigen;
        var destino = detalle.UbicacionDestino;

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("CARTA PORTE");

        var labelStyle = wb.Style;
        labelStyle.Fill.BackgroundColor = XLColor.LightGray;
        labelStyle.Font.Bold = true;
        labelStyle.Font.FontName = "Arial";
        labelStyle.Font.FontSize = 10;

        var headerStyle = wb.Style;
        headerStyle.Fill.BackgroundColor = XLColor.FromArgb(0, 0, 102);
        headerStyle.Font.FontColor = XLColor.White;
        headerStyle.Font.Bold = true;
        headerStyle.Font.FontName = "Arial";
        headerStyle.Font.FontSize = 10;

        var titleStyle = wb.Style;
        titleStyle.Font.Bold = true;
        titleStyle.Font.FontName = "Arial";
        titleStyle.Font.FontSize = 20;

        var normalStyle = wb.Style;
        normalStyle.Font.FontName = "Arial";
        normalStyle.Font.FontSize = 10;

        int row = 1;

        ws.Cell(row, 1).Value = "CARTA PORTE";
        ws.Cell(row, 1).Style = titleStyle;
        row += 2;

        ws.Cell(row, 1).Value = "INFORMACION DEL REMOLQUE";
        ws.Cell(row, 1).Style = labelStyle;
        row++;

        var datosRemolque = new Dictionary<string, string>
        {
            ["Tipo"] = t.Tipop ?? "",
            ["Remolque"] = t.Cnocaja ?? "",
            ["Placas"] = t.Placas ?? "",
            ["Referencias"] = t.Creferencia ?? "",
            ["Regimen Aduanero"] = t.Regimen ?? "",
            ["Cliente"] = t.Cliente ?? ""
        };

        foreach (var kv in datosRemolque)
        {
            ws.Cell(row, 1).Value = kv.Key;
            ws.Cell(row, 1).Style = labelStyle;
            ws.Cell(row, 2).Value = kv.Value;
            ws.Cell(row, 2).Style = normalStyle;
            row++;
        }

        row += 2;

        ws.Cell(row, 1).Value = "EMBARCADOR";
        ws.Cell(row, 1).Style = labelStyle;
        row++;

        var dataEmb = new Dictionary<string, string>
        {
            ["Razon Social"] = traf?.Nombre ?? "",
            ["RFC/Tax ID"] = traf?.Rfc ?? "",
            ["Calle"] = traf?.Calle ?? "",
            ["Numero Exterior"] = traf?.NumExt ?? "",
            ["Numero Interior"] = traf?.NumInt ?? "",
            ["Clave Colonia SAT"] = traf?.CveColonia ?? "",
            ["Colonia SAT"] = traf?.Colonia ?? "",
            ["CP"] = traf?.CodPostal ?? "",
            ["Clave Localidad SAT"] = traf?.CveLocalidad ?? "",
            ["Localidad"] = traf?.Localidad ?? "",
            ["Clave Municipio SAT"] = traf?.CveMunicipio ?? "",
            ["Municipio SAT"] = traf?.Municipio ?? "",
            ["Clave Estado SAT"] = traf?.CveEstado ?? "",
            ["Estado SAT"] = traf?.Estado ?? "",
            ["Contacto"] = traf?.Contacto ?? "",
            ["Telefono"] = traf?.Telefono ?? "",
            ["Correo"] = traf?.Email ?? ""
        };

        foreach (var kv in dataEmb)
        {
            ws.Cell(row, 1).Value = kv.Key;
            ws.Cell(row, 1).Style = labelStyle;
            ws.Cell(row, 2).Value = kv.Value;
            ws.Cell(row, 2).Style = normalStyle;
            row++;
        }

        row = 10; ws.Cell(row, 5).Value = "DESTINATARIO";
        ws.Cell(row, 5).Style = labelStyle;
        row++;

        var dataDest = new Dictionary<string, string>
        {
            ["Razon Social"] = destino?.Nombre ?? "",
            ["RFC/Tax ID"] = destino?.Rfc ?? "",
            ["Calle"] = destino?.Calle ?? "",
            ["Numero Exterior"] = destino?.NumExt ?? "",
            ["Numero Interior"] = destino?.NumInt ?? "",
            ["Clave Colonia SAT"] = destino?.CveColonia ?? "",
            ["Colonia SAT"] = destino?.Colonia ?? "",
            ["CP"] = destino?.CodPostal ?? "",
            ["Clave Localidad SAT"] = destino?.CveLocalidad ?? "",
            ["Localidad"] = destino?.Localidad ?? "",
            ["Clave Municipio SAT"] = destino?.CveMunicipio ?? "",
            ["Municipio SAT"] = destino?.Municipio ?? "",
            ["Clave Estado SAT"] = destino?.CveEstado ?? "",
            ["Estado SAT"] = destino?.Estado ?? "",
            ["Contacto"] = destino?.Contacto ?? "",
            ["Telefono"] = destino?.Telefono ?? "",
            ["Correo"] = destino?.Email ?? ""
        };

        int destRow = 11;
        foreach (var kv in dataDest)
        {
            ws.Cell(destRow, 5).Value = kv.Key;
            ws.Cell(destRow, 5).Style = labelStyle;
            ws.Cell(destRow, 6).Value = kv.Value;
            ws.Cell(destRow, 6).Style = normalStyle;
            destRow++;
        }

        int detRow = Math.Max(row, destRow) + 2;
        ws.Cell(detRow, 1).Value = "DETALLE DE MERCANCIA";
        ws.Cell(detRow, 1).Style = labelStyle;
        detRow++;

        var headers = new[] {
   "FECHA", "Bienes Transportados", "Descripcion bienes", "Cantidad",
   "Clave Unidad", "Unidad (SAT)", "Embalaje (SAT)", "Descripcion embalaje",
   "Peso Kg", "Fraccion Arancelaria", "UUID", "Pedimento",
   "Mat Peligroso", "Mat Peligroso (Clave)", "Valor mcia", "Moneda",
   "Tipo Materia (SAT)", "RFC Importador", "Regimen", "Transporte"
   };

        for (int c = 0; c < headers.Length; c++)
        {
            ws.Cell(detRow, c + 1).Value = headers[c];
            ws.Cell(detRow, c + 1).Style = headerStyle;
        }
        detRow++;

        string fecha = DateTime.Now.ToString("dd/MM/yyyy");
        string pedimentoCompleto = FormatearPedimento(t.Caduana, t.Nnopatente, t.Pedimento);
        string transporte = t.Tipop?.ToUpper() == "I" ? "SI" : "";
        string importador = "";
        string regimen = t.Regimen ?? "";

        foreach (var partida in detalle.Partidas)
        {
            int c = 1;
            ws.Cell(detRow, c++).Value = fecha;
            ws.Cell(detRow, c++).Value = partida.BienesTrans;
            ws.Cell(detRow, c++).Value = partida.DescBienes;
            ws.Cell(detRow, c++).Value = partida.Cantidad;
            ws.Cell(detRow, c++).Value = partida.Unidad;
            ws.Cell(detRow, c++).Value = partida.UnidadSat;
            ws.Cell(detRow, c++).Value = partida.Embalaje;
            ws.Cell(detRow, c++).Value = partida.DescEmbalaje;
            ws.Cell(detRow, c++).Value = partida.Peso;
            ws.Cell(detRow, c++).Value = partida.Fraccion;
            ws.Cell(detRow, c++).Value = partida.Uuid;
            ws.Cell(detRow, c++).Value = pedimentoCompleto;
            ws.Cell(detRow, c++).Value = partida.Hazmat;
            ws.Cell(detRow, c++).Value = partida.HazmatSat;
            ws.Cell(detRow, c++).Value = partida.ValorMerc;
            ws.Cell(detRow, c++).Value = partida.Moneda;
            ws.Cell(detRow, c++).Value = partida.TipoMateria;
            ws.Cell(detRow, c++).Value = importador;
            ws.Cell(detRow, c++).Value = regimen;
            ws.Cell(detRow, c++).Value = transporte;

            for (int col = 1; col <= headers.Length; col++)
                ws.Cell(detRow, col).Style = normalStyle;

            detRow++;
        }

        ws.Columns().AdjustToContents();
        wb.SaveAs(archivo);
    }


    private async Task EnviarCorreoAsync(CartaPorteDetalle detalle, string archivo)
    {
        try
        {
            var t = detalle.Trafico!;

            string host = _config["Smtp:Host"] ?? "";
            string portStr = _config["Smtp:Port"] ?? "587";
            string user = _config["Smtp:User"] ?? "";
            string pass = _config["Smtp:Password"] ?? "";
            string from = _config["Smtp:From"] ?? user;
            string fromName = _config["Smtp:FromName"] ?? "PMMovil";
            bool useSsl = _config["Smtp:UseSsl"] == "true";
            bool useStartTls = _config["Smtp:UseStartTls"] != "false";

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from))
            {
                _logger.LogWarning("CartaPorte: SMTP no configurado, no se envió correo");
                return;
            }

            int port = int.TryParse(portStr, out var p) ? p : 587;

            string subject = $"Carta Porte - {t.Pedimento}";
            string body = $@"
  <h2>Carta Porte</h2>
  <p><b>Referencia:</b> {t.Creferencia}</p>
  <p><b>Pedimento:</b> {t.Pedimento}</p>
  <p><b>Cliente:</b> {t.Cliente}</p>
  <p><b>Fecha:</b> {DateTime.Now:dd/MM/yyyy HH:mm:ss}</p>
  <hr>
  <p>Se adjunta archivo Excel con la Carta Porte.</p>";

            string destinatario = detalle.Usuario?.Correo ?? from;
            if (_env.IsDevelopment())
            {
                _logger.LogInformation("CartaPorte: entorno Development, se redirige el envío (destinatario original: {Original}) a {Destino}.", destinatario, CorreoPruebasDesarrollo);
                destinatario = CorreoPruebasDesarrollo;
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, from));
            message.To.Add(MailboxAddress.Parse(destinatario));
            message.Subject = subject;

            var builder = new BodyBuilder { HtmlBody = body };
            builder.Attachments.Add(archivo);
            message.Body = builder.ToMessageBody();

            using var client = new MailKit.Net.Smtp.SmtpClient();
            client.Timeout = 15000;

            SecureSocketOptions secureOption = SecureSocketOptions.Auto;
            if (useSsl) secureOption = SecureSocketOptions.SslOnConnect;
            else if (useStartTls) secureOption = SecureSocketOptions.StartTls;

            await client.ConnectAsync(host, port, secureOption);
            if (!string.IsNullOrWhiteSpace(user))
                await client.AuthenticateAsync(user, pass ?? "");

            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("CartaPorte: correo enviado a {Dest} con adjunto {Archivo}",
           destinatario, archivo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CartaPorte: error al enviar correo (no crítico)");
        }
    }


    private static string FormatearPedimento(string? aduana, string? patente, string? pedimento)
    {
        string yy = DateTime.Now.ToString("yy");
        return $"{yy} {aduana ?? ""} {patente ?? ""} {pedimento ?? ""}".Trim();
    }

    private static string ParseEmail(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return raw.Split(';').Select(e => e.Trim()).FirstOrDefault(e => e.Contains('@')) ?? "";
    }

    public string GetExcelPath(string trafico)
    {
        string rutaBase = _config["CartaPorte:RutaExcel"] ?? @"C:\CartasPorte\";
        return Path.Combine(rutaBase, $"CartaPorte_{trafico}.xlsx");
    }
}

internal class TransferDbRow
{
    public string Cidtransfer { get; set; } = string.Empty;
    public string Cnombre { get; set; } = string.Empty;
    public string Crfc { get; set; } = string.Empty;
    public string Cdireccion { get; set; } = string.Empty;
    public string Cnext { get; set; } = string.Empty;
    public string Cnint { get; set; } = string.Empty;
    public string Cvecolonia { get; set; } = string.Empty;
    public string Ccolonia { get; set; } = string.Empty;
    public string Ccodpostal { get; set; } = string.Empty;
    public string Clocalidad { get; set; } = string.Empty;
    public string Cciudad { get; set; } = string.Empty;
    public string Cestado { get; set; } = string.Empty;
    public string Ccontacto { get; set; } = string.Empty;
    public string Ctelefono { get; set; } = string.Empty;
    public string Cemail { get; set; } = string.Empty;
    public string Recep_cporte { get; set; } = string.Empty;
}

internal class UbicacionDbRow
{
    public string Cidubicacion { get; set; } = string.Empty;
    public string Cnombre { get; set; } = string.Empty;
    public string Crfc { get; set; } = string.Empty;
    public string Cdireccion { get; set; } = string.Empty;
    public string Cnext { get; set; } = string.Empty;
    public string Cnint { get; set; } = string.Empty;
    public string Cvecolonia { get; set; } = string.Empty;
    public string Ccolonia { get; set; } = string.Empty;
    public string Ccodpostal { get; set; } = string.Empty;
    public string Cciudad_sat { get; set; } = string.Empty;
    public string Clocalidad { get; set; } = string.Empty;
    public string Cmunicipio { get; set; } = string.Empty;
    public string Cestado { get; set; } = string.Empty;
    public string Ccontacto { get; set; } = string.Empty;
    public string Ctelefono { get; set; } = string.Empty;
    public string Cemail { get; set; } = string.Empty;
}

internal class PartidaDbRow
{
    public string BienesTrans { get; set; } = string.Empty;
    public string DescBienes { get; set; } = string.Empty;
    public string Cantidad { get; set; } = "0";
    public string Unidad { get; set; } = string.Empty;
    public string UnidadSat { get; set; } = string.Empty;
    public string Embalaje { get; set; } = string.Empty;
    public string DescEmbalaje { get; set; } = string.Empty;
    public string Peso { get; set; } = "0";
    public string Fraccion { get; set; } = string.Empty;
    public string Uuid { get; set; } = string.Empty;
    public string Hazmat { get; set; } = "NO";
    public string HazmatSat { get; set; } = string.Empty;
    public string ValorMerc { get; set; } = "0";
    public string Moneda { get; set; } = string.Empty;
    public string TipoMateria { get; set; } = string.Empty;
}
