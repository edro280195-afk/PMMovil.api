using System.Net;
using System.Text;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Arma el HTML de la página pública de "Seguimiento de entrada" -- mismo
/// diseño aprobado en la maqueta (fijo en claro, sin tema oscuro, marca PMM).
/// Todo texto que viene de la base de datos se escapa: esta página la puede
/// abrir cualquiera con el link, nunca confiar en el contenido como si ya
/// fuera HTML seguro.
/// </summary>
public static class EntradaShareHtmlRenderer
{
    public static string RenderResumen(EntradaShareResumen r, string fotoBaseUrl)
    {
        string cliente = Enc(r.NombreCliente.Length > 0 ? r.NombreCliente : "Sin información");
        string transportista = Enc(r.NombreTransportista.Length > 0 ? r.NombreTransportista : "Sin información");
        string unidad = Enc(r.Unidad.Length > 0 ? r.Unidad : "—");
        string ubicacion = Enc(r.Ubicacion.Length > 0 ? r.Ubicacion : "—");
        string fecha = Enc(r.FechaLlegada ?? "—");
        string cref = Enc(r.CrefAgrupada);

        var photosHtml = new StringBuilder();
        foreach (EntradaShareFoto foto in r.Fotos)
        {
            string caption = Enc(foto.Caption ?? "Foto");
            string src = $"{fotoBaseUrl}/{foto.NLineNo}";
            photosHtml.Append($@"
              <div class=""photo-tile"" onclick=""openLightbox({foto.NLineNo})"">
                <img src=""{src}"" alt=""{caption}"" loading=""lazy"">
                <div class=""photo-tile__caption"">{caption}</div>
              </div>");
        }

        string minutosDesde = MinutosDesde(r.GeneradoEn);
        string expiraTexto = r.Expira.ToString("d MMM, HH:mm");

        return $@"<!doctype html>
<html lang=""es"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Seguimiento de entrada {cref}</title>
<link rel=""preconnect"" href=""https://fonts.googleapis.com"">
<link rel=""preconnect"" href=""https://fonts.gstatic.com"" crossorigin>
<link href=""https://fonts.googleapis.com/css2?family=Manrope:wght@500;700;800&family=IBM+Plex+Sans:wght@400;500;600;700&family=IBM+Plex+Mono:wght@500;600&display=swap"" rel=""stylesheet"">
<style>{Css}</style>
</head>
<body>
<div class=""topbar"">
  <div class=""brand-mark"">PMM</div>
  <div>
    <div class=""brand-name"">Grupo PMM</div>
    <div class=""brand-sub"">Seguimiento de entrada</div>
  </div>
</div>
<div class=""shell"">
  <div class=""card"">
    <div class=""card__head"">
      <div class=""eyebrow"">Entrada</div>
      <div class=""ref-row"">
        <div class=""ref-number"">{cref}</div>
      </div>
      <div class=""freshness"">Generado hace {minutosDesde}</div>
    </div>
    <div class=""facts"">
      <div><div class=""fact-label"">Cliente</div><div class=""fact-value"">{cliente}</div></div>
      <div><div class=""fact-label"">Transportista</div><div class=""fact-value"">{transportista}</div></div>
      <hr>
      <div><div class=""fact-label"">Unidad</div><div class=""fact-value mono"">{unidad}</div></div>
      <div><div class=""fact-label"">Ubicación</div><div class=""fact-value"">{ubicacion}</div></div>
      <hr>
      <div><div class=""fact-label"">Llegada</div><div class=""fact-value mono"">{fecha}</div></div>
    </div>
  </div>

  <div class=""card"">
    <div class=""section-title""><h2>Fotos de la entrada</h2><span>{r.Fotos.Count} fotos</span></div>
    <div class=""photo-grid"">{(r.Fotos.Count > 0 ? photosHtml.ToString() : "<div class=\"empty-photos\">Todavía no hay fotos cargadas.</div>")}</div>
  </div>

  <div class=""footer-note"">
    Este enlace fue generado automáticamente por PMMovil y expira el <b>{expiraTexto}</b>.<br>
    No responder — para dudas, contacta a tu contacto en PMM.
    <div class=""footer-brand"">GRUPO PMM · SISTEMA INTERNO</div>
  </div>
</div>

<div class=""lightbox"" id=""lightbox"" onclick=""closeLightbox(event)"">
  <img id=""lightboxImg"" src="""" alt="""">
</div>

<script>
  const photoBase = ""{fotoBaseUrl}"";
  function openLightbox(linea) {{
    document.getElementById('lightboxImg').src = photoBase + '/' + linea;
    document.getElementById('lightbox').classList.add('show');
  }}
  function closeLightbox(e) {{
    if (e.target.id === 'lightbox' || e.target.id === 'lightboxImg') {{
      document.getElementById('lightbox').classList.remove('show');
    }}
  }}
</script>
</body>
</html>";
    }

    public static string RenderExpirado()
    {
        return $@"<!doctype html>
<html lang=""es"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Enlace no disponible</title>
<link rel=""preconnect"" href=""https://fonts.googleapis.com"">
<link rel=""preconnect"" href=""https://fonts.gstatic.com"" crossorigin>
<link href=""https://fonts.googleapis.com/css2?family=Manrope:wght@500;700;800&family=IBM+Plex+Sans:wght@400;500;600;700&display=swap"" rel=""stylesheet"">
<style>{Css}</style>
</head>
<body>
<div class=""topbar"">
  <div class=""brand-mark"">PMM</div>
  <div>
    <div class=""brand-name"">Grupo PMM</div>
    <div class=""brand-sub"">Seguimiento de entrada</div>
  </div>
</div>
<div class=""shell"">
  <div class=""expired-card"">
    <h2>Este enlace ya no está disponible</h2>
    <p>Por seguridad, los enlaces de seguimiento dejan de funcionar después de cierto tiempo. Pide a tu contacto en PMM que te comparta uno nuevo.</p>
  </div>
</div>
</body>
</html>";
    }

    private static string Enc(string value) => WebUtility.HtmlEncode(value);

    private static string MinutosDesde(DateTime desde)
    {
        TimeSpan diff = DateTime.Now - desde;
        if (diff.TotalMinutes < 1) return "un momento";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} h";
        return $"{(int)diff.TotalDays} días";
    }

    private const string Css = @"
        :root{
          --pmm-blue-deep:#003DA5; --pmm-navy-dark:#002680; --pmm-green:#16A34A;
          --bg:#EEF1F8; --surface:#FFFFFF; --surface-2:#F5F8FC;
          --ink:#0E1729; --ink-soft:#47506B; --ink-mute:#8A93AC; --border:#DEE4F0;
          --shadow:rgba(16,40,90,0.10);
        }
        *{box-sizing:border-box;}
        body{background:var(--bg);color:var(--ink);font-family:'IBM Plex Sans',system-ui,sans-serif;margin:0;padding-bottom:40px;-webkit-font-smoothing:antialiased;}
        .mono{font-family:'IBM Plex Mono',monospace;font-variant-numeric:tabular-nums;}
        h2{font-family:'Manrope',system-ui,sans-serif;margin:0;}
        .topbar{background:linear-gradient(155deg,var(--pmm-blue-deep),var(--pmm-navy-dark));padding:16px clamp(16px,4vw,32px);display:flex;align-items:center;gap:12px;}
        .brand-mark{width:34px;height:34px;border-radius:9px;background:#fff;display:flex;align-items:center;justify-content:center;font-family:'Manrope',sans-serif;font-weight:800;font-size:11px;color:var(--pmm-blue-deep);flex:0 0 auto;}
        .brand-name{color:#fff;font-weight:700;font-size:14px;}
        .brand-sub{color:rgba(255,255,255,.62);font-size:11px;margin-top:1px;}
        .shell{max-width:560px;margin:0 auto;padding:0 clamp(14px,4vw,20px);}
        .card{background:var(--surface);border:1px solid var(--border);border-radius:18px;box-shadow:0 2px 10px var(--shadow);margin-top:-18px;position:relative;z-index:2;overflow:hidden;}
        .card + .card{margin-top:16px;}
        .card__head{padding:20px 20px 4px;}
        .eyebrow{font-family:'IBM Plex Mono',monospace;font-size:10.5px;font-weight:600;letter-spacing:.12em;text-transform:uppercase;color:var(--pmm-blue-deep);}
        .ref-row{display:flex;align-items:baseline;gap:10px;flex-wrap:wrap;margin-top:6px;}
        .ref-number{font-size:22px;font-weight:700;font-family:'IBM Plex Mono',monospace;letter-spacing:-.01em;}
        .freshness{margin-top:10px;font-size:11.5px;color:var(--ink-mute);}
        .facts{padding:18px 20px 20px;display:grid;grid-template-columns:1fr 1fr;gap:14px 16px;}
        .fact-label{font-size:10px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:var(--ink-mute);}
        .fact-value{font-size:13.5px;font-weight:600;color:var(--ink);margin-top:3px;}
        .facts hr{grid-column:1/-1;border:none;border-top:1px solid var(--border);margin:2px 0;}
        .section-title{display:flex;align-items:center;justify-content:space-between;padding:18px 20px 10px;}
        .section-title h2{font-size:14.5px;font-weight:700;}
        .section-title span{font-size:11.5px;color:var(--ink-mute);}
        .photo-grid{display:grid;grid-template-columns:1fr 1fr;gap:9px;padding:0 20px 20px;}
        .photo-tile{aspect-ratio:4/3;border-radius:13px;border:1px solid var(--border);background:var(--surface-2);position:relative;overflow:hidden;cursor:pointer;}
        .photo-tile img{width:100%;height:100%;object-fit:cover;display:block;}
        .photo-tile__caption{position:absolute;left:0;right:0;bottom:0;padding:8px 10px;background:linear-gradient(to top, rgba(0,0,0,.55), transparent);color:#fff;font-size:10.5px;font-weight:600;}
        .empty-photos{grid-column:1/-1;padding:24px;text-align:center;color:var(--ink-mute);font-size:12.5px;}
        .footer-note{padding:20px 6px 0;font-size:11px;line-height:1.6;color:var(--ink-mute);text-align:center;}
        .footer-brand{margin-top:14px;font-size:10px;color:var(--ink-mute);letter-spacing:.04em;}
        .expired-card{background:var(--surface);border:1px solid var(--border);border-radius:18px;padding:40px 26px;text-align:center;margin-top:-18px;position:relative;z-index:2;box-shadow:0 2px 10px var(--shadow);}
        .expired-card h2{font-size:17px;font-weight:700;}
        .expired-card p{font-size:13px;color:var(--ink-soft);line-height:1.6;margin:10px auto 0;max-width:34ch;}
        .lightbox{position:fixed;inset:0;z-index:100;background:rgba(6,10,22,.9);display:flex;align-items:center;justify-content:center;opacity:0;pointer-events:none;transition:opacity .2s;padding:24px;}
        .lightbox.show{opacity:1;pointer-events:auto;}
        .lightbox img{max-width:100%;max-height:100%;border-radius:12px;}
    ";
}
