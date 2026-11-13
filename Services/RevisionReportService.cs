using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Data;

namespace PMMovil.Api.Services;

public interface IRevisionReportService
{
    /// <summary>
    /// Manda el correo de revisión finalizada al cliente. Los dos últimos parámetros son para el modo admin
    /// <c>--notificar-revision-finalizada</c> (Program.cs); el flujo normal no los usa:
    /// <paramref name="usuarioRevisorClaveManual"/> fija quién revisó cuando la sesión no quedó cerrada con su clave, y
    /// <paramref name="destinatariosManual"/>, si trae algo, manda el correo SOLO a esas casillas, sin copias.
    /// </summary>
    Task<bool> EnviarReporteRevisionNormalAsync(
        string creferencia,
        string? baseUrl = null,
        string? usuarioRevisorClaveManual = null,
        IReadOnlyCollection<string>? destinatariosManual = null);
    Task<bool> EnviarReporteBarcodeAsync(int idSesion);
}

public class RevisionReportService : IRevisionReportService
{
    // Copia oculta del reporte de barcode durante la operación.
    private const string CorreoDesarrolloPrincipal = "desarrollo@grupopmm.com";
    private const string CorreoDesarrollo = "desarrollo3@grupopmm.com";
    private const string CorreoCarmen = "carmen.barragan@grupopmm.com";
    private const string CorreoCalidad = "calidad@grupopmm.com";
    private const string CorreoSergio = "sergio.miranda@grupopmm.com";
    private const string CorreoHumberto = "humberto.sanchez@grupopmm.com";
    private const string CorreoArturo = "arturo.martinez@grupopmm.com";
    private const string CorreoPilar = "pilar.dominguez@grupopmm.com";
    private const string CorreoNancy = "nancy.duran@grupopmm.com";
    private const string CorreoAlicia = "alicia.medina@grupopmm.com";
    private const string CorreoTatis = "tatis.guevara@grupopmm.com";
    private const string CorreoMariana = "mariana.ortegon@grupopmm.com";
    private const string CorreoBarcodeRemitente = "PMMovil <notificacionespmm@imexnotify.com>";
    private const string CorreoBarcodeRenato = "renato.araujo@grupopmm.com";
    private const string CorreoBarcodeRoxana = "roxana.figueroa@grupopmm.com";
    private const string CorreoBarcodeDespacho = "despacho.ferrocarril@grupopmm.com";
    private const string CorreoBarcodeMaru = "maru.martinez@grupopmm.com";
    private const string CorreoBarcodeMariana = "mariana.ortegon@grupopmm.com";
    private const string CorreoBarcodeRoberto = "roberto.garza@grupopmm.com";

    // CC fija del correo de revisión normal (feedback de Eduardo, 14/sept):
    // antes se copiaba a una lista larga de ~11 correos en cada envío; ahora
    // solo a estas 4 personas. desarrollo3 se movió a BCC (ver
    // CorreosBccRevisionNormal), ya no va en copia visible.
    private static readonly string[] CorreosCcRevisionNormal =
    [
        CorreoTatis,
        CorreoMariana,
        CorreoCalidad,
        CorreoAlicia
    ];

    private static readonly string[] CorreosBccRevisionNormal =
    [
        CorreoDesarrollo
    ];

    // Respaldo del "To" cuando el cliente de la referencia no tiene ningún
    // contacto en correoscl que califique (ver
    // ObtenerDestinatariosClienteAsync) -- se intenta primero el correo del
    // revisor y, si tampoco hay, estos dos.
    private static readonly string[] CorreosFallbackRevisionNormal =
    [
        CorreoSergio,
        CorreoHumberto
    ];

    private readonly BaseRepository _db;
    private readonly ISmtpService _smtp;
    private readonly IRevisionShareService _revisionShareService;
    private readonly ILogger<RevisionReportService> _logger;
    private readonly IConfiguration _config;

    public RevisionReportService(
        BaseRepository db,
        ISmtpService smtp,
        IRevisionShareService revisionShareService,
        ILogger<RevisionReportService> logger,
        IConfiguration config)
    {
        _db = db;
        _smtp = smtp;
        _revisionShareService = revisionShareService;
        _logger = logger;
        _config = config;
    }

    public async Task<bool> EnviarReporteRevisionNormalAsync(
        string creferencia,
        string? baseUrl = null,
        string? usuarioRevisorClaveManual = null,
        IReadOnlyCollection<string>? destinatariosManual = null)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();

            string sqlGeneral = @"
  SELECT
 f.cref_agrupada,
 MAX(f.cclienteid) AS cidcte,
 MAX(cl.cnombre) AS nombreCliente,
 MAX(p.cnombre) AS nombreProveedor,
 -- Subquery en vez de SUM(f.totbultos) directo: el LEFT JOIN a revdet de
 -- abajo repite cada factura una vez por partida (es la única forma de
 -- llegar a provs para el nombre del proveedor), y como totbultos/
 -- totbultosdoc son columnas de fact, un SUM sobre esas filas repetidas
 -- contaba el mismo bulto una vez por partida (2 bultos x 2 partidas = 4
 -- en el correo, cuando la factura real solo tenía 2). Esta subquery suma
 -- directo sobre fact, sin pasar por el join que las multiplica.
 (SELECT COALESCE(SUM(f2.totbultos), 0) FROM fact f2 WHERE f2.cref_agrupada = f.cref_agrupada) AS bultosFisicos,
 (SELECT COALESCE(SUM(f2.totbultosdoc), 0) FROM fact f2 WHERE f2.cref_agrupada = f.cref_agrupada) AS bultosDocumento,
 MAX(f.usurev) AS usuarioRevisorNombre,
 (SELECT NULLIF(TRIM(t.usurevisa), '')
    FROM traftiemposrevision t
   WHERE t.creferencia = f.cref_agrupada
     AND " + RevisionTimeRules.ClosedSessionPredicate("t") + @"
     AND NULLIF(TRIM(t.usurevisa), '') IS NOT NULL
   ORDER BY t.ffinrevision DESC, t.idtiemposrevision DESC
   LIMIT 1) AS usuarioRevisorClave,
 (SELECT NULLIF(TRIM(t3.observacion_revision), '')
    FROM traf3 t3
   WHERE t3.creferencia = f.cref_agrupada
   LIMIT 1) AS observacionRevision,
 (SELECT COALESCE(SUM(TIMESTAMPDIFF(SECOND, finiciorevision, ffinrevision)), 0)
  FROM traftiemposrevision
  WHERE creferencia = f.cref_agrupada
    AND " + RevisionTimeRules.ClosedSessionPredicate() + @") AS segundosTotales
  FROM fact f
  LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid
  LEFT JOIN revdet rd ON rd.idfactura = f.idfactura
  LEFT JOIN provs p ON p.cidprov = rd.cprovid
  WHERE f.cref_agrupada = @cref
  GROUP BY f.cref_agrupada";

            dynamic data = await conn.QueryFirstOrDefaultAsync<object>(sqlGeneral, new { cref = creferencia });
            if (data == null) return false;

            // MAX() en las columnas de "fact": con sql_mode=ONLY_FULL_GROUP_BY,
            // MySQL rechaza columnas no agregadas que no estén en el GROUP BY
            // (aunque sean funcionalmente dependientes de cnofact). Antes esto
            // tronaba con MySqlException y mataba el envío del correo completo.
            string sqlFacturas = @"
  SELECT
 f.cnofact,
 MAX(f.status_rev) AS status_rev,
 MAX(f.totbultos) AS totbultos,
 MAX(f.totbultosdoc) AS totbultosdoc,
 MAX(NULLIF(TRIM(f.discrepancias), '')) AS discrepancias,
 MAX(NULLIF(TRIM(f.obsrev), '')) AS observacionNormal,
 COUNT(rd.idrevdet) AS totalPartidas,
 SUM(rd.revisada) AS partidasRevisadas
  FROM fact f
  LEFT JOIN revdet rd ON rd.idfactura = f.idfactura
  WHERE f.cref_agrupada = @cref
  GROUP BY f.cnofact";

            List<dynamic> facturas = (await conn.QueryAsync<object>(sqlFacturas, new { cref = creferencia })).ToList();

            // El envío también se protege aquí, no solo en TimerController:
            // este servicio puede ser invocado por los cierres automáticos o
            // por otro consumidor del API. Solo se notifica cuando todas las
            // facturas están marcadas como completas (1) o completas con
            // discrepancia (2).
            List<string> facturasIncompletas = [];
            foreach (dynamic factura in facturas)
            {
                int status = Convert.ToInt32(factura.status_rev ?? 0);
                int totalPartidas = Convert.ToInt32(factura.totalPartidas ?? 0);
                int partidasRevisadas = Convert.ToInt32(factura.partidasRevisadas ?? 0);
                if (status is 1 or 2 && (totalPartidas == 0 || partidasRevisadas == totalPartidas))
                {
                    continue;
                }

                string cnofact = Convert.ToString(factura.cnofact) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(cnofact))
                {
                    facturasIncompletas.Add(cnofact);
                }
            }
            if (facturas.Count == 0 || facturasIncompletas.Count > 0)
            {
                _logger.LogWarning(
                    "No se envía reporte normal para {Cref}: {Total} facturas, {Incompletas} incompletas ({Facturas}).",
                    creferencia,
                    facturas.Count,
                    facturasIncompletas.Count,
                    string.Join(", ", facturasIncompletas));
                return false;
            }

            string usuarioRevisorClave = Convert.ToString(data.usuarioRevisorClave)?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(usuarioRevisorClaveManual))
            {
                // Modo admin: la sesión de revisión no quedó cerrada con la clave de quien revisó, así que la
                // consulta de arriba no la trae. El HTML lee al revisor de "data", por eso se escribe también ahí.
                usuarioRevisorClave = usuarioRevisorClaveManual.Trim();
                ((IDictionary<string, object>)data)["usuarioRevisorClave"] = usuarioRevisorClave;
            }
            string clienteId = Convert.ToString(data.cidcte)?.Trim() ?? string.Empty;

            // "To" ahora son los contactos del cliente (tabla correoscl) que
            // pidieron el aviso de revisión física -- más el revisor, que
            // siempre va en el To (feedback de Eduardo, 14/sept: "hay que
            // incluir al que revisó, él va en el To"), no solo como
            // respaldo cuando el cliente no tiene contactos.
            List<string> destinatarios = await ObtenerDestinatariosClienteAsync(conn, clienteId);
            string? correoRevisor = await ObtenerCorreoRevisorAsync(conn, usuarioRevisorClave);
            if (!string.IsNullOrWhiteSpace(correoRevisor) &&
                !destinatarios.Contains(correoRevisor, StringComparer.OrdinalIgnoreCase))
            {
                destinatarios.Add(correoRevisor);
            }
            if (destinatarios.Count == 0)
            {
                // Ni contactos en correoscl para este cliente ni correo de
                // revisor (p. ej. nadie tomó la referencia todavía) -- mismo
                // respaldo que se usaba antes de este cambio, para no
                // quedarnos sin avisar a nadie.
                destinatarios = CorreosFallbackRevisionNormal.ToList();
                _logger.LogInformation(
                    "Revisión normal {Cref}: cliente {Cliente} sin contactos en correoscl con not_revision_fisica activo (o ninguno con usuario activo en Nuevo Laredo, lugar 09) y sin correo de revisor; se usa respaldo ({Destino}).",
                    creferencia,
                    clienteId,
                    string.Join(", ", destinatarios));
            }

            List<string> copias = CorreosCcRevisionNormal
                .Where(correo => !destinatarios.Contains(correo, StringComparer.OrdinalIgnoreCase))
                .ToList();
            IEnumerable<string> copiasOcultas = CorreosBccRevisionNormal;

            if (destinatariosManual is { Count: > 0 })
            {
                // Modo admin en validación: el correo sale completo (mismo asunto, mismo cuerpo, mismo link)
                // pero solo a estas casillas, sin copia al cliente ni a los de CC/BCC.
                destinatarios = destinatariosManual.ToList();
                copias = [];
                copiasOcultas = [];
            }

            EstadoGeneralRevision estado = CalcularEstadoGeneral(facturas);

            // Link público de "Seguimiento de revisión" (ya existe, mismo que
            // usa el botón "Compartir" de la app) -- siempre va en el correo.
            // Ya no se adjuntan fotos al mensaje (podía pasar de 20 fotos y
            // rebasar el tope de Outlook/O365) -- todas viven en esta galería,
            // con su propio visor/carrusel y descarga individual o en ZIP.
            // La clave del usuario puede venir vacía si nadie ha tomado la
            // referencia todavía.
            string usuarioParaLink = usuarioRevisorClave.Length > 0
                ? usuarioRevisorClave
                : "sistema-correo";
            (string Token, DateTime Expira)? link = await _revisionShareService.GenerarLinkAsync(creferencia, usuarioParaLink);
            string origenLinks = string.IsNullOrWhiteSpace(baseUrl) ? (_config["PublicLinks:ApiBaseUrl"] ?? "") : baseUrl!.TrimEnd('/');
            string? galeriaUrl = link != null && !string.IsNullOrWhiteSpace(origenLinks)
                ? $"{origenLinks}/api/publico/revision/{link.Value.Token}"
                : null;

            ConteoFotosResultado conteoFotos = await ContarFotosAsync(conn, creferencia);

            string body = BuildRevisionNormalHtml(data, facturas, estado, galeriaUrl, conteoFotos);
            string subject = $"Revision de mercancía - {creferencia} - {data.nombreCliente}";

            return await _smtp.EnviarAsync(
                destinatarios,
                subject,
                body,
                copias: copias,
                copiasOcultas: copiasOcultas,
                incluirCopiasOcultasAutomaticas: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando reporte normal para {Cref}", creferencia);
            return false;
        }
    }

    // No hay llave entre correoscl (contactos por cliente) y usuarios
    // (cuentas internas de PMM) -- el cruce es por correo electrónico, y
    // sirve de candado de seguridad: un contacto de correoscl solo cuenta si
    // ese mismo correo pertenece además a una cuenta de PMM activa en
    // Nuevo Laredo (lugar '09'). Verificado contra datos reales (14/sept): de 9
    // contactos con not_revision_fisica=1 en toda la tabla (todos del grupo
    // ExxonMobil), 8 pasan este candado; el que no pasa
    // (sergio.miranda@grupopmm.com) sigue registrado como contacto pero su
    // usuario ya no está activo -- exactamente el caso que este filtro evita.
    private static async Task<List<string>> ObtenerDestinatariosClienteAsync(IDbConnection conn, string clienteId)
    {
        if (string.IsNullOrWhiteSpace(clienteId))
        {
            return [];
        }

        const string sql = @"
            SELECT DISTINCT c.cemail
            FROM correoscl c
            INNER JOIN usuarios u ON u.correo = c.cemail AND u.lugar = '09' AND u.inactivo = 0
            WHERE c.cidcte = @cliente
              AND c.not_revision_fisica = 1
              AND c.inactivo = 0";

        IEnumerable<string> correos = await conn.QueryAsync<string>(sql, new { cliente = clienteId });
        return correos
            .Where(correo => !string.IsNullOrWhiteSpace(correo))
            .Select(correo => correo.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<string?> ObtenerCorreoRevisorAsync(IDbConnection conn, string usuarioRevisorClave)
    {
        if (string.IsNullOrWhiteSpace(usuarioRevisorClave))
        {
            return null;
        }

        string? correo = await conn.QueryFirstOrDefaultAsync<string?>(
            @"SELECT NULLIF(TRIM(correo), '')
              FROM usuarios
              WHERE claveusuario = @usuario
              LIMIT 1",
            new { usuario = usuarioRevisorClave.Trim() });

        return string.IsNullOrWhiteSpace(correo) ? null : correo.Trim();
    }

    public async Task<bool> EnviarReporteBarcodeAsync(int idSesion)
    {
        try
        {
            using IDbConnection conn = _db.CreateConnection();
            conn.Open();

            string sqlMultiple = @"
  SELECT s.id_sesion AS idSesion, s.cref_agrupada AS crefAgrupada, s.cidcte, s.cidprov,
    s.barcode_long AS barcodeLong, s.usuario, s.fecha_inicio AS fechaInicio,
    s.observaciones,
    COALESCE((SELECT COUNT(*)
              FROM pmmutileria.revisiones_barcode_lectura l
              WHERE l.id_sesion = s.id_sesion AND l.eliminado = 0), 0) AS totalLecturas,
    COALESCE((SELECT COUNT(*)
              FROM pmmutileria.revisiones_barcode_lectura l
              WHERE l.id_sesion = s.id_sesion AND l.eliminado = 0 AND l.existe = 1), 0) AS totalExistentes,
    COALESCE((SELECT COUNT(*)
              FROM pmmutileria.revisiones_barcode_lectura l
              WHERE l.id_sesion = s.id_sesion AND l.eliminado = 0 AND l.existe = 0), 0) AS totalInexistentes,
    (SELECT cnombre FROM pmmtraf.clientes WHERE cidcte = s.cidcte LIMIT 1) AS nombreCliente,
    (SELECT cnombre FROM pmmtraf.provs WHERE cidprov = s.cidprov LIMIT 1) AS nombreProveedor
  FROM pmmutileria.revisiones_barcode_sesion s
  WHERE s.id_sesion = @id;

  SELECT CASE WHEN l.existe = 1 THEN l.cidproduct WHEN s.barcode_long > 0 THEN LEFT(l.codigo_raw, s.barcode_long) ELSE l.codigo_raw END AS Cidproduct,
    COUNT(DISTINCT l.id_lectura) AS Cantidad,
    MAX(l.existe) AS ExisteInt,
    COALESCE(MAX(p.cdescripcio), '') AS Descripcion
  FROM pmmutileria.revisiones_barcode_lectura l
  INNER JOIN pmmutileria.revisiones_barcode_sesion s ON s.id_sesion = l.id_sesion
  LEFT JOIN pmmtraf.products p ON p.cidproduct = l.cidproduct
  AND p.cclienteid = (SELECT cidcte FROM pmmutileria.revisiones_barcode_sesion WHERE id_sesion = @id)
  AND p.cprovid    = (SELECT cidprov FROM pmmutileria.revisiones_barcode_sesion WHERE id_sesion = @id)
  WHERE l.id_sesion = @id
    AND l.eliminado = 0
  GROUP BY CASE WHEN l.existe = 1 THEN l.cidproduct WHEN s.barcode_long > 0 THEN LEFT(l.codigo_raw, s.barcode_long) ELSE l.codigo_raw END
  ORDER BY Cantidad DESC";

            using var multi = await conn.QueryMultipleAsync(sqlMultiple, new { id = idSesion });
            dynamic sesion = await multi.ReadFirstOrDefaultAsync();
            if (sesion == null)
            {
                _logger.LogWarning("ReporteBarcode: No se encontró la sesión {Id}", idSesion);
                return false;
            }
            var filas = (await multi.ReadAsync<object>()).ToList();

            _logger.LogInformation("ReporteBarcode: Generando reporte para sesión {Id} con {N} productos.", idSesion, filas.Count);

            List<string> destinatarios =
            [
                CorreoBarcodeRemitente,
                CorreoBarcodeRenato,
                CorreoTatis,
                CorreoSergio,
                CorreoBarcodeRoxana,
                CorreoBarcodeDespacho,
                CorreoBarcodeMaru,
                CorreoBarcodeMariana,
                CorreoBarcodeRoberto,
                CorreoHumberto,
                CorreoAlicia,
                CorreoCalidad
            ];

            (string Asunto, string CuerpoHtml) email = BuildBarcodeHtml(sesion, filas);
            return await _smtp.EnviarAsync(
                destinatarios,
                email.Asunto,
                email.CuerpoHtml,
                copiasOcultas: [CorreoDesarrollo],
                incluirCopiasOcultasAutomaticas: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando reporte barcode para sesión {Id}", idSesion);
            return false;
        }
    }

    private string BuildRevisionNormalHtml(
        dynamic data,
        IEnumerable<dynamic> facturas,
        EstadoGeneralRevision estado,
        string? galeriaUrl,
        ConteoFotosResultado fotos)
    {
        string cref = (string)data.cref_agrupada;
        string cliente = (string)data.nombreCliente;
        string proveedor = (string)data.nombreProveedor;
        string usuario = Convert.ToString(data.usuarioRevisorNombre)?.Trim() ?? string.Empty;
        if (usuario.Length == 0)
        {
            usuario = Convert.ToString(data.usuarioRevisorClave)?.Trim() ?? string.Empty;
        }
        string observacionGeneral = Convert.ToString(data.observacionRevision)?.Trim() ?? string.Empty;
        int totalFisicos = (int)(data.bultosFisicos ?? 0);
        int totalDoc = (int)(data.bultosDocumento ?? 0);

        string colorPrimary = "#003d8c";
        string colorAccent = "#becc00";
        string colorBg = "#f0f4f9";
        string colorWhite = "#ffffff";
        string colorBorder = "#dde3ed";
        string colorTextMuted = "#6b7a99";
        string colorTextDark = "#1a2340";
        string colorRowAlt = "#f7f9fc";

        string bultosColor = (totalFisicos != totalDoc) ? "#c0392b" : colorPrimary;

        StringBuilder sb = new StringBuilder();

        sb.Append("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">");
        sb.Append("<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\">");
        sb.Append("<head>");
        sb.Append("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=UTF-8\" />");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
        sb.Append("<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\" />");
        sb.Append("<title>Reporte de Revision</title>");
        sb.Append("<!--[if mso]>");
        sb.Append("<xml><o:OfficeDocumentSettings><o:AllowPNG/><o:PixelsPerInch>96</o:PixelsPerInch></o:OfficeDocumentSettings></xml>");
        sb.Append("<![endif]-->");
        sb.Append("</head>");

        sb.Append("<body style=\"margin:0; padding:0; background-color:").Append(colorBg).Append("; -webkit-text-size-adjust:100%; -ms-text-size-adjust:100%;\">");
        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" bgcolor=\"").Append(colorBg).Append("\">");
        sb.Append("<tr><td align=\"center\" style=\"padding-top:30px; padding-bottom:30px; padding-left:15px; padding-right:15px;\">");

        sb.Append("<!--[if mso]><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"600\" align=\"center\"><tr><td><![endif]-->");
        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"max-width:600px;\" bgcolor=\"").Append(colorWhite).Append("\">");

        sb.Append("<tr><td bgcolor=\"").Append(colorPrimary).Append("\" style=\"background-color:").Append(colorPrimary).Append("; padding-top:28px; padding-bottom:28px; padding-left:32px; padding-right:32px;\">");
        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\">");
        sb.Append("<tr>");

        sb.Append("<td style=\"vertical-align:middle;\">");
        sb.Append("<p style=\"margin:0; padding:0; font-family:Arial, Helvetica, sans-serif; font-size:22px; font-weight:bold; color:").Append(colorWhite).Append("; line-height:1.2;\">Revision Fisica</p>");
        sb.Append("<p style=\"margin:0; padding:0; padding-top:5px; font-family:Arial, Helvetica, sans-serif; font-size:13px; color:#aec4e8; line-height:1.4;\">PMMovil &middot; Entrada ").Append(WebUtility.HtmlEncode(cref)).Append("</p>");
        sb.Append("</td>");

        sb.Append("<td align=\"right\" style=\"vertical-align:middle;\">");
        sb.Append("<!--[if mso]><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\"><tr><td bgcolor=\"").Append(estado.ColorSolido).Append("\" style=\"padding-top:6px; padding-bottom:6px; padding-left:14px; padding-right:14px;\"><![endif]-->");
        sb.Append("<span style=\"background-color:").Append(estado.ColorSolido).Append("; color:").Append(colorWhite).Append("; font-family:Arial, Helvetica, sans-serif; font-size:11px; font-weight:bold; letter-spacing:1px; padding-top:6px; padding-bottom:6px; padding-left:14px; padding-right:14px; display:inline-block; mso-padding-alt:6px 14px;\">").Append(estado.Icono).Append(" ").Append(estado.Codigo).Append("</span>");
        sb.Append("<!--[if mso]></td></tr></table><![endif]-->");
        sb.Append("</td>");

        sb.Append("</tr></table>");
        sb.Append("</td></tr>");

        sb.Append("<tr><td bgcolor=\"").Append(colorAccent).Append("\" height=\"4\" style=\"background-color:").Append(colorAccent).Append("; height:4px; font-size:0; line-height:0;\">&nbsp;</td></tr>");

        // Banner de estado -- color y mensaje distintos según Finalizada /
        // En proceso / Discrepancia, para que se note de un vistazo sin tener
        // que leer la tabla de facturas.
        sb.Append("<tr><td style=\"padding-top:24px; padding-left:32px; padding-right:32px;\">");
        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"background-color:").Append(estado.ColorFondoTinte).Append("; border-left:4px solid ").Append(estado.ColorSolido).Append(";\">");
        sb.Append("<tr><td style=\"padding-top:14px; padding-bottom:14px; padding-left:16px; padding-right:16px;\">");
        sb.Append("<p style=\"margin:0; padding:0; font-family:Arial, Helvetica, sans-serif; font-size:13.5px; font-weight:bold; color:").Append(estado.ColorTexto).Append(";\">").Append(estado.Icono).Append(" ").Append(WebUtility.HtmlEncode(estado.Etiqueta)).Append("</p>");
        sb.Append("<p style=\"margin:0; padding:0; padding-top:4px; font-family:Arial, Helvetica, sans-serif; font-size:12.5px; color:").Append(colorTextDark).Append("; line-height:1.5;\">").Append(WebUtility.HtmlEncode(estado.Mensaje)).Append("</p>");
        sb.Append("</td></tr></table>");
        sb.Append("</td></tr>");

        sb.Append("<tr><td style=\"padding-top:20px; padding-bottom:32px; padding-left:32px; padding-right:32px;\">");

        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"border-collapse:collapse; margin-bottom:24px;\">");

        // Reunión 1-oct-2026: el tiempo de revisión NO va en el correo (le llega al
        // revisador y a los contactos del cliente); solo los monitores lo ven en el panel.
        string[] infoLabels = { "Referencia", "Cliente", "Proveedor", "Revisador" };
        string[] infoValues =
        {
   WebUtility.HtmlEncode(cref),
   WebUtility.HtmlEncode(cliente),
   WebUtility.HtmlEncode(proveedor),
   WebUtility.HtmlEncode(usuario)
    };
        bool[] infoBold = { true, true, false, false };

        for (int i = 0; i < infoLabels.Length; i++)
        {
            string rowBg = (i % 2 == 0) ? colorWhite : colorRowAlt;
            sb.Append("<tr bgcolor=\"").Append(rowBg).Append("\">");
            sb.Append("<td width=\"38%\" style=\"background-color:").Append(rowBg).Append("; padding-top:11px; padding-bottom:11px; padding-left:14px; padding-right:8px;");
            sb.Append(" font-family:Arial, Helvetica, sans-serif; font-size:13px; color:").Append(colorTextMuted).Append("; border-bottom:1px solid ").Append(colorBorder).Append(";\">");
            sb.Append(infoLabels[i]).Append(":</td>");
            sb.Append("<td style=\"background-color:").Append(rowBg).Append("; padding-top:11px; padding-bottom:11px; padding-left:8px; padding-right:14px;");
            sb.Append(" font-family:Arial, Helvetica, sans-serif; font-size:13px; color:").Append(colorTextDark).Append("; border-bottom:1px solid ").Append(colorBorder);
            if (infoBold[i]) sb.Append("; font-weight:bold;");
            sb.Append(";\">").Append(infoValues[i]).Append("</td>");
            sb.Append("</tr>");
        }

        sb.Append("</table>");

        if (!string.IsNullOrWhiteSpace(observacionGeneral))
        {
            sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"background-color:#fffbeb; border-left: 5px solid #f59e0b; margin-bottom: 30px;\">");
            sb.Append("<tr><td style=\"padding: 20px;\">");
            sb.Append("<h4 style=\"margin:0 0 10px 0; color:#92400e; font-size: 12px; text-transform:uppercase;\">Observaciones del Revisador</h4>");
            sb.Append("<p style=\"margin:0; font-size:14px; line-height:1.5; color:#451a03; white-space:pre-line;\">")
                .Append(WebUtility.HtmlEncode(observacionGeneral))
                .Append("</p>");
            sb.Append("</td></tr></table>");
        }

        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin-bottom:28px;\">");
        sb.Append("<tr>");

        sb.Append("<td width=\"4\" bgcolor=\"").Append(colorAccent).Append("\" style=\"background-color:").Append(colorAccent).Append("; width:4px;\"></td>");
        sb.Append("<td bgcolor=\"").Append(colorRowAlt).Append("\" style=\"background-color:").Append(colorRowAlt).Append("; padding-top:16px; padding-bottom:16px; padding-left:20px; padding-right:20px;\">");
        sb.Append("<p style=\"margin:0; padding:0; font-family:Arial, Helvetica, sans-serif; font-size:11px; font-weight:bold; color:").Append(colorTextMuted).Append("; letter-spacing:1px; text-transform:uppercase;\">Bultos &mdash; F&#237;sicos / Documento</p>");
        sb.Append("<p style=\"margin:0; padding:0; padding-top:6px; font-family:Arial, Helvetica, sans-serif; font-size:28px; font-weight:bold; color:").Append(bultosColor).Append("; line-height:1;\">").Append(totalFisicos).Append(" / ").Append(totalDoc).Append("</p>");
        sb.Append("</td>");
        sb.Append("</tr></table>");

        sb.Append("<p style=\"margin:0; padding:0; padding-bottom:10px; font-family:Arial, Helvetica, sans-serif; font-size:14px; font-weight:bold; color:").Append(colorPrimary).Append("; border-bottom:2px solid ").Append(colorAccent).Append(";\">Detalle de Facturas</p>");

        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"border-collapse:collapse; margin-top:2px;\">");

        string thStyle = "background-color:" + colorPrimary + "; padding-top:10px; padding-bottom:10px; padding-left:12px; padding-right:12px; font-family:Arial, Helvetica, sans-serif; font-size:12px; font-weight:bold; color:" + colorWhite + "; text-align:center;";
        sb.Append("<tr bgcolor=\"").Append(colorPrimary).Append("\">");
        sb.Append("<th align=\"center\" style=\"").Append(thStyle).Append("\">Factura</th>");
        sb.Append("<th align=\"center\" style=\"").Append(thStyle).Append("\">Bultos</th>");
        sb.Append("<th align=\"center\" style=\"").Append(thStyle).Append("\">Partidas</th>");
        sb.Append("<th align=\"center\" style=\"").Append(thStyle).Append("\">Estatus</th>");
        sb.Append("</tr>");

        bool alt = false;
        foreach (var f in facturas)
        {
            int status = (int)(f.status_rev ?? 0);
            string sTxt = status == 1 ? "OK" : (status == 2 ? "DISCR." : "PEND.");
            string sCol = status == 1 ? "#3a7d00" : (status == 2 ? "#c0392b" : "#6b7a99");
            string rowBg = alt ? colorRowAlt : colorWhite;
            alt = !alt;
            string factura = Convert.ToString(f.cnofact)?.Trim() ?? string.Empty;

            string tdStyle = "background-color:" + rowBg + "; padding-top:10px; padding-bottom:10px; padding-left:12px; padding-right:12px; font-family:Arial, Helvetica, sans-serif; font-size:13px; color:" + colorTextDark + "; border-bottom:1px solid " + colorBorder + "; text-align:center;";

            sb.Append("<tr bgcolor=\"").Append(rowBg).Append("\">");
            sb.Append("<td align=\"center\" style=\"").Append(tdStyle).Append("\">").Append(WebUtility.HtmlEncode(factura)).Append("</td>");
            sb.Append("<td align=\"center\" style=\"").Append(tdStyle).Append("\">").Append((int)f.totbultos).Append(" / ").Append((int)f.totbultosdoc).Append("</td>");
            sb.Append("<td align=\"center\" style=\"").Append(tdStyle).Append("\">").Append((int)f.partidasRevisadas).Append(" / ").Append((int)f.totalPartidas).Append("</td>");
            sb.Append("<td align=\"center\" style=\"").Append(tdStyle).Append(" font-weight:bold; color:").Append(sCol).Append(";\">").Append(sTxt).Append("</td>");
            sb.Append("</tr>");
        }

        sb.Append("</table>");

        AppendSeccionObservacionesFacturas(sb, facturas, colorPrimary, colorAccent, colorBorder, colorTextMuted, colorTextDark, colorRowAlt);

        AppendSeccionEvidencia(sb, fotos, galeriaUrl, colorPrimary, colorAccent, colorWhite, colorTextMuted, colorTextDark, colorBorder, colorRowAlt);

        sb.Append("</td></tr>");

        sb.Append("<tr><td bgcolor=\"").Append(colorPrimary).Append("\" style=\"background-color:").Append(colorPrimary).Append("; padding-top:18px; padding-bottom:18px; padding-left:32px; padding-right:32px; text-align:center;\">");
        sb.Append("<p style=\"margin:0; padding:0; font-family:Arial, Helvetica, sans-serif; font-size:11px; color:#aec4e8;\">Notificaci&#243;n autom&#225;tica de Grupo PMM &middot; 2026</p>");
        sb.Append("</td></tr>");

        sb.Append("</table>");
        sb.Append("<!--[if mso]></td></tr></table><![endif]-->");
        sb.Append("</td></tr></table>");
        sb.Append("</body></html>");

        return sb.ToString();
    }

    /// <summary>
    /// Incluye las observaciones que pertenecen a cada factura. La observación
    /// general se muestra por separado dentro del resumen de la revisión; aquí
    /// se conservan las dos notas que pueden variar entre facturas: la normal
    /// (fact.obsrev) y la discrepancia (fact.discrepancias).
    /// </summary>
    private static void AppendSeccionObservacionesFacturas(
        StringBuilder sb,
        IEnumerable<dynamic> facturas,
        string colorPrimary,
        string colorAccent,
        string colorBorder,
        string colorTextMuted,
        string colorTextDark,
        string colorRowAlt)
    {
        List<dynamic> facturasConObservaciones = facturas
            .Where(f =>
                !string.IsNullOrWhiteSpace(Convert.ToString(f.observacionNormal)) ||
                !string.IsNullOrWhiteSpace(Convert.ToString(f.discrepancias)))
            .ToList();

        if (facturasConObservaciones.Count == 0)
        {
            return;
        }

        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"border-collapse:collapse; margin-top:28px; margin-bottom:28px;\">");
        sb.Append("<tr><td style=\"padding-bottom:10px; border-bottom:2px solid ").Append(colorAccent).Append(";\">");
        sb.Append("<p style=\"margin:0; padding:0; font-family:Arial, Helvetica, sans-serif; font-size:14px; font-weight:bold; color:").Append(colorPrimary).Append(";\">Observaciones por factura</p>");
        sb.Append("</td></tr>");

        foreach (dynamic factura in facturasConObservaciones)
        {
            string numeroFactura = Convert.ToString(factura.cnofact)?.Trim() ?? string.Empty;
            string observacionNormal = Convert.ToString(factura.observacionNormal)?.Trim() ?? string.Empty;
            string discrepancia = Convert.ToString(factura.discrepancias)?.Trim() ?? string.Empty;

            sb.Append("<tr><td bgcolor=\"").Append(colorRowAlt).Append("\" style=\"background-color:").Append(colorRowAlt).Append("; border-bottom:1px solid ").Append(colorBorder).Append("; padding-top:16px; padding-bottom:16px; padding-left:16px; padding-right:16px;\">");
            sb.Append("<p style=\"margin:0 0 12px 0; font-family:Arial, Helvetica, sans-serif; font-size:13px; font-weight:bold; color:").Append(colorPrimary).Append(";\">Factura ").Append(WebUtility.HtmlEncode(numeroFactura)).Append("</p>");

            if (!string.IsNullOrWhiteSpace(observacionNormal))
            {
                sb.Append("<p style=\"margin:0 0 6px 0; font-family:Arial, Helvetica, sans-serif; font-size:11px; font-weight:bold; color:").Append(colorTextMuted).Append("; text-transform:uppercase; letter-spacing:.4px;\">Observación normal</p>");
                sb.Append("<p style=\"margin:0 0 14px 0; font-family:Arial, Helvetica, sans-serif; font-size:13px; line-height:1.5; color:").Append(colorTextDark).Append("; white-space:pre-line;\">")
                    .Append(WebUtility.HtmlEncode(observacionNormal))
                    .Append("</p>");
            }

            if (!string.IsNullOrWhiteSpace(discrepancia))
            {
                sb.Append("<p style=\"margin:0 0 6px 0; font-family:Arial, Helvetica, sans-serif; font-size:11px; font-weight:bold; color:#b42318; text-transform:uppercase; letter-spacing:.4px;\">Discrepancia</p>");
                sb.Append("<p style=\"margin:0; font-family:Arial, Helvetica, sans-serif; font-size:13px; line-height:1.5; color:#7f1d1d; white-space:pre-line;\">")
                    .Append(WebUtility.HtmlEncode(discrepancia))
                    .Append("</p>");
            }

            sb.Append("</td></tr>");
        }

        sb.Append("</table>");
    }

    /// <summary>Sección "Evidencia fotográfica" del correo: solo cuenta y enlaza -- ya
    /// no se adjuntan fotos al mensaje (con revisiones de 20+ fotos se pasaba fácil el
    /// tope de Outlook/O365). Toda la evidencia vive en la galería pública, que tiene su
    /// propio visor tipo carrusel y descarga individual o en ZIP.
    ///
    /// La galería se muestra aunque el conteo inicial sea cero. Las fotos de Android
    /// pueden terminar de sincronizarse después del cierre y la página pública consulta
    /// la información en vivo.</summary>
    private static void AppendSeccionEvidencia(
        StringBuilder sb,
        ConteoFotosResultado fotos,
        string? galeriaUrl,
        string colorPrimary,
        string colorAccent,
        string colorWhite,
        string colorTextMuted,
        string colorTextDark,
        string colorBorder,
        string colorRowAlt)
    {
        int totalFotos = fotos.TotalNormales + fotos.TotalDiscrepancia;
        if (totalFotos == 0 && string.IsNullOrWhiteSpace(galeriaUrl))
        {
            return;
        }

        sb.Append("<p style=\"margin:0; padding:0; padding-top:28px; padding-bottom:10px; font-family:Arial, Helvetica, sans-serif; font-size:14px; font-weight:bold; color:").Append(colorPrimary).Append("; border-bottom:2px solid ").Append(colorAccent).Append(";\">Evidencia fotogr&#225;fica</p>");

        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin-top:2px;\">");
        sb.Append("<tr>");
        sb.Append("<td width=\"50%\" style=\"padding-top:12px; padding-bottom:12px; padding-right:8px;\">");
        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" bgcolor=\"").Append(colorRowAlt).Append("\" style=\"background-color:").Append(colorRowAlt).Append(";\"><tr><td style=\"padding:14px 16px;\">");
        sb.Append("<p style=\"margin:0; font-family:Arial, Helvetica, sans-serif; font-size:11px; font-weight:bold; color:").Append(colorTextMuted).Append("; letter-spacing:1px; text-transform:uppercase;\">Evidencia normal</p>");
        sb.Append("<p style=\"margin:0; padding-top:4px; font-family:Arial, Helvetica, sans-serif; font-size:22px; font-weight:bold; color:").Append(colorPrimary).Append(";\">").Append(fotos.TotalNormales).Append("</p>");
        sb.Append("</td></tr></table></td>");
        sb.Append("<td width=\"50%\" style=\"padding-top:12px; padding-bottom:12px; padding-left:8px;\">");
        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" bgcolor=\"").Append(colorRowAlt).Append("\" style=\"background-color:").Append(colorRowAlt).Append(";\"><tr><td style=\"padding:14px 16px;\">");
        sb.Append("<p style=\"margin:0; font-family:Arial, Helvetica, sans-serif; font-size:11px; font-weight:bold; color:").Append(colorTextMuted).Append("; letter-spacing:1px; text-transform:uppercase;\">Discrepancia</p>");
        sb.Append("<p style=\"margin:0; padding-top:4px; font-family:Arial, Helvetica, sans-serif; font-size:22px; font-weight:bold; color:").Append(fotos.TotalDiscrepancia > 0 ? "#c0392b" : colorPrimary).Append(";\">").Append(fotos.TotalDiscrepancia).Append("</p>");
        sb.Append("</td></tr></table></td>");
        sb.Append("</tr></table>");

        if (!string.IsNullOrWhiteSpace(galeriaUrl))
        {
            string mensajeGaleria = totalFotos == 0
                ? "Todavía no se habían registrado fotos al generar este aviso. Si se tomaron fotografías, aparecerán en esta galería al terminar la sincronización."
                : "Toda la evidencia -- incluida la de discrepancia -- vive en la galería pública, con su propio visor y descarga individual o en ZIP.";
            sb.Append("<p style=\"margin:0; padding-top:6px; padding-bottom:14px; font-family:Arial, Helvetica, sans-serif; font-size:12px; color:")
                .Append(colorTextMuted)
                .Append(";\">")
                .Append(WebUtility.HtmlEncode(mensajeGaleria))
                .Append("</p>");

            sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\">");
            sb.Append("<tr><td bgcolor=\"").Append(colorPrimary).Append("\" style=\"background-color:").Append(colorPrimary).Append("; border-radius:6px;\">");
            sb.Append("<a href=\"").Append(galeriaUrl).Append("\" target=\"_blank\" style=\"display:inline-block; padding:11px 20px; font-family:Arial, Helvetica, sans-serif; font-size:13px; font-weight:bold; color:").Append(colorWhite).Append("; text-decoration:none;\">Ver y descargar las fotos</a>");
            sb.Append("</td></tr></table>");
        }
    }

    private sealed record EstadoGeneralRevision(
        string Codigo,
        string Etiqueta,
        string Mensaje,
        string ColorSolido,
        string ColorTexto,
        string ColorFondoTinte,
        string Icono);

    /// <summary>Discrepancia manda sobre todo lo demás; si nada tiene discrepancia pero
    /// no todas las facturas del grupo están completas, es "en proceso"; solo si TODAS
    /// quedaron con status_rev=1 se considera Finalizada.</summary>
    private static EstadoGeneralRevision CalcularEstadoGeneral(IEnumerable<dynamic> facturas)
    {
        bool algunaDiscrepancia = false;
        bool todasCompletas = true;

        foreach (dynamic f in facturas)
        {
            int status = (int)(f.status_rev ?? 0);
            if (status == 2) algunaDiscrepancia = true;
            if (status != 1) todasCompletas = false;
        }

        if (algunaDiscrepancia)
        {
            return new EstadoGeneralRevision(
                "DISCREPANCIA",
                "Discrepancia detectada",
                "Se detectó una discrepancia durante la revisión física de esta mercancía y requiere atención.",
                "#c0392b", "#c0392b", "#fdecea", "⚠");
        }

        if (todasCompletas)
        {
            return new EstadoGeneralRevision(
                "FINALIZADA",
                "Revisión finalizada",
                "La revisión física de esta mercancía concluyó sin discrepancias.",
                "#1e8449", "#1e8449", "#eafaf1", "✓");
        }

        return new EstadoGeneralRevision(
            "EN PROCESO",
            "En proceso",
            "Todavía hay facturas de este embarque pendientes por completar su revisión física.",
            "#b7791f", "#b7791f", "#fff8e1", "⏳");
    }

    private sealed class ConteoFotosResultado
    {
        public int TotalNormales { get; set; }
        public int TotalDiscrepancia { get; set; }
    }

    private sealed class ConteoPorTipo
    {
        public int NTipoDocto { get; set; }
        public int Total { get; set; }
    }

    /// <summary>Solo cuenta cuántas fotos hay de cada tipo -- ya no se leen ni se
    /// comprimen archivos aquí, el correo nunca adjunta fotos (ver AppendSeccionEvidencia).</summary>
    private static async Task<ConteoFotosResultado> ContarFotosAsync(IDbConnection conn, string cref)
    {
        var resultado = new ConteoFotosResultado();

        List<ConteoPorTipo> conteos = (await conn.QueryAsync<ConteoPorTipo>(
            @"SELECT ntipodocto AS NTipoDocto, COUNT(*) AS Total
              FROM fotos
              WHERE creferencia = @cref AND ntipodocto IN (107, 103)
              GROUP BY ntipodocto",
            new { cref })).ToList();

        foreach (ConteoPorTipo fila in conteos)
        {
            if (fila.NTipoDocto == 107) resultado.TotalNormales = fila.Total;
            else if (fila.NTipoDocto == 103) resultado.TotalDiscrepancia = fila.Total;
        }

        return resultado;
    }

    private (string Asunto, string CuerpoHtml) BuildBarcodeHtml(dynamic s, IEnumerable<dynamic> productos)
    {
        string cref = (string)s.crefAgrupada;
        string cliente = (string)s.nombreCliente;
        string proveedor = (string)s.nombreProveedor;
        string usuarioClave = (string)s.usuario;
        string facturas = "N/A";
        int total = (int)s.totalLecturas;
        int ok = (int)s.totalExistentes;
        int bad = (int)s.totalInexistentes;
        dynamic inicio = s.fechaInicio;
        string obs = (string)s.observaciones;

        string asunto = $"Revision de mercancía - {cref} - {cliente}";
        StringBuilder sb = new StringBuilder();

        sb.Append("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">");
        sb.Append("<html xmlns=\"http://www.w3.org/1999/xhtml\">");
        sb.Append("<head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=UTF-8\" /><title>Reporte</title></head>");
        sb.Append("<body style=\"margin:0; padding:0; font-family: Arial, sans-serif; background-color: #f1f5f9;\">");

        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" bgcolor=\"#f1f5f9\"><tr><td align=\"center\" style=\"padding: 20px 0;\">");

        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"720\" style=\"background-color: #ffffff; border: 1px solid #e2e8f0;\">");

        sb.Append("<tr><td height=\"8\" bgcolor=\"#becc00\" style=\"font-size:1px; line-height:1px;\">&nbsp;</td></tr>");

        sb.Append("<tr><td bgcolor=\"#003d8c\" style=\"padding: 30px;\">");
        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\"><tr>");
        sb.Append("<td><h2 style=\"margin:0; font-size: 22px; color: #ffffff; font-weight:bold;\">Revisión por códigos de barras</h2>");
        sb.Append("<p style=\"margin:8px 0 0 0; font-size:14px; color: #cbd5e1;\">Entrada <strong>").Append(WebUtility.HtmlEncode(cref)).Append("</strong> · ").Append(WebUtility.HtmlEncode(cliente)).Append("</p></td>");

        sb.Append("<td align=\"right\" width=\"140\">");
        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\"><tr><td bgcolor=\"#becc00\" style=\"padding: 8px 16px; color: #003d8c; font-weight: bold; font-size: 12px; text-transform: uppercase;\">FINALIZADA ✓</td></tr></table>");
        sb.Append("</td></tr></table></td></tr>");

        sb.Append("<tr><td style=\"padding: 30px;\">");

        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"font-size: 14px;\">");

        string[] labels = { "Entrada", "Cliente", "Factura(s)", "Total escaneado", "Revisador", "Fecha y hora", "Proveedor", "En catálogo", "Fuera de catálogo" };
        object[] values = { cref, cliente, facturas, total, usuarioClave, inicio, proveedor, ok, bad };

        for (int i = 0; i < labels.Length; i++)
        {
            string borderStyle = "border-bottom: 1px solid #f1f5f9;";
            string textColor = "#000000";
            string weight = "600";

            if (labels[i] == "En catálogo") textColor = "#8a9400";
            if (labels[i] == "Fuera de catálogo") { textColor = "#dc2626"; borderStyle = ""; }
            if (labels[i] == "Total escaneado") { textColor = "#003d8c"; }

            sb.Append("<tr>");
            sb.Append("<td width=\"180\" style=\"padding: 10px 0; color:#64748b; ").Append(borderStyle).Append("\"><strong>").Append(labels[i]).Append("</strong></td>");
            sb.Append("<td style=\"padding: 10px 0; font-weight:").Append(weight).Append("; color:").Append(textColor).Append("; ").Append(borderStyle).Append("\">");

            if (labels[i] == "En catálogo") sb.Append("✓ ");
            if (labels[i] == "Fuera de catálogo") sb.Append("✕ ");

            sb.Append(WebUtility.HtmlEncode(values[i]?.ToString() ?? "")).Append("</td></tr>");
        }
        sb.Append("</table><br/><br/>");

        if (!string.IsNullOrWhiteSpace(obs))
        {
            sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"background-color:#fffbeb; border-left: 5px solid #f59e0b; margin-bottom: 30px;\">");
            sb.Append("<tr><td style=\"padding: 20px;\">");
            sb.Append("<h4 style=\"margin:0 0 10px 0; color:#92400e; font-size: 12px; text-transform:uppercase;\">Observaciones del Revisador</h4>");
            sb.Append("<p style=\"margin:0; font-size:14px; line-height:1.5; color:#451a03;\">").Append(WebUtility.HtmlEncode(obs)).Append("</p>");
            sb.Append("</td></tr></table>");
        }

        sb.Append("<h3 style=\"color:#003d8c; margin-bottom:15px; font-size:16px; border-bottom:2px solid #becc00;\">Desglose de productos</h3>");
        sb.Append("<table border=\"0\" cellpadding=\"12\" cellspacing=\"0\" width=\"100%\" style=\"border:1px solid #e2e8f0; font-size:13px;\">");
        sb.Append("<tr bgcolor=\"#003d8c\">");
        sb.Append("<th align=\"left\" style=\"color:#ffffff;\">Producto</th>");
        sb.Append("<th align=\"left\" style=\"color:#ffffff;\">Descripción</th>");
        sb.Append("<th align=\"center\" style=\"color:#ffffff;\">Cant.</th>");
        sb.Append("<th align=\"center\" style=\"color:#ffffff;\">Existe</th></tr>");

        int rowIdx = 0;
        foreach (dynamic p in productos)
        {
            string rowBg = (rowIdx++ % 2 == 0) ? "#ffffff" : "#fbfcfd";
            bool existeBool = Convert.ToInt32(p.ExisteInt) == 1;

            sb.Append("<tr bgcolor=\"").Append(rowBg).Append("\">");
            sb.Append("<td style=\"border-bottom:1px solid #f1f5f9; font-weight:bold; color:#003d8c;\">").Append(WebUtility.HtmlEncode((string)p.Cidproduct)).Append("</td>");
            sb.Append("<td style=\"border-bottom:1px solid #f1f5f9; color:#475569;\">").Append(WebUtility.HtmlEncode(((string)p.Descripcion) ?? "")).Append("</td>");
            sb.Append("<td align=\"center\" style=\"border-bottom:1px solid #f1f5f9; font-weight:bold;\">").Append((int)p.Cantidad).Append("</td>");
            sb.Append("<td align=\"center\" style=\"border-bottom:1px solid #f1f5f9;\">");

            if (existeBool) sb.Append("<span style=\"color:#8a9400; font-weight:bold;\">SÍ</span>");
            else sb.Append("<span style=\"color:#dc2626; font-weight:bold;\">NO</span>");

            sb.Append("</td></tr>");
        }
        sb.Append("</table>");

        sb.Append("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin-top:40px; border-top:1px solid #e2e8f0;\">");
        sb.Append("<tr><td align=\"center\" style=\"padding-top:20px;\">");
        sb.Append("<p style=\"font-size:11px; color:#94a3b8; margin:0; line-height:1.5;\">Notificación generada automáticamente por Grupo PMM.</p>");
        sb.Append("<p style=\"font-size:11px; color:#94a3b8; margin:5px 0 0 0; font-weight:bold;\">© 2026 Grupo PMM · Aduanas y logística</p>");
        sb.Append("</td></tr></table>");

        sb.Append("</td></tr></table>"); sb.Append("</td></tr></table>"); sb.Append("</body></html>");

        return (Asunto: asunto, CuerpoHtml: sb.ToString());
    }

    private string GetUnifiedFooter(string tipo)
    {
        return $@"<div style=""margin-top:40px;padding-top:20px;border-top:1px solid #e2e8f0;text-align:center;"">
  <p style=""font-size:11px;color:#94a3b8;margin:0;line-height:1.5;"">Notificacion generada automaticamente por el proceso de Revision {tipo} de Grupo PMM, tras el cierre de la revision fisica.</p>
  <p style=""font-size:11px;color:#94a3b8;margin:5px 0 0 0;font-weight:bold;"">© 2026 Grupo PMM · Aduanas y logistica</p>
   </div>";
    }
}
