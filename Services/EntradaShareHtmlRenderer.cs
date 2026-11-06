using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Arma el HTML de la página pública de "Seguimiento de entrada". Rediseñada 2026-11-06 con el mismo lenguaje que
/// "Seguimiento de revisión" y la evidencia de caseta (<see cref="CasetaShareHtmlRenderer"/>): barra azul con la marca
/// PMM, resumen fijo a la izquierda, paneles, visor a pantalla completa y pie institucional, según el mockup aprobado
/// (<c>mockups/entrada-enlace-danio-rediseno.html</c>).
///
/// Novedad: cuando la entrada se declaró con mercancía dañada, sus fotos salen en una sección propia arriba de la
/// evidencia, con la etiqueta «Daño», el aviso «faltan N por llegar» mientras la cola de la app las sube y un ZIP con
/// carpetas «Daño» y «Evidencia».
///
/// Todo texto que viene de la base de datos se escapa: esta página la puede abrir cualquiera con el link, nunca confiar
/// en el contenido como si ya fuera HTML seguro (la descripción del daño es texto libre escrito por bodega).
/// </summary>
public static class EntradaShareHtmlRenderer
{
    private static readonly string[] Meses = { "ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic" };

    private static readonly Regex Marcador = new(@"@@(\w+)@@", RegexOptions.Compiled);

    /// <param name="fotoBaseUrl">Base de las fotos de evidencia; a cada una se le agrega «/{línea}».</param>
    /// <param name="fotoDanioBaseUrl">Base de las fotos del daño; a cada una se le agrega «/{línea}».</param>
    /// <param name="mostrarDescripcionDanio">
    /// Si es <c>false</c> la página muestra las fotos del daño con su etiqueta pero NO el texto libre que escribió bodega
    /// (el mismo enlace puede llegar a clientes). Se controla con <c>PublicLinks:MostrarDescripcionDanioEntrada</c>.
    /// </param>
    public static string RenderResumen(
        EntradaShareResumen r,
        string fotoBaseUrl,
        string fotoDanioBaseUrl,
        string zipUrl,
        bool mostrarDescripcionDanio = true)
    {
        int nEvidencia = r.Fotos.Count;
        int nDanio = r.Danada ? r.FotosDanio.Count : 0;
        int pendientes = r.Danada ? Math.Max(0, r.FotosDanioPendientes) : 0;
        int total = nEvidencia + nDanio;

        // Datos de la entrada. Una fila sin dato no se pinta.
        var stats = new StringBuilder();
        void Fila(string etiqueta, string? valor, bool mono)
        {
            if (!string.IsNullOrWhiteSpace(valor))
            {
                stats.Append($@"<div class=""stat""><span>{Enc(etiqueta)}</span><b{(mono ? @" class=""mono""" : "")}>{Enc(valor.Trim())}</b></div>");
            }
        }

        Fila("Cliente", r.NombreCliente, false);
        Fila("Transportista", r.NombreTransportista, false);
        Fila("Unidad", r.Unidad, true);
        Fila("Ubicación", r.Ubicacion, false);
        Fila("Llegada", r.FechaLlegada, true);
        Fila("Fotos", nDanio > 0 ? $"{total} · {nDanio} de daño" : total.ToString(), true);

        // Las fotos van en este orden (daño primero): el visor y el arreglo PHOTOS dependen de que sea el del documento.
        var fotos = new List<(string Url, string Grupo, int N, string Hora)>();
        var danioTiles = new StringBuilder();
        var evidenciaTiles = new StringBuilder();

        string baseEvidencia = fotoBaseUrl.TrimEnd('/');
        string baseDanio = fotoDanioBaseUrl.TrimEnd('/');

        foreach (EntradaShareFoto foto in r.Danada ? r.FotosDanio : new List<EntradaShareFoto>())
        {
            string url = $"{baseDanio}/{foto.NLineNo}";
            fotos.Add((url, "d", foto.NLineNo, foto.Hora));
            danioTiles.Append(Tile(url, foto.NLineNo, foto.Hora, esDanio: true, $"Abrir foto del daño {foto.NLineNo}"));
        }

        foreach (EntradaShareFoto foto in r.Fotos)
        {
            string url = $"{baseEvidencia}/{foto.NLineNo}";
            fotos.Add((url, "e", foto.NLineNo, foto.Hora));
            evidenciaTiles.Append(Tile(url, foto.NLineNo, foto.Hora, esDanio: false, $"Abrir foto {foto.NLineNo}"));
        }

        string danioPanel = string.Empty;
        if (r.Danada)
        {
            int esperadas = nDanio + pendientes;
            string conteo = pendientes > 0
                ? $"{nDanio} de {esperadas} {(esperadas == 1 ? "foto" : "fotos")} del daño"
                : $"{nDanio} {(nDanio == 1 ? "foto" : "fotos")} del daño";

            string descripcion = mostrarDescripcionDanio && !string.IsNullOrWhiteSpace(r.DanadaDescripcion)
                ? $@"<div class=""dmg-desc""><small>Descripción registrada por bodega</small><p>{Enc(r.DanadaDescripcion)}</p></div>"
                : string.Empty;

            string aviso = pendientes > 0
                ? $@"<div class=""banner"" id=""pendNote""><span class=""spin""></span><span id=""pendTxt"">{(pendientes == 1 ? "Falta 1 foto" : $"Faltan {pendientes} fotos")} del daño por llegar. Esta página se actualiza sola.</span></div>"
                : string.Empty;

            var esperando = new StringBuilder();
            for (int i = 0; i < pendientes; i++)
            {
                esperando.Append(@"<div class=""tile tile--wait"" role=""img"" aria-label=""Foto del daño subiéndose""><span class=""spin""></span>subiendo…</div>");
            }

            string vacio = nDanio == 0 && pendientes == 0
                ? $@"<div class=""empty"">{Icono("camera", 34, 1.6)}Bodega declaró mercancía dañada y las fotos del daño todavía no llegan.</div>"
                : string.Empty;

            danioPanel = $@"<section class=""panel panel--dmg"" aria-label=""Mercancía dañada""><div class=""panel__head""><h2>{Icono("warn", 18, 2.2)} Mercancía dañada</h2><span class=""count"">{Enc(conteo)}</span></div>{descripcion}{aviso}<div class=""grid"">{danioTiles}{esperando}{vacio}</div></section>";
        }

        string tituloEvidencia = r.Danada ? "Evidencia de la entrada" : "Fotos de la entrada";
        string evidenciaPanel = $@"<section class=""panel"" aria-label=""{Enc(tituloEvidencia)}""><div class=""panel__head""><h2>{Enc(tituloEvidencia)}</h2><span class=""count"">{nEvidencia} {(nEvidencia == 1 ? "foto" : "fotos")}</span></div><div class=""grid"">{(nEvidencia > 0 ? evidenciaTiles.ToString() : $@"<div class=""empty"">{Icono("camera", 34, 1.6)}Todavía no hay fotos cargadas.</div>")}</div></section>";

        string pillDanio = r.Danada ? $@"<span class=""pill pill--amber"">{Icono("warn", 15, 2.2)} Mercancía dañada</span>" : string.Empty;

        string cta = total == 0 ? string.Empty
            : $@"<div class=""cta""><a class=""btn"" href=""{Enc(zipUrl)}""><span>{Icono("download", 16, 2.4)}Descargar todas las fotos</span><small>{(nDanio > 0 ? $"ZIP con carpetas {EntradaShareService.CarpetaZipDanio} y {EntradaShareService.CarpetaZipEvidencia}" : "ZIP con todas las fotos")}</small></a></div>";

        var datos = new StringBuilder();
        datos.Append("var PHOTOS=")
            .Append(JsonSerializer.Serialize(fotos.Select(f => new { u = f.Url, g = f.Grupo, n = f.N, t = f.Hora })))
            .Append(";var PENDING=").Append(pendientes).Append(';');

        var valores = new Dictionary<string, string>
        {
            ["CSS"] = Css,
            ["REF"] = Enc(r.CrefAgrupada),
            ["PILLDANIO"] = pillDanio,
            ["FRESCO"] = Enc($"Enlace generado hace {MinutosDesde(r.GeneradoEn)}"),
            ["STATS"] = stats.Length > 0 ? $@"<div class=""stats"">{stats}</div>" : string.Empty,
            ["CTA"] = cta,
            ["EXPIRA"] = Enc(FechaExpira(r.Expira)),
            ["DANIO"] = danioPanel,
            ["EVIDENCIA"] = evidenciaPanel,
            ["DATOS"] = datos.ToString(),
            ["JS"] = Js
        };

        return Rellenar(Plantilla, valores);
    }

    public static string RenderExpirado()
    {
        var valores = new Dictionary<string, string>
        {
            ["CSS"] = Css,
            ["RELOJ"] = Icono("clock", 26, 2)
        };
        return Rellenar(PlantillaExpirado, valores);
    }

    // ── Ayudas ───────────────────────────────────────────────────────────────

    private static string Tile(string url, int n, string hora, bool esDanio, string etiquetaAria)
    {
        string src = Enc(url);
        string rotulo = esDanio ? $"Daño {n}" : $"Foto {n}";
        return $@"<a class=""tile"" href=""{src}"" target=""_blank"" rel=""noreferrer"" aria-label=""{Enc(etiquetaAria)}""><img src=""{src}"" alt=""{Enc(rotulo)}"" loading=""lazy""><span class=""tile__n"">{n}</span>{(esDanio ? @"<span class=""tile__tag"">Daño</span>" : "")}{(hora.Length > 0 ? $@"<span class=""tile__t"">{Enc(hora)}</span>" : "")}</a>";
    }

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>Una sola pasada: lo que se inserta nunca se vuelve a buscar en busca de marcadores.</summary>
    private static string Rellenar(string plantilla, Dictionary<string, string> valores) =>
        Marcador.Replace(plantilla, m => valores.TryGetValue(m.Groups[1].Value, out string? v) ? v : string.Empty);

    private static string MinutosDesde(DateTime desde)
    {
        TimeSpan diff = DateTime.Now - desde;
        if (diff.TotalMinutes < 1) return "un momento";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} h";
        return $"{(int)diff.TotalDays} días";
    }

    private static string FechaExpira(DateTime fecha) => $"{fecha.Day} {Meses[fecha.Month - 1]}, {fecha:HH:mm}";

    private static string Icono(string nombre, int tamano, double trazo)
    {
        string trazos = nombre switch
        {
            "download" => "<path d=\"M12 4v11M7 11l5 5 5-5M5 20h14\"/>",
            "camera" => "<path d=\"M4 8h3l1.5-2h7L17 8h3v11H4z\"/><circle cx=\"12\" cy=\"13\" r=\"3.5\"/>",
            "clock" => "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 7v5l3 2\"/>",
            "warn" => "<path d=\"M12 3 2.8 19h18.4L12 3Z\"/><path d=\"M12 9v4m0 3h.01\"/>",
            "check" => "<path d=\"m5 12.5 4.5 4.5L19 7.5\"/>",
            _ => string.Empty
        };
        string color = nombre == "clock" ? "#003DA5" : nombre == "camera" ? "#9AA6C0" : "currentColor";
        return $"<svg width=\"{tamano}\" height=\"{tamano}\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{trazo.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">{trazos}</svg>";
    }

    // ── Plantillas ───────────────────────────────────────────────────────────

    private const string Plantilla = """
<!doctype html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<meta name="robots" content="noindex, nofollow">
<title>Seguimiento de entrada @@REF@@ · Grupo PMM</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Manrope:wght@500;700;800&family=IBM+Plex+Sans:wght@400;500;600;700&family=IBM+Plex+Mono:wght@500;600&display=swap" rel="stylesheet">
<style>@@CSS@@</style>
</head>
<body>
<div class="topbar"><div class="topbar__in">
  <div class="mark">PMM</div>
  <div class="brand"><b>Grupo PMM</b><span>Seguimiento de entrada</span></div>
  <div class="secure"><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 018 0v3"/></svg>Enlace privado</div>
</div></div>
<div class="shell">
  <aside class="summary">
    <div class="summary__head">
      <div class="eyebrow">Entrada</div>
      <div class="ref"><small>Referencia</small>@@REF@@</div>
      <div class="pills"><span class="pill pill--green"><svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m5 12.5 4.5 4.5L19 7.5"/></svg> Entrada registrada</span>@@PILLDANIO@@</div>
      <div class="fresh">@@FRESCO@@</div>
    </div>
    @@STATS@@
    @@CTA@@
    <div class="meta">Este enlace expira el <b>@@EXPIRA@@</b>.</div>
  </aside>
  <main class="main">
    @@DANIO@@
    @@EVIDENCIA@@
  </main>
</div>
<div class="foot">No responder: para dudas, contacta a tu contacto en PMM.<i>GRUPO PMM · SISTEMA INTERNO</i></div>
<div class="vw" id="vw" role="dialog" aria-modal="true" aria-label="Visor de fotos" aria-hidden="true">
  <div class="vw__top">
    <span class="vw__tag" id="vwTag"></span><span class="vw__cap" id="vwCap"></span><span class="vw__sp"></span>
    <button type="button" class="vw__b" id="vwZoom" aria-label="Acercar o alejar" title="Acercar (Z)"><svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="M21 21l-4.3-4.3M11 8v6M8 11h6"/></svg></button>
    <button type="button" class="vw__b" id="vwClose" aria-label="Cerrar visor" title="Cerrar (Esc)"><svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><path d="M6 6l12 12M18 6L6 18"/></svg></button>
  </div>
  <div class="vw__stage" id="vwStage">
    <button type="button" class="vw__nav vw__nav--p" id="vwPrev" aria-label="Foto anterior"><svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M15 5l-7 7 7 7"/></svg></button>
    <img id="vwImg" alt="">
    <button type="button" class="vw__nav vw__nav--n" id="vwNext" aria-label="Foto siguiente"><svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M9 5l7 7-7 7"/></svg></button>
  </div>
  <div class="vw__bot">
    <div class="vw__thumbs" id="vwThumbs"></div>
    <a class="vw__dl" id="vwDl" href="#" download><svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 4v11M7 11l5 5 5-5M5 20h14"/></svg>Descargar esta foto</a>
  </div>
</div>
<script>@@DATOS@@</script>
<script>@@JS@@</script>
</body>
</html>
""";

    private const string PlantillaExpirado = """
<!doctype html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<meta name="robots" content="noindex, nofollow">
<title>Enlace no disponible · Grupo PMM</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Manrope:wght@500;700;800&family=IBM+Plex+Sans:wght@400;500;600;700&family=IBM+Plex+Mono:wght@500;600&display=swap" rel="stylesheet">
<style>@@CSS@@</style>
</head>
<body>
<div class="topbar"><div class="topbar__in">
  <div class="mark">PMM</div>
  <div class="brand"><b>Grupo PMM</b><span>Seguimiento de entrada</span></div>
</div></div>
<div class="gone"><div class="gone__c">
  <div class="gone__i">@@RELOJ@@</div>
  <h2>Este enlace ya no está disponible</h2>
  <p>Por seguridad, los enlaces de seguimiento dejan de funcionar después de cierto tiempo. Pide a tu contacto en PMM que te comparta uno nuevo.</p>
</div></div>
</body>
</html>
""";

    // Mismos tokens que Seguimiento de revisión y la evidencia de caseta. Dos columnas en escritorio, una en <=860px.
    private const string Css = """
:root{
  --blue:#003DA5; --navy:#002680; --blue-pale:#EAF1FF; --blue-line:#CFE0FF;
  --green:#15803D; --green-pale:#EAF9F0;
  --amber-ink:#92400E; --amber-pale:#FFF4E5; --amber-line:#F6D3A2;
  --bg:#EDF1F8; --surface:#FFFFFF; --surface-2:#F6F8FC; --ink:#0E1729; --ink-soft:#47506B; --ink-mute:#667089; --border:#E1E6F0;
  --shadow:0 1px 2px rgba(16,40,90,.06),0 8px 24px rgba(16,40,90,.06); --radius:16px;
  --f-display:'Manrope',system-ui,sans-serif; --f-body:'IBM Plex Sans',system-ui,sans-serif; --f-mono:'IBM Plex Mono',ui-monospace,monospace;
}
*{box-sizing:border-box}
html,body{margin:0;padding:0}
body{background:var(--bg);color:var(--ink);font-family:var(--f-body);-webkit-font-smoothing:antialiased}
body.vw-open{overflow:hidden}
a{color:inherit}
:focus-visible{outline:3px solid #7AA7FF;outline-offset:2px}
.mono{font-family:var(--f-mono);font-variant-numeric:tabular-nums}
h1,h2{font-family:var(--f-display);margin:0}
.topbar{background:linear-gradient(160deg,var(--blue),var(--navy));padding:18px 40px}
.topbar__in{max-width:1180px;margin:0 auto;display:flex;align-items:center;gap:14px}
.mark{width:38px;height:38px;border-radius:10px;background:#fff;display:grid;place-items:center;font:800 12px var(--f-display);color:var(--blue);flex:none}
.brand b{display:block;color:#fff;font-size:15px;font-weight:700}
.brand span{display:block;color:rgba(255,255,255,.74);font-size:11.5px;margin-top:2px}
.secure{margin-left:auto;display:flex;gap:7px;align-items:center;color:rgba(255,255,255,.82);font-size:11.5px}
.shell{max-width:1180px;margin:0 auto;padding:36px 40px 20px;display:grid;grid-template-columns:300px 1fr;gap:24px;align-items:start}
.summary{position:sticky;top:24px;background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);box-shadow:var(--shadow);overflow:hidden}
.summary__head{padding:22px 22px 18px;border-bottom:1px solid var(--border)}
.eyebrow{font:600 10.5px var(--f-mono);letter-spacing:.14em;text-transform:uppercase;color:var(--blue)}
.ref{margin-top:8px;font:700 21px var(--f-mono);letter-spacing:-.01em;word-break:break-all}
.ref small{display:block;font:500 11px var(--f-body);color:var(--ink-mute);letter-spacing:0;margin-bottom:2px}
.pills{margin-top:14px;display:flex;flex-wrap:wrap;gap:8px}
.pill{display:inline-flex;align-items:center;gap:7px;padding:7px 14px;border-radius:999px;font-size:12px;font-weight:700;letter-spacing:.02em}
.pill--green{background:var(--green-pale);color:var(--green)}
.pill--amber{background:var(--amber-pale);color:var(--amber-ink);border:1px solid var(--amber-line)}
.fresh{margin-top:12px;font-size:11.5px;color:var(--ink-mute)}
.stats{padding:18px 22px;display:flex;flex-direction:column;gap:15px}
.stat{display:flex;justify-content:space-between;align-items:baseline;gap:12px}
.stat span{font-size:11.5px;color:var(--ink-soft);flex:none}
.stat b{font-size:13.5px;font-weight:700;text-align:right;min-width:0;overflow-wrap:anywhere}
.cta{padding:0 22px 18px}
.btn{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:2px;width:100%;background:var(--blue);color:#fff;font-weight:700;font-size:13px;text-decoration:none;padding:11px 16px;border-radius:11px;border:0;text-align:center}
.btn span{display:flex;align-items:center;gap:8px}
.btn:hover{background:var(--navy)}
.btn small{font-weight:500;opacity:.82;font-size:11.5px}
.meta{padding:14px 22px 20px;font-size:11px;color:var(--ink-mute);line-height:1.6;border-top:1px solid var(--border)}
.main{display:flex;flex-direction:column;gap:22px;min-width:0}
.panel{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);box-shadow:var(--shadow);overflow:hidden}
.panel__head{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:18px 24px;background:var(--blue-pale);border-bottom:1px solid var(--blue-line)}
.panel__head h2{font-size:15px;font-weight:700;color:var(--navy);display:flex;align-items:center;gap:9px}
.panel__head .count{font-size:12px;color:var(--ink-soft);font-weight:600;white-space:nowrap}
.panel--dmg{border-color:var(--amber-line)}
.panel--dmg .panel__head{background:var(--amber-pale);border-bottom-color:var(--amber-line)}
.panel--dmg .panel__head h2{color:var(--amber-ink)}
.dmg-desc{margin:18px 18px 0;padding:13px 16px;border-left:4px solid #E0A458;background:var(--surface-2);border-radius:0 10px 10px 0}
.dmg-desc small{display:block;font:600 10px var(--f-mono);letter-spacing:.12em;text-transform:uppercase;color:var(--ink-mute)}
.dmg-desc p{margin:5px 0 0;font-size:13.5px;color:var(--ink);font-weight:500;white-space:pre-wrap;overflow-wrap:anywhere}
.banner{margin:18px 18px 0;display:flex;gap:10px;align-items:center;padding:11px 14px;border-radius:11px;background:var(--blue-pale);border:1px solid var(--blue-line);color:var(--navy);font-size:12.5px;font-weight:600}
.spin{width:16px;height:16px;border-radius:50%;border:2.5px solid #B9C9EE;border-top-color:var(--blue);animation:rot 1s linear infinite;flex:none;display:inline-block}
@keyframes rot{to{transform:rotate(360deg)}}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:12px;padding:18px}
.tile{position:relative;aspect-ratio:4/3;border-radius:12px;border:1px solid var(--border);background:var(--surface-2);overflow:hidden;padding:0;display:block;width:100%;text-decoration:none}
.tile img{width:100%;height:100%;object-fit:cover;display:block;transition:transform .25s}
.tile:hover img,.tile:focus-visible img{transform:scale(1.06)}
.tile__n{position:absolute;top:7px;left:7px;background:rgba(255,255,255,.94);color:var(--navy);font:700 11px var(--f-mono);min-width:22px;height:22px;border-radius:999px;display:grid;place-items:center;padding:0 6px}
.tile__tag{position:absolute;top:7px;right:7px;background:var(--amber-pale);color:var(--amber-ink);border:1px solid var(--amber-line);font:700 9.5px var(--f-mono);letter-spacing:.08em;text-transform:uppercase;border-radius:999px;padding:2px 8px}
.tile__t{position:absolute;inset:auto 0 0 0;padding:16px 8px 6px;background:linear-gradient(transparent,rgba(6,10,22,.82));color:#fff;font:500 10.5px var(--f-mono);text-align:left}
.tile--wait{display:grid;place-items:center;align-content:center;gap:8px;background:repeating-linear-gradient(135deg,#F1F4FA 0 10px,#E8EDF7 10px 20px);color:var(--ink-mute);font:600 11px var(--f-mono);border-style:dashed}
.empty{grid-column:1/-1;padding:40px 20px;text-align:center;color:var(--ink-mute);font-size:13px}
.empty svg{display:block;margin:0 auto 10px}
.foot{max-width:1180px;margin:0 auto;padding:0 40px 36px;text-align:center;font-size:11px;line-height:1.6;color:var(--ink-mute)}
.foot i{display:block;margin-top:12px;font-style:normal;font-size:10px;letter-spacing:.05em}
.gone{max-width:520px;margin:0 auto;padding:60px 20px}
.gone__c{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:40px 32px;text-align:center;box-shadow:var(--shadow)}
.gone__i{width:56px;height:56px;border-radius:50%;background:var(--blue-pale);display:grid;place-items:center;margin:0 auto 16px}
.gone h2{font-size:18px;font-weight:700}
.gone p{font-size:13.5px;color:var(--ink-soft);line-height:1.65;margin:12px auto 0;max-width:38ch}
.vw{position:fixed;inset:0;z-index:100;background:rgba(6,10,22,.97);display:none;flex-direction:column;color:#fff}
.vw.show{display:flex}
.vw__top{display:flex;align-items:center;gap:12px;padding:14px 18px}
.vw__tag{font:700 11px var(--f-body);letter-spacing:.05em;text-transform:uppercase;padding:5px 12px;border-radius:999px;background:rgba(255,255,255,.14);flex:none}
.vw__tag.dmg{background:var(--amber-pale);color:var(--amber-ink)}
.vw__cap{font:500 12px var(--f-mono);color:rgba(255,255,255,.78);min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.vw__sp{flex:1}
.vw__b{width:36px;height:36px;border-radius:50%;background:rgba(255,255,255,.13);border:0;color:#fff;display:grid;place-items:center;flex:none;cursor:pointer}
.vw__b:hover{background:rgba(255,255,255,.24)}
.vw__stage{flex:1;min-height:0;display:flex;align-items:center;justify-content:center;position:relative;padding:0 64px 8px;overflow:hidden;touch-action:pan-y}
.vw__stage img{max-width:100%;max-height:100%;border-radius:10px;box-shadow:0 20px 60px rgba(0,0,0,.5);transition:transform .2s;cursor:zoom-in;user-select:none;-webkit-user-drag:none}
.vw.zoom .vw__stage img{transform:scale(1.8);cursor:zoom-out}
.vw__nav{position:absolute;top:50%;transform:translateY(-50%);width:46px;height:46px;border-radius:50%;background:rgba(255,255,255,.13);border:0;color:#fff;display:grid;place-items:center;cursor:pointer}
.vw__nav:hover{background:rgba(255,255,255,.26)}
.vw__nav--p{left:12px}
.vw__nav--n{right:12px}
.vw__bot{padding:8px 16px 16px;display:flex;flex-direction:column;gap:12px;align-items:center}
.vw__thumbs{display:flex;gap:8px;max-width:100%;overflow-x:auto;padding:2px}
.vw__thumbs button{flex:none;width:64px;height:48px;border-radius:8px;border:2px solid transparent;padding:0;overflow:hidden;opacity:.55;background:#000;cursor:pointer}
.vw__thumbs button.d{box-shadow:inset 0 -3px 0 #E0A458}
.vw__thumbs button[aria-current="true"]{border-color:#fff;opacity:1}
.vw__thumbs img{width:100%;height:100%;object-fit:cover;display:block}
.vw__dl{display:inline-flex;gap:8px;align-items:center;background:#fff;color:var(--ink);font-weight:700;font-size:12.5px;text-decoration:none;padding:10px 18px;border-radius:999px}
@media (min-width:1000px){.grid{grid-template-columns:repeat(auto-fill,minmax(170px,1fr))}}
@media (max-width:860px){
  .topbar{padding:14px 18px}
  .secure{display:none}
  .shell{grid-template-columns:1fr;padding:0 0 12px;gap:0}
  .summary{position:static;border-radius:0;border-left:0;border-right:0;margin-top:-1px}
  .main{padding:16px;gap:16px}
  .grid{grid-template-columns:repeat(2,1fr);padding:14px;gap:10px}
  .panel__head{padding:15px 16px}
  .ref{font-size:19px}
  .foot{padding:0 16px 28px}
  .vw__stage{padding:0 10px 8px}
  .vw__nav{width:38px;height:38px}
  .vw__nav--p{left:6px}
  .vw__nav--n{right:6px}
  .vw__thumbs button{width:52px;height:40px}
}
@media (prefers-reduced-motion:reduce){*{transition:none!important}.spin{animation:none}}
@media print{.vw,.cta{display:none!important}.summary{position:static}}
""";

    // El visor: flechas, teclado, deslizar, miniaturas y acercar donde se toca; muestra el grupo («Daño» o «Evidencia»)
    // y el número dentro de su grupo. Sin JavaScript cada foto abre en otra pestaña. Si faltan fotos del daño por llegar,
    // la página se recarga sola cada 30 s (hasta 20 min) para completarse, sin tocar nada mientras el visor está abierto.
    private const string Js = """
(function () {
  var vw = document.getElementById('vw');
  if (!vw || !window.PHOTOS || !PHOTOS.length) return;
  var img = document.getElementById('vwImg'), cap = document.getElementById('vwCap'), tag = document.getElementById('vwTag'),
      thumbs = document.getElementById('vwThumbs'), dl = document.getElementById('vwDl'),
      closeBtn = document.getElementById('vwClose'), stage = document.getElementById('vwStage'),
      tiles = Array.prototype.slice.call(document.querySelectorAll('a.tile'));
  var cur = 0, zoomed = false, opener = null, thumbsReady = false, startX = null;
  var groupTotal = { d: 0, e: 0 };
  PHOTOS.forEach(function (p) { groupTotal[p.g] = (groupTotal[p.g] || 0) + 1; });

  function posInGroup(i) {
    var n = 0;
    for (var k = 0; k <= i; k++) if (PHOTOS[k].g === PHOTOS[i].g) n++;
    return n;
  }

  function setZoom(on, ev) {
    zoomed = on;
    vw.classList.toggle('zoom', on);
    if (on && ev && img.getBoundingClientRect) {
      var r = img.getBoundingClientRect();
      var x = Math.min(100, Math.max(0, (ev.clientX - r.left) / r.width * 100));
      var y = Math.min(100, Math.max(0, (ev.clientY - r.top) / r.height * 100));
      img.style.transformOrigin = x + '% ' + y + '%';
    } else if (!on) {
      img.style.transformOrigin = '50% 50%';
    }
  }

  function thumbSrc(im) {
    try {
      if (im && im.complete && im.naturalWidth) {
        var c = document.createElement('canvas'); c.width = 96; c.height = 72;
        var r = Math.max(96 / im.naturalWidth, 72 / im.naturalHeight), w = 96 / r, h = 72 / r;
        c.getContext('2d').drawImage(im, (im.naturalWidth - w) / 2, (im.naturalHeight - h) / 2, w, h, 0, 0, 96, 72);
        return c.toDataURL('image/jpeg', 0.6);
      }
    } catch (e) { }
    return im ? (im.currentSrc || im.src) : '';
  }

  function buildThumbs() {
    if (thumbsReady) return;
    thumbsReady = true;
    tiles.forEach(function (t, k) {
      var b = document.createElement('button');
      b.type = 'button'; b.setAttribute('aria-label', 'Foto ' + (k + 1));
      if (PHOTOS[k] && PHOTOS[k].g === 'd') b.className = 'd';
      var im = document.createElement('img'); im.alt = ''; im.src = thumbSrc(t.querySelector('img'));
      b.appendChild(im);
      b.addEventListener('click', function () { show(k); });
      thumbs.appendChild(b);
    });
  }

  function markThumb() {
    var items = thumbs.children;
    for (var k = 0; k < items.length; k++) {
      if (k === cur) { items[k].setAttribute('aria-current', 'true'); if (items[k].scrollIntoView) items[k].scrollIntoView({ block: 'nearest', inline: 'center' }); }
      else items[k].removeAttribute('aria-current');
    }
  }

  function show(i) {
    cur = (i + PHOTOS.length) % PHOTOS.length;
    var p = PHOTOS[cur], dano = p.g === 'd', nombre = dano ? 'Daño' : 'Evidencia';
    setZoom(false);
    img.src = p.u;
    img.alt = nombre + ' ' + posInGroup(cur) + ' de ' + groupTotal[p.g];
    tag.textContent = nombre;
    tag.className = 'vw__tag' + (dano ? ' dmg' : '');
    cap.textContent = posInGroup(cur) + ' de ' + groupTotal[p.g] + (p.t ? ' · ' + p.t : '');
    dl.href = p.u;
    markThumb();
  }

  function open(i) {
    opener = document.activeElement;
    vw.classList.add('show'); vw.setAttribute('aria-hidden', 'false');
    document.body.classList.add('vw-open');
    buildThumbs(); show(i);
    closeBtn.focus();
  }

  function close() {
    vw.classList.remove('show'); vw.setAttribute('aria-hidden', 'true');
    document.body.classList.remove('vw-open');
    setZoom(false);
    if (opener && opener.focus) opener.focus();
  }

  tiles.forEach(function (t, k) {
    t.addEventListener('click', function (e) {
      if (e.ctrlKey || e.metaKey || e.shiftKey || e.button === 1) return;
      e.preventDefault(); open(k);
    });
  });
  closeBtn.addEventListener('click', close);
  document.getElementById('vwPrev').addEventListener('click', function () { show(cur - 1); });
  document.getElementById('vwNext').addEventListener('click', function () { show(cur + 1); });
  document.getElementById('vwZoom').addEventListener('click', function () { setZoom(!zoomed); });
  img.addEventListener('click', function (e) { setZoom(!zoomed, e); });

  document.addEventListener('keydown', function (e) {
    if (!vw.classList.contains('show')) return;
    if (e.key === 'ArrowLeft') show(cur - 1);
    else if (e.key === 'ArrowRight') show(cur + 1);
    else if (e.key === 'Escape') close();
    else if (e.key === 'z' || e.key === 'Z') setZoom(!zoomed);
    else if (e.key === 'Tab') {
      var f = vw.querySelectorAll('button, a[href]');
      if (!f.length) return;
      var first = f[0], last = f[f.length - 1];
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    }
  });

  stage.addEventListener('pointerdown', function (e) { if (!e.target.closest('.vw__nav')) startX = e.clientX; });
  stage.addEventListener('pointerup', function (e) {
    if (startX === null) return;
    var dx = e.clientX - startX; startX = null;
    if (Math.abs(dx) > 50 && !zoomed) show(cur + (dx < 0 ? 1 : -1));
  });
})();

(function () {
  // Faltan fotos del daño por llegar: se recarga sola cada 30 s, hasta 20 min, para completarse. El contador y la posición
  // se guardan en sessionStorage; si el navegador no lo permite no se recarga (mejor nada que un ciclo sin fin).
  var note = document.getElementById('pendNote'), txt = document.getElementById('pendTxt');
  if (!note || !window.PENDING) return;
  var KEY = 'entradaRefresh:' + location.pathname, MAX = 40, n = 0, ok = true;
  function pararAviso() {
    var s = note.querySelector('.spin'); if (s) s.style.display = 'none';
    if (txt) txt.textContent = txt.textContent.replace('Esta página se actualiza sola.', 'Vuelve a abrir la página más tarde.');
  }
  try {
    n = parseInt(sessionStorage.getItem(KEY) || '0', 10) || 0;
    var y = parseInt(sessionStorage.getItem(KEY + ':y') || '0', 10);
    if (y > 0) window.scrollTo(0, y);
  } catch (e) { ok = false; }
  if (!ok || n >= MAX) { pararAviso(); return; }
  setInterval(function () {
    var vw = document.getElementById('vw');
    if (vw && vw.classList.contains('show')) return;
    try { sessionStorage.setItem(KEY, String(n + 1)); sessionStorage.setItem(KEY + ':y', String(window.scrollY || 0)); } catch (e) { return; }
    location.reload();
  }, 30000);
})();
""";
}
