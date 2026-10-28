using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Página pública de la evidencia fotográfica de caseta (entrada y salida). Rediseñada 2026-10-28 con el mismo lenguaje
/// de "Seguimiento de revisión" (<see cref="RevisionShareHtmlRenderer"/>): barra azul con la marca PMM, resumen fijo a la
/// izquierda con el número grande y el estado, paneles, visor a pantalla completa y pie institucional. Los tokens de
/// color y las tipografías son los mismos que usa esa página.
///
/// Lo que muestra viene de <see cref="CasetaShareDetalle"/> y deliberadamente NO incluye clientes ni pedimentos: el mismo
/// enlace llega a todos los clientes de una relación. Todo texto que viene de la base se escapa: la puede abrir cualquiera
/// con el enlace.
/// </summary>
public static class CasetaShareHtmlRenderer
{
    private static readonly string[] Meses = { "ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic" };

    private static readonly Dictionary<string, string> Acentos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["importacion"] = "Importación",
        ["exportacion"] = "Exportación",
        ["colocacion"] = "Colocación",
        ["mensajeria"] = "Mensajería",
        ["recoleccion"] = "Recolección",
        ["vacio"] = "Vacío"
    };

    private static readonly Regex Marcador = new(@"@@(\w+)@@", RegexOptions.Compiled);

    /// <param name="mostrarOperador">
    /// Si es <c>false</c> la página no muestra operador, tractor ni sellos (siguen yendo en el correo). Se controla con
    /// <c>PublicLinks:MostrarDatosOperadorCaseta</c>; por omisión se muestran.
    /// </param>
    public static string RenderResumen(CasetaShareResumen resumen, string fotoBaseUrl, string zipUrl, bool mostrarOperador = true)
    {
        bool salida = resumen.Tipo == CasetaShareService.TipoSalida;
        CasetaShareDetalle d = resumen.Detalle ?? new CasetaShareDetalle();
        string tipoTexto = salida ? "Salida de caseta" : "Entrada de caseta";
        int total = resumen.Fotos.Count;

        // Estado y cuánto hace que ocurrió.
        DateTime? momento = salida ? d.Salida : d.Registrada;
        string pillClase = salida ? "pill--green" : "pill--blue";
        string pillTexto = salida ? "✓ Salida confirmada" : "● Entrada registrada";
        string fresco = $"Registrada hace {MinutosDesde(momento ?? resumen.GeneradoEn)}";

        // Datos de la relación o del registro. Una fila sin dato no se pinta.
        var filas = new List<(string Etiqueta, string Valor, bool Mono)>();
        void Fila(string etiqueta, string? valor, bool mono)
        {
            if (!string.IsNullOrWhiteSpace(valor))
            {
                filas.Add((etiqueta, valor.Trim(), mono));
            }
        }

        Fila("Caja", d.Caja, true);
        if (salida)
        {
            Fila("Transportista", d.Transportista, false);
            if (mostrarOperador)
            {
                Fila("Operador", d.Operador, false);
                Fila("Tractor", d.Tractor, true);
                Fila("Sellos", string.Join(" · ", d.Sellos.Split(' ', StringSplitOptions.RemoveEmptyEntries)), true);
            }

            Fila("Salida", d.Salida.HasValue ? FechaCorta(d.Salida.Value) : null, true);
        }
        else
        {
            Fila("Línea", d.Transportista, false);
            Fila("Placas", d.Placas, true);
            Fila("Movimiento", Legible(d.Movimiento), false);
            Fila("Llegada", d.Llegada.HasValue ? FechaCorta(d.Llegada.Value) : null, true);
        }

        var stats = new StringBuilder();
        foreach ((string etiqueta, string valor, bool mono) in filas)
        {
            stats.Append($@"<div class=""stat""><span>{Enc(etiqueta)}</span><b{(mono ? @" class=""mono""" : "")}>{Enc(valor)}</b></div>");
        }

        // Recorrido: solo los pasos de los que hay fecha.
        var pasos = new List<(string Titulo, DateTime Fecha)>();
        if (d.Llegada.HasValue) pasos.Add(("Llegada a caseta", d.Llegada.Value));
        if (salida)
        {
            if (d.Autorizada.HasValue) pasos.Add(("Autorizada para salir", d.Autorizada.Value));
            if (d.Salida.HasValue) pasos.Add(("Salida de caseta", d.Salida.Value));
        }
        else if (d.Registrada.HasValue)
        {
            pasos.Add(("Entrada registrada", d.Registrada.Value));
        }

        string pasosHtml = string.Empty;
        if (pasos.Count >= 2)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < pasos.Count; i++)
            {
                sb.Append($@"<div class=""step {(i == pasos.Count - 1 ? "cur" : "done")}""><b>{Enc(pasos[i].Titulo)}</b><span>{Enc(FechaCorta(pasos[i].Fecha))}</span></div>");
            }

            pasosHtml = $@"<div class=""panel""><div class=""panel__head""><h2>Recorrido</h2><span class=""count"">{pasos.Count} pasos</span></div><div class=""steps"">{sb}</div></div>";
        }

        // Fotos. Cada foto es un enlace real (abre en otra pestaña si no hay JavaScript); el script lo cambia por el visor.
        var tiles = new StringBuilder();
        for (int i = 0; i < total; i++)
        {
            CasetaShareFoto foto = resumen.Fotos[i];
            string hora = SoloHora(foto.Fecha);
            string src = $"{Enc(fotoBaseUrl)}/{foto.IdFotos}";
            tiles.Append($@"<a class=""tile"" href=""{src}"" target=""_blank"" rel=""noreferrer"" aria-label=""Abrir foto {i + 1} de {total}""><img src=""{src}"" alt=""Foto {i + 1} de {Enc(tipoTexto.ToLowerInvariant())}"" loading=""lazy""><span class=""tile__n"">{i + 1}</span>{(hora.Length > 0 ? $@"<span class=""tile__t"">{Enc(hora)}</span>" : "")}<span class=""tile__x"" aria-hidden=""true"">{Icono("expand", 13, 2.2)}</span></a>");
        }

        string zipHtml = Enc(zipUrl);
        string textoBoton = $"Descargar todo (ZIP) <small>· {total} foto{(total == 1 ? "" : "s")}</small>";
        string cta = total == 0 ? string.Empty
            : $@"<div class=""cta""><a class=""btn"" href=""{zipHtml}"">{Icono("download", 16, 2.4)}{textoBoton}</a></div>";
        string dock = total == 0 ? string.Empty
            : $@"<div class=""dock""><a class=""btn"" href=""{zipHtml}"">{Icono("download", 16, 2.4)}{textoBoton}</a></div>";
        string expira = Enc(FechaExpira(resumen.Expira));

        var datos = new StringBuilder();
        datos.Append("var PHOTO_BASE=").Append(JsonSerializer.Serialize(fotoBaseUrl)).Append(";var PHOTOS=")
            .Append(JsonSerializer.Serialize(resumen.Fotos.Select(f => new { id = f.IdFotos, t = f.Fecha }))).Append(';');

        var valores = new Dictionary<string, string>
        {
            ["TITULO"] = Enc(tipoTexto),
            ["CSS"] = Css,
            ["SUBTITULO"] = salida ? "Evidencia de salida de caseta" : "Evidencia de entrada de caseta",
            ["TIPO"] = Enc(tipoTexto),
            ["REFETIQUETA"] = salida ? "Relación" : "Folio de caseta",
            ["REF"] = Enc(resumen.Referencia),
            ["PILLCLASE"] = pillClase,
            ["PILL"] = pillTexto,
            ["FRESCO"] = Enc(fresco),
            ["STATS"] = stats.Length > 0 ? $@"<div class=""stats"">{stats}</div>" : string.Empty,
            ["CTA"] = cta,
            ["EXPIRA"] = expira,
            ["CONTEO"] = total == 0 ? "0 fotos" : $"{total} foto{(total == 1 ? "" : "s")}",
            ["TILES"] = total == 0
                ? $@"<div class=""empty"">{Icono("camera", 34, 1.6)}Todavía no hay fotos de esta {(salida ? "salida" : "entrada")}.</div>"
                : tiles.ToString(),
            ["PASOS"] = pasosHtml,
            ["DOCK"] = dock,
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

    public static string RenderEmailLink(string? url, string tipo, DateTime expira)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        string etiqueta = tipo == CasetaShareService.TipoSalida ? "fotos de salida" : "fotos de entrada";
        string enlace = WebUtility.HtmlEncode(url);
        string vence = WebUtility.HtmlEncode(expira.ToString("dd/MM/yyyy HH:mm"));
        return $@"
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:20px;'>
  <tr><td style='background:#eff6ff;border:1px solid #bfdbfe;border-radius:10px;padding:16px 20px;font-family:Arial,sans-serif;'>
    <div style='font-size:14px;font-weight:bold;color:#1e3a8a;margin-bottom:8px;'>Evidencia fotográfica</div>
    <a href='{enlace}' style='display:inline-block;background:#0b3b7a;color:#ffffff;text-decoration:none;padding:11px 16px;border-radius:7px;font-size:14px;font-weight:bold;'>Ver {etiqueta}</a>
    <div style='font-size:12px;color:#475569;margin-top:9px;'>Enlace vigente hasta {vence}.</div>
  </td></tr>
</table>";
    }

    public static string InsertarEnCorreo(string htmlBody, string bloque)
    {
        if (string.IsNullOrWhiteSpace(bloque))
        {
            return htmlBody;
        }

        int cierreBody = htmlBody.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return cierreBody < 0 ? htmlBody + bloque : htmlBody.Insert(cierreBody, bloque);
    }

    // ── Ayudas ───────────────────────────────────────────────────────────────

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

    private static string FechaCorta(DateTime fecha) => $"{fecha.Day:00}/{fecha.Month:00} · {fecha:HH:mm}";

    private static string FechaExpira(DateTime fecha) => $"{fecha.Day} {Meses[fecha.Month - 1]}, {fecha:HH:mm}";

    /// <summary>De «28/10/2026 14:09:35» deja «14:09:35».</summary>
    private static string SoloHora(string fecha)
    {
        int espacio = fecha.LastIndexOf(' ');
        string hora = espacio >= 0 ? fecha[(espacio + 1)..] : fecha;
        return hora.Contains(':') ? hora : string.Empty;
    }

    /// <summary>«IMPORTACION · COLOCACION/VACIO» pasa a «Importación · Colocación/Vacío».</summary>
    private static string Legible(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return string.Empty;

        static string Palabra(string palabra)
        {
            string minuscula = palabra.Trim().ToLowerInvariant();
            if (minuscula.Length == 0) return minuscula;
            return Acentos.TryGetValue(minuscula, out string? conAcento)
                ? conAcento
                : char.ToUpperInvariant(minuscula[0]) + minuscula[1..];
        }

        return string.Join(" · ", texto.Split(" · ").Select(parte => string.Join("/", parte.Split('/').Select(Palabra))));
    }

    private static string Icono(string nombre, int tamano, double trazo)
    {
        string trazos = nombre switch
        {
            "download" => "<path d=\"M12 4v11M7 11l5 5 5-5M5 20h14\"/>",
            "expand" => "<path d=\"M15 4h5v5M20 4l-7 7M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4\"/>",
            "camera" => "<path d=\"M4 8h3l1.5-2h7L17 8h3v11H4z\"/><circle cx=\"12\" cy=\"13\" r=\"3.5\"/>",
            "clock" => "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 7v5l3 2\"/>",
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
<title>@@TITULO@@ · Grupo PMM</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Manrope:wght@500;700;800&family=IBM+Plex+Sans:wght@400;500;600;700&family=IBM+Plex+Mono:wght@500;600&display=swap" rel="stylesheet">
<style>@@CSS@@</style>
</head>
<body>
<div class="topbar"><div class="topbar__in">
  <div class="mark">PMM</div>
  <div class="brand"><b>Grupo PMM</b><span>@@SUBTITULO@@</span></div>
  <div class="secure"><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 018 0v3"/></svg>Enlace privado</div>
</div></div>
<div class="shell">
  <aside class="summary">
    <div class="summary__head">
      <div class="eyebrow">@@TIPO@@</div>
      <div class="ref"><small>@@REFETIQUETA@@</small>@@REF@@</div>
      <span class="pill @@PILLCLASE@@">@@PILL@@</span>
      <div class="fresh">@@FRESCO@@</div>
    </div>
    @@STATS@@
    @@CTA@@
    <div class="meta">Este enlace expira el <b>@@EXPIRA@@</b>.<br>No responder: para dudas, contacta a tu contacto en PMM.</div>
  </aside>
  <main class="main">
    <div class="panel">
      <div class="panel__head"><h2>Evidencia fotográfica</h2><span class="count">@@CONTEO@@</span></div>
      <div class="grid">@@TILES@@</div>
    </div>
    @@PASOS@@
  </main>
</div>
<div class="foot">Este enlace fue generado automáticamente por PMMovil y expira el <b>@@EXPIRA@@</b>.<br>No responder: para dudas, contacta a tu contacto en PMM.<i>GRUPO PMM · SISTEMA INTERNO</i></div>
@@DOCK@@
<div class="vw" id="vw" role="dialog" aria-modal="true" aria-label="Visor de fotos" aria-hidden="true">
  <div class="vw__top">
    <span class="vw__tag">@@TIPO@@</span><span class="vw__cap" id="vwCap"></span><span class="vw__sp"></span>
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
  <div class="brand"><b>Grupo PMM</b><span>Evidencia de caseta</span></div>
</div></div>
<div class="gone"><div class="gone__c">
  <div class="gone__i">@@RELOJ@@</div>
  <h2>Este enlace ya no está disponible</h2>
  <p>Por seguridad, los enlaces de evidencia dejan de funcionar a los 2 meses. Pide a tu contacto en PMM que te comparta uno nuevo.</p>
</div></div>
</body>
</html>
""";

    // Mismos tokens que Seguimiento de revisión. El diseño es de escritorio con dos columnas y pasa a una sola en <=860px.
    private const string Css = """
:root{
  --blue:#003DA5; --navy:#002680; --blue-pale:#EAF1FF; --blue-line:#CFE0FF;
  --green:#15803D; --green-pale:#EAF9F0;
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
.shell{max-width:1180px;margin:0 auto;padding:36px 40px 40px;display:grid;grid-template-columns:300px 1fr;gap:24px;align-items:start}
.summary{position:sticky;top:24px;background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);box-shadow:var(--shadow);overflow:hidden}
.summary__head{padding:22px 22px 18px;border-bottom:1px solid var(--border)}
.eyebrow{font:600 10.5px var(--f-mono);letter-spacing:.14em;text-transform:uppercase;color:var(--blue)}
.ref{margin-top:8px;font:700 21px var(--f-mono);letter-spacing:-.01em;word-break:break-all}
.ref small{display:block;font:500 11px var(--f-body);color:var(--ink-mute);letter-spacing:0;margin-bottom:2px}
.pill{margin-top:14px;display:inline-flex;align-items:center;gap:7px;padding:7px 14px;border-radius:999px;font-size:12px;font-weight:700;letter-spacing:.02em}
.pill--green{background:var(--green-pale);color:var(--green)}
.pill--blue{background:var(--blue-pale);color:var(--navy)}
.fresh{margin-top:12px;font-size:11.5px;color:var(--ink-mute)}
.stats{padding:18px 22px;display:flex;flex-direction:column;gap:15px}
.stat{display:flex;justify-content:space-between;align-items:baseline;gap:12px}
.stat span{font-size:11.5px;color:var(--ink-soft);flex:none}
.stat b{font-size:13.5px;font-weight:700;text-align:right;min-width:0;overflow-wrap:anywhere}
.cta{padding:0 22px 18px}
.btn{display:flex;align-items:center;justify-content:center;gap:8px;width:100%;background:var(--blue);color:#fff;font-weight:700;font-size:13px;text-decoration:none;padding:12px 16px;border-radius:11px;border:0}
.btn:hover{background:var(--navy)}
.btn small{font-weight:500;opacity:.8;font-size:11.5px}
.meta{padding:14px 22px 20px;font-size:11px;color:var(--ink-mute);line-height:1.6;border-top:1px solid var(--border)}
.main{display:flex;flex-direction:column;gap:22px;min-width:0}
.panel{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);box-shadow:var(--shadow);overflow:hidden}
.panel__head{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:18px 24px;background:var(--blue-pale);border-bottom:1px solid var(--blue-line)}
.panel__head h2{font-size:15px;font-weight:700;color:var(--navy)}
.panel__head .count{font-size:12px;color:var(--ink-soft);font-weight:600}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:12px;padding:18px}
.tile{position:relative;aspect-ratio:4/3;border-radius:12px;border:1px solid var(--border);background:var(--surface-2);overflow:hidden;padding:0;display:block;width:100%;text-decoration:none}
.tile img{width:100%;height:100%;object-fit:cover;display:block;transition:transform .25s}
.tile:hover img,.tile:focus-visible img{transform:scale(1.06)}
.tile__n{position:absolute;top:7px;left:7px;background:rgba(255,255,255,.94);color:var(--navy);font:700 11px var(--f-mono);min-width:22px;height:22px;border-radius:999px;display:grid;place-items:center;padding:0 6px}
.tile__t{position:absolute;inset:auto 0 0 0;padding:16px 8px 6px;background:linear-gradient(transparent,rgba(6,10,22,.82));color:#fff;font:500 10.5px var(--f-mono);text-align:left}
.tile__x{position:absolute;top:7px;right:7px;width:24px;height:24px;border-radius:999px;background:rgba(14,23,41,.55);color:#fff;display:grid;place-items:center;opacity:0;transition:opacity .15s}
.tile:hover .tile__x,.tile:focus-visible .tile__x{opacity:1}
.empty{grid-column:1/-1;padding:46px 20px;text-align:center;color:var(--ink-mute);font-size:13px}
.empty svg{display:block;margin:0 auto 10px}
.steps{display:flex;padding:24px 24px 26px;gap:0}
.step{flex:1;position:relative;padding-top:28px;min-width:0}
.step::before{content:"";position:absolute;top:6px;left:0;width:14px;height:14px;border-radius:50%;background:#fff;border:3px solid #B9C4DD;z-index:1}
.step::after{content:"";position:absolute;top:12px;left:14px;right:0;height:2px;background:#D5DCEC}
.step:last-child::after{display:none}
.step.done::before{background:var(--blue);border-color:var(--blue)}
.step.done::after{background:var(--blue)}
.step.cur::before{background:var(--green);border-color:#BBF7D0;box-shadow:0 0 0 3px var(--green-pale)}
.step b{display:block;font-size:12.5px;font-weight:700;padding-right:10px}
.step span{display:block;font:500 11.5px var(--f-mono);color:var(--ink-soft);margin-top:2px}
.foot{max-width:1180px;margin:0 auto;padding:0 40px 36px;text-align:center;font-size:11px;line-height:1.6;color:var(--ink-mute)}
.foot i{display:block;margin-top:12px;font-style:normal;font-size:10px;letter-spacing:.05em}
.dock{display:none}
.gone{max-width:520px;margin:0 auto;padding:60px 20px}
.gone__c{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:40px 32px;text-align:center;box-shadow:var(--shadow)}
.gone__i{width:56px;height:56px;border-radius:50%;background:var(--blue-pale);display:grid;place-items:center;margin:0 auto 16px}
.gone h2{font-size:18px;font-weight:700}
.gone p{font-size:13.5px;color:var(--ink-soft);line-height:1.65;margin:12px auto 0;max-width:38ch}
.vw{position:fixed;inset:0;z-index:100;background:rgba(6,10,22,.97);display:none;flex-direction:column;color:#fff}
.vw.show{display:flex}
.vw__top{display:flex;align-items:center;gap:12px;padding:14px 18px}
.vw__tag{font:700 11px var(--f-body);letter-spacing:.05em;text-transform:uppercase;padding:5px 12px;border-radius:999px;background:rgba(255,255,255,.14);flex:none}
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
.vw__thumbs button[aria-current="true"]{border-color:#fff;opacity:1}
.vw__thumbs img{width:100%;height:100%;object-fit:cover;display:block}
.vw__dl{display:inline-flex;gap:8px;align-items:center;background:#fff;color:var(--ink);font-weight:700;font-size:12.5px;text-decoration:none;padding:10px 18px;border-radius:999px}
@media (min-width:1000px){.grid{grid-template-columns:repeat(auto-fill,minmax(170px,1fr))}}
@media (max-width:860px){
  .topbar{padding:14px 18px}
  .secure{display:none}
  .shell{grid-template-columns:1fr;padding:0 0 28px;gap:0}
  .summary{position:static;border-radius:0;border-left:0;border-right:0;margin-top:-1px}
  .main{padding:16px;gap:16px}
  .grid{grid-template-columns:repeat(2,1fr);padding:14px;gap:10px}
  .steps{flex-direction:column;padding:20px 24px}
  .step{padding:0 0 22px 32px;flex:none}
  .step:last-child{padding-bottom:0}
  .step::before{top:2px}
  .step::after{top:18px;left:6px;right:auto;width:2px;height:calc(100% - 12px)}
  .foot{padding:0 16px 28px}
  .cta{display:none}
  .dock{display:flex;position:sticky;bottom:0;z-index:5;gap:10px;align-items:center;background:rgba(255,255,255,.96);border-top:1px solid var(--border);padding:12px 16px calc(12px + env(safe-area-inset-bottom,0px))}
  .dock .btn{flex:1}
  .vw__stage{padding:0 10px 8px}
  .vw__nav{width:38px;height:38px}
  .vw__nav--p{left:6px}
  .vw__nav--n{right:6px}
  .vw__thumbs button{width:52px;height:40px}
}
@media (prefers-reduced-motion:reduce){*{transition:none!important}}
@media print{.dock,.vw,.tile__x,.cta{display:none!important}.summary{position:static}}
""";

    // El visor: flechas, teclado, deslizar, miniaturas y acercar donde se toca. Sin JavaScript cada foto abre en otra pestaña.
    private const string Js = """
(function () {
  var vw = document.getElementById('vw');
  if (!vw || !window.PHOTOS || !PHOTOS.length) return;
  var img = document.getElementById('vwImg'), cap = document.getElementById('vwCap'),
      thumbs = document.getElementById('vwThumbs'), dl = document.getElementById('vwDl'),
      closeBtn = document.getElementById('vwClose'), stage = document.getElementById('vwStage'),
      tiles = Array.prototype.slice.call(document.querySelectorAll('.tile'));
  var cur = 0, zoomed = false, opener = null, thumbsReady = false, startX = null;

  function url(i) { return PHOTO_BASE + '/' + PHOTOS[i].id; }

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
    setZoom(false);
    img.src = url(cur);
    img.alt = 'Foto ' + (cur + 1) + ' de ' + PHOTOS.length;
    cap.textContent = 'Foto ' + (cur + 1) + ' de ' + PHOTOS.length + (PHOTOS[cur].t ? ' · ' + PHOTOS[cur].t : '');
    dl.href = url(cur);
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
""";
}
