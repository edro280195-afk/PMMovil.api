using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Text;

using Dapper;

using PMMovil.Api.Data;

namespace PMMovil.Api.Services;

/// <summary>Un renglón de la mercancía recibida (trafdet).</summary>
public sealed record EntradaAvisoDetalle(decimal Cantidad, string Clase, string Descripcion, string Atados, string Ubicacion);

/// <summary>Lo capturado en la entrada, ya listo para pintarse en el correo de prueba.</summary>
public sealed class EntradaAvisoDatos
{
    public string Referencia { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string Transportista { get; set; } = string.Empty;
    public string Caja { get; set; } = string.Empty;
    public string Guia { get; set; } = string.Empty;
    public string Manejo { get; set; } = string.Empty;
    public string Planta { get; set; } = string.Empty;
    public string Bodega { get; set; } = string.Empty;
    public string Recibio { get; set; } = string.Empty;
    public string Observaciones { get; set; } = string.Empty;
    public DateTime? FechaArribo { get; set; }
    public decimal PesoLbs { get; set; }
    public decimal PesoKgs { get; set; }
    public decimal Bultos { get; set; }
    public bool Hazmat { get; set; }
    public string ClaseHazmat { get; set; } = string.Empty;
    public string UnHazmat { get; set; } = string.Empty;
    public bool Danada { get; set; }
    public string DescripcionDanio { get; set; } = string.Empty;
    public bool InBond { get; set; }
    public string InBondNumero { get; set; } = string.Empty;
    public int MaderaConSello { get; set; }
    public int MaderaSinSello { get; set; }
    public int Aserrin { get; set; }
    public int NoRequiereSello { get; set; }
    public int FotosEvidencia { get; set; }
    public int FotosDanio { get; set; }
    public List<EntradaAvisoDetalle> Detalles { get; set; } = new();

    public decimal TotalPiezas => Detalles.Sum(d => d.Cantidad);
}

/// <summary>
/// Aviso de PRUEBA de la entrada de bodega (6-nov-2026). PMMovil NO manda la notificación de entrada al cliente: la manda
/// Gladys desde el sistema PMM (Visual FoxPro), que es quien complementa la información de la entrada. Esto es solo para
/// que, al terminar la captura en el teléfono, llegue UN correo a desarrollo3 y se pueda ver (1) cómo quedó la captura y
/// (2) cómo se vería la notificación.
///
/// Garantía de «solo me llegue a mí»: el destinatario sale de <see cref="DestinatarioDePrueba"/> (una sola dirección
/// @grupopmm.com, por omisión desarrollo3) y NUNCA de la tabla de correos del cliente; el envío no lleva copias, ni copias
/// ocultas, y desactiva las copias ocultas automáticas de <see cref="SmtpService"/> (que en producción agregan a
/// desarrollo@ y carmen.barragan@). Se apaga con <c>EntradaNotificacion:Prueba:Activa=false</c>.
/// </summary>
public static class EntradaAvisoCaptura
{
    public const string DestinatarioPorOmision = "desarrollo3@grupopmm.com";
    private const string Dominio = "@grupopmm.com";
    private static readonly TimeSpan VentanaAntiRepeticion = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, DateTime> Recientes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// La única dirección a la que se manda el aviso. Si la configuración trae algo que no sea UNA dirección @grupopmm.com
    /// (varias separadas por coma, un correo externo, vacío) se ignora y se usa desarrollo3: este correo nunca debe poder
    /// salir a un cliente por un error de configuración.
    /// </summary>
    public static string DestinatarioDePrueba(string? configurado)
    {
        string candidato = (configurado ?? string.Empty).Trim();
        bool valido = candidato.Length > Dominio.Length
            && candidato.EndsWith(Dominio, StringComparison.OrdinalIgnoreCase)
            && candidato.Count(c => c == '@') == 1
            && candidato.IndexOfAny(new[] { ',', ';', ' ', '<', '>', '"', '\t' }) < 0;
        return valido ? candidato : DestinatarioPorOmision;
    }

    /// <summary>
    /// Manda el correo SOLO a <paramref name="destino"/>: sin Cc, sin Bcc y sin las copias ocultas automáticas. Pide la cola
    /// durable (el mensaje queda en disco y sale en segundos aunque el SMTP tarde).
    /// </summary>
    public static Task<bool> EnviarSoloAAsync(ISmtpService smtp, string destino, string asunto, string html)
        => smtp.EnviarAsync(
            destinatarios: new[] { destino },
            asunto: asunto,
            cuerpoHtml: html,
            adjuntos: null,
            copias: null,
            copiasOcultas: null,
            incluirCopiasOcultasAutomaticas: false,
            ct: CancellationToken.None,
            preferirCola: true);

    /// <summary>
    /// ¿Ya se mandó este aviso hace poco? Evita duplicados por un doble toque o por el reintento del teléfono cuando la
    /// respuesta se perdió. Devuelve true si es repetido; si no, lo anota.
    /// </summary>
    public static bool YaEnviadoReciente(string cref, bool esEdicion, DateTime ahora)
    {
        string clave = $"{cref.Trim()}|{(esEdicion ? "edicion" : "nueva")}";
        foreach (var viejo in Recientes.Where(p => ahora - p.Value > VentanaAntiRepeticion).Select(p => p.Key).ToList())
        {
            Recientes.TryRemove(viejo, out _);
        }

        if (Recientes.TryGetValue(clave, out DateTime cuando) && ahora - cuando <= VentanaAntiRepeticion)
        {
            return true;
        }

        Recientes[clave] = ahora;
        return false;
    }

    /// <summary>Solo para pruebas: olvida lo enviado.</summary>
    public static void OlvidarEnviados() => Recientes.Clear();

    private static string E(string? valor) => WebUtility.HtmlEncode(valor ?? string.Empty);

    public static (string Asunto, string Html) Construir(
        EntradaAvisoDatos d,
        string destino,
        string? urlSeguimiento,
        DateTime? expiraEnlace,
        bool esEdicion,
        DateTime ahora)
    {
        string cliente = string.IsNullOrWhiteSpace(d.Cliente) ? "Sin cliente" : d.Cliente.Trim();
        string asunto = $"[PRUEBA] [{d.Referencia}] Recepción en Bodega{(esEdicion ? " (actualizada)" : "")} - {cliente}";

        const string celdaEt = "padding:6px;border:1px solid #ddd;font-weight:bold;background:#f5f5f5;width:34%;vertical-align:top";
        const string celdaVal = "padding:6px;border:1px solid #ddd;vertical-align:top";

        string Fila(string etiqueta, string? valor)
            => string.IsNullOrWhiteSpace(valor)
                ? string.Empty
                : $"<tr><td style='{celdaEt}'>{E(etiqueta)}</td><td style='{celdaVal}'>{E(valor.Trim())}</td></tr>";

        string Num(decimal n) => n.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        // ── Parte 1: la notificación tal como se vería (misma plantilla «Recepción en Bodega» de siempre) ──
        var detalles = new StringBuilder();
        foreach (EntradaAvisoDetalle det in d.Detalles)
        {
            detalles.Append($@"<tr>
<td style='padding:6px;border:1px solid #ddd;text-align:center'>{E(Num(det.Cantidad))}</td>
<td style='padding:6px;border:1px solid #ddd'>{E(det.Descripcion)}</td>
<td style='padding:6px;border:1px solid #ddd;text-align:center'>{E(det.Ubicacion)}</td></tr>");
        }

        // Planta y bodega se capturan como códigos (no hay catálogo de nombres): se muestran tal cual, sin inventar un nombre.
        string bodega = string.Join(" · ", new[]
        {
            string.IsNullOrWhiteSpace(d.Planta) ? null : $"Planta {d.Planta.Trim()}",
            string.IsNullOrWhiteSpace(d.Bodega) ? null : $"Bodega {d.Bodega.Trim()}"
        }.Where(x => x != null));
        string fecha = (d.FechaArribo ?? ahora).ToString("dd/MM/yyyy HH:mm");

        string notificacion = $@"
<h2 style='color:#003d8c;margin:0 0 6px'>Recepción en Bodega</h2>
<p style='margin:0 0 10px'>Se ha registrado la recepción de la siguiente mercancía:</p>
<table style='border-collapse:collapse;width:100%'>
{Fila("Referencia", d.Referencia)}{Fila("Cliente", cliente)}{Fila("Bodega", bodega)}{Fila("Fecha", fecha)}{Fila("Total Piezas", Num(d.TotalPiezas))}
</table>
<h3 style='color:#003d8c;margin:18px 0 6px'>Detalle de Mercancía</h3>
<table style='border-collapse:collapse;width:100%'>
<tr style='background:#003d8c;color:#fff;'>
<th style='padding:8px;border:1px solid #003d8c'>Cant</th>
<th style='padding:8px;border:1px solid #003d8c'>Descripción</th>
<th style='padding:8px;border:1px solid #003d8c'>Ubicación</th></tr>
{(detalles.Length > 0 ? detalles.ToString() : "<tr><td colspan='3' style='padding:8px;border:1px solid #ddd;color:#888;text-align:center'>Sin renglones de mercancía</td></tr>")}
</table>";

        // ── Parte 2: cómo quedó la captura hecha en el teléfono ──
        var madera = new List<string>();
        if (d.MaderaConSello > 0) madera.Add($"Madera con sello: {d.MaderaConSello}");
        if (d.MaderaSinSello > 0) madera.Add($"Madera sin sello: {d.MaderaSinSello}");
        if (d.Aserrin > 0) madera.Add($"Aserrín: {d.Aserrin}");
        if (d.NoRequiereSello > 0) madera.Add($"No requiere sello: {d.NoRequiereSello}");

        string peso = d.PesoLbs > 0 || d.PesoKgs > 0 ? $"{Num(d.PesoLbs)} lb · {Num(d.PesoKgs)} kg" : string.Empty;
        string hazmat = d.Hazmat
            ? $"Sí · clase {(string.IsNullOrWhiteSpace(d.ClaseHazmat) ? "—" : d.ClaseHazmat.Trim())}{(string.IsNullOrWhiteSpace(d.UnHazmat) ? "" : $" · UN {d.UnHazmat.Trim()}")}"
            : "No";
        string danio = d.Danada
            ? $"SÍ{(string.IsNullOrWhiteSpace(d.DescripcionDanio) ? "" : $" — {d.DescripcionDanio.Trim()}")}"
            : "No";
        string inbond = d.InBond ? $"Sí{(string.IsNullOrWhiteSpace(d.InBondNumero) ? "" : $" · {d.InBondNumero.Trim()}")}" : "No";
        string fotos = $"{d.FotosEvidencia} de evidencia" + (d.Danada ? $" · {d.FotosDanio} del daño" : "");

        string botonFotos = string.IsNullOrWhiteSpace(urlSeguimiento)
            ? "<p style='color:#888;margin:10px 0 0'>No se pudo generar el enlace de las fotos.</p>"
            : $@"<p style='margin:14px 0 4px'><a href='{E(urlSeguimiento)}' style='display:inline-block;background:#0b3b7a;color:#ffffff;text-decoration:none;padding:11px 16px;border-radius:7px;font-weight:bold'>Ver fotos y seguimiento de la entrada</a></p>
<p style='font-size:12px;color:#666;margin:0'>{(expiraEnlace.HasValue ? $"Enlace vigente hasta {E(expiraEnlace.Value.ToString("dd/MM/yyyy HH:mm"))}. " : "")}Las fotos se completan solas si todavía se están subiendo.</p>";

        string captura = $@"
<h2 style='color:#003d8c;margin:0 0 6px'>Datos capturados en el teléfono</h2>
<table style='border-collapse:collapse;width:100%'>
{Fila("Transporte", d.Transportista)}{Fila("Caja de arribo", d.Caja)}{Fila("Guía / B.L.", d.Guia)}{Fila("Tipo de manejo", d.Manejo)}{Fila("Peso total", peso)}{Fila("HAZMAT", hazmat)}{Fila("Mercancía dañada", danio)}{Fila("IN-BOND", inbond)}{Fila("Control de madera", string.Join(" · ", madera))}{Fila("Observaciones", d.Observaciones)}{Fila("Recibió", d.Recibio)}{Fila("Fotos", fotos)}
</table>
{botonFotos}";

        _ = destino;
        string html = $@"<html><body style='font-family:Arial,sans-serif;color:#333;'>
<div style='max-width:660px;margin:0 auto;'>
<div style='font-size:11px;letter-spacing:.08em;color:#667089;margin:0 0 4px'>1 · ASÍ SE VERÍA LA NOTIFICACIÓN</div>
<div style='border:1px solid #e1e6f0;border-radius:8px;padding:16px;margin-bottom:22px'>{notificacion}
<p style='margin:18px 0 0;font-size:0.85em;color:#666;'>Este correo se genera automáticamente al registrar la recepción en bodega.</p></div>
<div style='font-size:11px;letter-spacing:.08em;color:#667089;margin:0 0 4px'>2 · CÓMO QUEDÓ LA CAPTURA</div>
<div style='border:1px solid #e1e6f0;border-radius:8px;padding:16px'>{captura}</div>
</div></body></html>";

        return (asunto, html);
    }
}

/// <summary>Arma y manda el aviso de prueba de una entrada (ver <see cref="EntradaAvisoCaptura"/>).</summary>
public sealed class EntradaAvisoCapturaService
{
    private readonly BaseRepository _db;
    private readonly IEntradaShareService _share;
    private readonly ISmtpService _smtp;
    private readonly IConfiguration _config;
    private readonly ILogger<EntradaAvisoCapturaService> _logger;

    public EntradaAvisoCapturaService(
        BaseRepository db,
        IEntradaShareService share,
        ISmtpService smtp,
        IConfiguration config,
        ILogger<EntradaAvisoCapturaService> logger)
    {
        _db = db;
        _share = share;
        _smtp = smtp;
        _config = config;
        _logger = logger;
    }

    public async Task<(bool Ok, string Mensaje)> EnviarAsync(string cref, string usuario, bool esEdicion, string baseUrl)
    {
        if (!_config.GetValue("EntradaNotificacion:Prueba:Activa", true))
        {
            return (false, "El aviso de prueba de entrada está desactivado.");
        }

        if (string.IsNullOrWhiteSpace(cref))
        {
            return (false, "Referencia inválida.");
        }

        EntradaAvisoDatos? datos = await CargarAsync(cref.Trim());
        if (datos == null)
        {
            return (false, "Referencia no encontrada.");
        }

        if (EntradaAvisoCaptura.YaEnviadoReciente(datos.Referencia, esEdicion, DateTime.Now))
        {
            return (true, "El aviso ya se mandó hace unos minutos; no se repite.");
        }

        // El enlace de las fotos es un extra: si falla, el aviso sale igual, sin el botón.
        string? url = null;
        DateTime? expira = null;
        try
        {
            (string Token, DateTime Expira)? enlace = await _share.GenerarLinkAsync(datos.Referencia, usuario);
            if (enlace != null)
            {
                url = $"{baseUrl.TrimEnd('/')}/api/publico/entrada/{enlace.Value.Token}";
                expira = enlace.Value.Expira;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aviso de entrada {Cref}: no se pudo generar el enlace de las fotos; el aviso sale sin él.", datos.Referencia);
        }

        string destino = EntradaAvisoCaptura.DestinatarioDePrueba(_config["EntradaNotificacion:Prueba:Destinatario"]);
        (string asunto, string html) = EntradaAvisoCaptura.Construir(datos, destino, url, expira, esEdicion, DateTime.Now);
        bool enviado = await EntradaAvisoCaptura.EnviarSoloAAsync(_smtp, destino, asunto, html);

        _logger.LogInformation(
            "Aviso de prueba de entrada {Cref} ({Tipo}) -> {Destino}: {Resultado}.",
            datos.Referencia, esEdicion ? "edición" : "nueva", destino, enviado ? "enviado/en cola" : "no salió ahora (quedó en la cola de reintentos si el SMTP falló)");

        // Aunque no haya salido AHORA, el mensaje ya quedó en la cola durable: el teléfono no debe reintentar (duplicaría).
        return (true, enviado ? $"Aviso de prueba enviado a {destino}." : $"El aviso de prueba para {destino} quedó en la cola de correo.");
    }

    private async Task<EntradaAvisoDatos?> CargarAsync(string cref)
    {
        using IDbConnection conn = _db.CreateConnection();

        // Clases tipadas con alias explícitos (no dynamic, no snake_case): ver la memoria «Dapper snake_case mapping».
        FilaEntrada? f = await conn.QueryFirstOrDefaultAsync<FilaEntrada>(
            @"SELECT t.creferencia AS Referencia,
                     c.cnombre AS Cliente,
                     tr.cnombre AS Transportista,
                     t.cnocaja AS Caja,
                     t.cnoguia AS Guia,
                     t.cidmanejo AS Manejo,
                     t.planta AS Planta,
                     t.bodega AS Bodega,
                     t.crecibio AS Recibio,
                     t.mobserv AS Observaciones,
                     t.cmarcas1 AS UnHazmat,
                     t.cmarcas2 AS ClaseHazmat,
                     t.cinbondno AS InBondNumero,
                     t.fcreatrafico AS FechaArribo,
                     t.nsubpeso AS PesoLbs,
                     t.ntotpeso AS PesoKgs,
                     t.nbultosin AS Bultos,
                     CAST(COALESCE(t.lhazmat, 0) AS SIGNED) AS Hazmat,
                     CAST(COALESCE(t.linbond, 0) AS SIGNED) AS InBond,
                     CAST(COALESCE(t3.danada, 0) AS SIGNED) AS Danada,
                     t3.danada_descripcion AS DescripcionDanio,
                     CAST(COALESCE(t3.madera_c_sello, 0) AS SIGNED) AS MaderaConSello,
                     CAST(COALESCE(t3.madera_s_sello, 0) AS SIGNED) AS MaderaSinSello,
                     CAST(COALESCE(t3.aserrin, 0) AS SIGNED) AS Aserrin,
                     CAST(COALESCE(t3.nosello, 0) AS SIGNED) AS NoRequiereSello
              FROM traf t
              LEFT JOIN clientes c ON c.cidcte = t.cclienteidtraf
              LEFT JOIN transpor tr ON tr.cidtranspor = t.cidtransportraf
              LEFT JOIN traf3 t3 ON t3.fk_idtraf = t.idtraf
              WHERE t.creferencia = @cref
              LIMIT 1",
            new { cref });
        if (f == null)
        {
            return null;
        }

        List<FilaDetalle> detalles = (await conn.QueryAsync<FilaDetalle>(
            @"SELECT cclase AS Clase, ncantidad AS Cantidad, cdescripcion AS Descripcion,
                     cobservaciones AS Atados, ubicacion AS Ubicacion
              FROM trafdet WHERE creferencia = @cref ORDER BY nlineno ASC",
            new { cref })).ToList();

        List<FilaConteo> conteo = (await conn.QueryAsync<FilaConteo>(
            "SELECT ntipodocto AS Tipo, COUNT(*) AS Total FROM fotos WHERE creferencia = @cref AND ntipodocto IN (100, 103) GROUP BY ntipodocto",
            new { cref })).ToList();

        // «mobserv» trae las etiquetas internas pegadas con « | » (DAÑADA, MaderaSello:…): al correo va solo lo que escribió bodega.
        string observaciones = f.Observaciones ?? string.Empty;
        int pipe = observaciones.IndexOf(" | ", StringComparison.Ordinal);
        if (pipe >= 0) observaciones = observaciones[..pipe];

        bool danada = f.Danada == 1;
        return new EntradaAvisoDatos
        {
            Referencia = f.Referencia?.Trim() ?? cref,
            Cliente = f.Cliente ?? string.Empty,
            Transportista = f.Transportista ?? string.Empty,
            Caja = f.Caja ?? string.Empty,
            Guia = f.Guia ?? string.Empty,
            Manejo = f.Manejo ?? string.Empty,
            Planta = f.Planta ?? string.Empty,
            Bodega = f.Bodega ?? string.Empty,
            Recibio = f.Recibio ?? string.Empty,
            Observaciones = observaciones.Trim(),
            FechaArribo = f.FechaArribo,
            PesoLbs = f.PesoLbs ?? 0,
            PesoKgs = f.PesoKgs ?? 0,
            Bultos = f.Bultos ?? 0,
            Hazmat = f.Hazmat == 1,
            ClaseHazmat = f.ClaseHazmat ?? string.Empty,
            UnHazmat = f.UnHazmat ?? string.Empty,
            Danada = danada,
            DescripcionDanio = f.DescripcionDanio ?? string.Empty,
            InBond = f.InBond == 1,
            InBondNumero = f.InBondNumero ?? string.Empty,
            MaderaConSello = f.MaderaConSello,
            MaderaSinSello = f.MaderaSinSello,
            Aserrin = f.Aserrin,
            NoRequiereSello = f.NoRequiereSello,
            FotosEvidencia = conteo.FirstOrDefault(c => c.Tipo == 100)?.Total ?? 0,
            FotosDanio = danada ? conteo.FirstOrDefault(c => c.Tipo == 103)?.Total ?? 0 : 0,
            Detalles = detalles
                .Select(d => new EntradaAvisoDetalle(d.Cantidad ?? 0, d.Clase ?? string.Empty, d.Descripcion ?? string.Empty, d.Atados ?? string.Empty, d.Ubicacion ?? string.Empty))
                .ToList()
        };
    }

    private sealed class FilaEntrada
    {
        public string? Referencia { get; set; }
        public string? Cliente { get; set; }
        public string? Transportista { get; set; }
        public string? Caja { get; set; }
        public string? Guia { get; set; }
        public string? Manejo { get; set; }
        public string? Planta { get; set; }
        public string? Bodega { get; set; }
        public string? Recibio { get; set; }
        public string? Observaciones { get; set; }
        public string? UnHazmat { get; set; }
        public string? ClaseHazmat { get; set; }
        public string? InBondNumero { get; set; }
        public DateTime? FechaArribo { get; set; }
        public decimal? PesoLbs { get; set; }
        public decimal? PesoKgs { get; set; }
        public decimal? Bultos { get; set; }
        public int Hazmat { get; set; }
        public int InBond { get; set; }
        public int Danada { get; set; }
        public string? DescripcionDanio { get; set; }
        public int MaderaConSello { get; set; }
        public int MaderaSinSello { get; set; }
        public int Aserrin { get; set; }
        public int NoRequiereSello { get; set; }
    }

    private sealed class FilaDetalle
    {
        public string? Clase { get; set; }
        public decimal? Cantidad { get; set; }
        public string? Descripcion { get; set; }
        public string? Atados { get; set; }
        public string? Ubicacion { get; set; }
    }

    private sealed class FilaConteo
    {
        public int Tipo { get; set; }
        public int Total { get; set; }
    }
}
