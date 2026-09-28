using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>
/// Arma el HTML de la página pública de "Seguimiento de revisión". Rediseñada
/// 2026-08-26: layout de escritorio (dos columnas: resumen fijo a la
/// izquierda + contenido a la derecha) que colapsa a una sola columna en
/// pantallas angostas -- ya NO es mobile-first. Evidencia normal y de
/// discrepancia se muestran en paneles con su propio color (azul/rojo) y
/// comparten un visor tipo carrusel (flechas, contador, atajos de teclado).
/// Todo texto que viene de la base de datos se escapa: la puede abrir
/// cualquiera con el link.
/// </summary>
public static class RevisionShareHtmlRenderer
{
    public static string RenderResumen(RevisionShareResumen r, string fotoBaseUrl, string zipUrl)
    {
        string cliente = Enc(r.NombreCliente.Length > 0 ? r.NombreCliente : "Sin información");
        string cref = Enc(r.CrefAgrupada);

        // fotoBaseUrl/zipUrl traen el token de la URL tal cual llegó -- a
        // diferencia del resto de los valores de esta página, NO vienen de
        // la base de datos, así que no basta con el mismo Enc() de siempre:
        // se necesita codificación por contexto (atributo HTML vs. cadena
        // JS). Hoy no es explotable (el token solo hace match si es
        // EXACTAMENTE el GUID de 32 hex que generó el servidor, sin
        // metacaracteres), pero se codifica de todas formas para no
        // depender de eso.
        string fotoBaseUrlHtml = Enc(fotoBaseUrl);
        string fotoBaseUrlJs = JsonSerializer.Serialize(fotoBaseUrl);
        string zipUrlHtml = Enc(zipUrl);

        bool algunaDiscrepancia = r.Facturas.Any(f => f.StatusRev == 2);
        (string Texto, string Clase) estadoGeneral = r.TotalFacturas == 0
            ? ("Sin facturas", "status-pill--gray")
            : algunaDiscrepancia
                ? ("⚠ Discrepancia detectada", "status-pill--red")
                : r.FacturasCompletas == r.TotalFacturas
                    ? ("✓ Revisión completa", "status-pill--green")
                    : ($"{r.TotalFacturas - r.FacturasCompletas} factura{(r.TotalFacturas - r.FacturasCompletas == 1 ? "" : "s")} pendiente{(r.TotalFacturas - r.FacturasCompletas == 1 ? "" : "s")}", "status-pill--amber");

        var filasTabla = new StringBuilder();
        var tarjetasMovil = new StringBuilder();
        foreach (RevisionShareFactura f in r.Facturas)
        {
            (string label, string cls) = EstatusFactura(f.StatusRev);
            string revisor = Enc(f.Revisor.Length > 0 ? f.Revisor : "Sin asignar");
            string discrepClass = f.StatusRev == 2 ? " is-discrepancia" : "";
            string discrepanciaTexto = Enc(f.Discrepancias.Trim());

            filasTabla.Append($@"
              <tr class=""{discrepClass.Trim()}"">
                <td class=""mono"">{Enc(f.Cnofact)}</td>
                <td>{revisor}</td>
                <td class=""mono"">{f.TotBultos}/{f.TotBultosDoc}</td>
                <td><span class=""pill {cls}"">{label}</span></td>
              </tr>");

            tarjetasMovil.Append($@"
              <div class=""invoice-row{discrepClass}"">
                <div class=""invoice-row__top"">
                  <span class=""invoice-row__num mono"">{Enc(f.Cnofact)}</span>
                  <span class=""pill {cls}"">{label}</span>
                </div>
                <div class=""invoice-row__facts"">
                  <span>Revisor: <b>{revisor}</b></span>
                  <span>Bultos: <b class=""mono"">{f.TotBultos}/{f.TotBultosDoc}</b></span>
                </div>{(discrepanciaTexto.Length > 0 ? $@"
                <div class=""invoice-row__discrepancia"">{discrepanciaTexto}</div>" : "")}
              </div>");
        }

        // El visor/carrusel navega TODAS las fotos en un solo arreglo -- normales
        // primero, discrepancia después -- así que el índice global de cada
        // tarjeta se calcula aquí para que el onclick abra la posición correcta.
        int totalFotos = r.FotosNormales.Count + r.FotosDiscrepancia.Count;

        string BuildPhotoGrid(List<RevisionShareFoto> fotos, int offset, bool esDiscrepancia)
        {
            var html = new StringBuilder();
            for (int i = 0; i < fotos.Count; i++)
            {
                int globalIndex = offset + i;
                string fechaRegistro = string.IsNullOrWhiteSpace(fotos[i].FCrea)
                    ? string.Empty
                    : $@"<span class=""photo-tile__meta"">Registrada: {Enc(fotos[i].FCrea)}</span>";
                html.Append($@"
              <div class=""photo-tile{(esDiscrepancia ? " photo-tile--discrepancia" : "")}"" onclick=""openViewer({globalIndex})"">
                {(esDiscrepancia ? "<span class=\"photo-tile__badge\">Discrepancia</span>" : "")}
                <img src=""{fotoBaseUrlHtml}/{fotos[i].IdFotos}"" alt=""{(esDiscrepancia ? "Discrepancia" : "Evidencia")}"" loading=""lazy"">
                {fechaRegistro}
              </div>");
            }
            return html.ToString();
        }

        string gridNormalesHtml = BuildPhotoGrid(r.FotosNormales, 0, false);
        string gridDiscrepanciaHtml = BuildPhotoGrid(r.FotosDiscrepancia, r.FotosNormales.Count, true);

        // Arreglo JS plano para el visor: mismo orden que los offsets de arriba.
        var fotosJs = new StringBuilder("[");
        foreach (RevisionShareFoto f in r.FotosNormales)
        {
            fotosJs.Append($"{{id:{f.IdFotos},tipo:'normal'}},");
        }
        foreach (RevisionShareFoto f in r.FotosDiscrepancia)
        {
            fotosJs.Append($"{{id:{f.IdFotos},tipo:'discrepancia'}},");
        }
        fotosJs.Append(']');

        string minutosDesde = MinutosDesde(r.GeneradoEn);
        string expiraTexto = r.Expira.ToString("d MMM, HH:mm");

        string ctaZipHtml = totalFotos > 0
            ? $@"<div class=""summary__cta""><a href=""{zipUrlHtml}"" class=""btn-primary"">⬇ Descargar todo (ZIP)</a></div>"
            : "";

        return $@"<!doctype html>
<html lang=""es"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Seguimiento de revisión {cref}</title>
<link rel=""preconnect"" href=""https://fonts.googleapis.com"">
<link rel=""preconnect"" href=""https://fonts.gstatic.com"" crossorigin>
<link href=""https://fonts.googleapis.com/css2?family=Manrope:wght@500;700;800&family=IBM+Plex+Sans:wght@400;500;600;700&family=IBM+Plex+Mono:wght@500;600&display=swap"" rel=""stylesheet"">
<style>{Css}</style>
</head>
<body>
<div class=""topbar""><div class=""topbar__inner"">
  <div class=""brand-mark"">PMM</div>
  <div>
    <div class=""brand-name"">Grupo PMM</div>
    <div class=""brand-sub"">Seguimiento de revisión</div>
  </div>
</div></div>

<div class=""shell"">

  <aside class=""summary"">
    <div class=""summary__head"">
      <div class=""eyebrow"">Revisión</div>
      <div class=""ref-number"">{cref}</div>
      <span class=""status-pill {estadoGeneral.Clase}"">{estadoGeneral.Texto}</span>
      <div class=""freshness"">Generado hace {minutosDesde}</div>
    </div>
    <div class=""summary__stats"">
      <div class=""stat-row""><span class=""stat-label"">Cliente</span><span class=""stat-value"">{cliente}</span></div>
      <div class=""stat-row""><span class=""stat-label"">Facturas completas</span><span class=""stat-value mono"">{r.FacturasCompletas}/{r.TotalFacturas}</span></div>
      <div class=""stat-row""><span class=""stat-label"">Partidas revisadas</span><span class=""stat-value mono"">{r.PartidasRevisadas}/{r.TotalPartidas}</span></div>
    </div>
    {ctaZipHtml}
    <div class=""summary__meta"">Este enlace expira el <b>{expiraTexto}</b>.<br>No responder — contacta a tu contacto en PMM.</div>
  </aside>

  <div class=""main"">

    <div class=""panel"">
      <div class=""panel__head""><h2>Facturas</h2><span class=""count"">{r.Facturas.Count}</span></div>
      {(r.Facturas.Count > 0 ? $@"
      <table class=""invoice-table"">
        <thead><tr><th>Factura</th><th>Revisor</th><th>Bultos</th><th>Estatus</th></tr></thead>
        <tbody>{filasTabla}</tbody>
      </table>
      <div class=""invoice-cards"">{tarjetasMovil}</div>" : @"<div class=""empty-state"">Todavía no hay facturas.</div>")}
    </div>

    <div class=""evidence-grid"">
      <div class=""evidence-panel evidence-panel--normal panel"">
        <div class=""panel__head""><h2>Evidencia normal</h2><span class=""count"">{r.FotosNormales.Count} foto{(r.FotosNormales.Count == 1 ? "" : "s")}</span></div>
        <div class=""photo-grid"">{(r.FotosNormales.Count > 0 ? gridNormalesHtml : @"<div class=""empty-state"">Todavía no hay fotos de evidencia.</div>")}</div>
      </div>
      <div class=""evidence-panel evidence-panel--discrepancia panel"">
        <div class=""panel__head""><h2>⚠ Discrepancia</h2><span class=""count"">{r.FotosDiscrepancia.Count} foto{(r.FotosDiscrepancia.Count == 1 ? "" : "s")}</span></div>
        <div class=""photo-grid"">{(r.FotosDiscrepancia.Count > 0 ? gridDiscrepanciaHtml : @"<div class=""empty-state"">Sin discrepancias reportadas.</div>")}</div>
      </div>
    </div>

  </div>
</div>

<div class=""footer-note"">
  Este enlace fue generado automáticamente por PMMovil y expira el <b>{expiraTexto}</b>.<br>
  No responder — para dudas, contacta a tu contacto en PMM.
  <div class=""footer-brand"">GRUPO PMM · SISTEMA INTERNO</div>
</div>

<div class=""viewer"" id=""viewer"">
  <div class=""viewer__top"">
    <span class=""viewer__tag"" id=""viewerTag"">Evidencia</span>
    <span class=""viewer__counter"" id=""viewerCounter"">1 / {Math.Max(totalFotos, 1)}</span>
    <button class=""viewer__close"" onclick=""closeViewer()"">✕</button>
  </div>
  <div class=""viewer__stage"">
    <button class=""viewer__nav viewer__nav--prev"" onclick=""navViewer(-1)"">‹</button>
    <img id=""viewerImg"" src="""" alt="""">
    <button class=""viewer__nav viewer__nav--next"" onclick=""navViewer(1)"">›</button>
  </div>
  <div class=""viewer__bottom"">
    <a id=""viewerDownload"" class=""viewer__download"" href=""#"" download onclick=""event.stopPropagation()"">⬇ Descargar esta foto</a>
  </div>
</div>

<script>
  const photoBase = {fotoBaseUrlJs};
  const fotos = {fotosJs};
  let currentIndex = 0;

  function openViewer(i) {{
    currentIndex = i;
    updateViewer();
    document.getElementById('viewer').classList.add('show');
  }}
  function closeViewer() {{
    document.getElementById('viewer').classList.remove('show');
  }}
  function navViewer(delta) {{
    if (fotos.length === 0) return;
    currentIndex = (currentIndex + delta + fotos.length) % fotos.length;
    updateViewer();
  }}
  function updateViewer() {{
    const foto = fotos[currentIndex];
    if (!foto) return;
    const url = photoBase + '/' + foto.id;
    document.getElementById('viewerImg').src = url;
    document.getElementById('viewerDownload').href = url;
    document.getElementById('viewerCounter').textContent = (currentIndex + 1) + ' / ' + fotos.length;
    const tag = document.getElementById('viewerTag');
    tag.textContent = foto.tipo === 'discrepancia' ? '⚠ Discrepancia' : 'Evidencia';
    tag.className = 'viewer__tag' + (foto.tipo === 'discrepancia' ? ' viewer__tag--discrepancia' : '');
  }}
  document.addEventListener('keydown', function (e) {{
    if (!document.getElementById('viewer').classList.contains('show')) return;
    if (e.key === 'ArrowLeft') navViewer(-1);
    if (e.key === 'ArrowRight') navViewer(1);
    if (e.key === 'Escape') closeViewer();
  }});
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
<div class=""topbar""><div class=""topbar__inner"">
  <div class=""brand-mark"">PMM</div>
  <div>
    <div class=""brand-name"">Grupo PMM</div>
    <div class=""brand-sub"">Seguimiento de revisión</div>
  </div>
</div></div>
<div class=""expired-shell"">
  <div class=""expired-card"">
    <h2>Este enlace ya no está disponible</h2>
    <p>Por seguridad, los enlaces de seguimiento dejan de funcionar después de cierto tiempo. Pide a tu contacto en PMM que te comparta uno nuevo.</p>
  </div>
</div>
</body>
</html>";
    }

    private static (string Label, string CssClass) EstatusFactura(int statusRev) => statusRev switch
    {
        1 => ("Completa", "pill--green"),
        2 => ("Diferencia", "pill--red"),
        3 => ("En proceso", "pill--amber"),
        _ => ("Pendiente", "pill--gray")
    };

    private static string Enc(string value) => WebUtility.HtmlEncode(value);

    private static string MinutosDesde(DateTime desde)
    {
        TimeSpan diff = DateTime.Now - desde;
        if (diff.TotalMinutes < 1) return "un momento";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} h";
        return $"{(int)diff.TotalDays} días";
    }

    // Rediseño 2026-08-26 -- layout de escritorio (dos columnas) que colapsa a
    // una sola en <=860px, NO mobile-first. Mismos tokens de marca PMM que
    // EntradaShareHtmlRenderer, más los propios de esta página (paneles de
    // evidencia por color, tabla de facturas, visor/carrusel a pantalla completa).
    private const string Css = @"
        :root{
          --pmm-blue-deep:#003DA5; --pmm-navy-dark:#002680; --pmm-blue-pale:#EAF1FF; --pmm-blue-line:#CFE0FF;
          --pmm-green:#16A34A; --pmm-green-pale:#EAF9F0;
          --pmm-red:#DC2626; --pmm-red-pale:#FDECEC; --pmm-red-line:#F6C8C8; --pmm-red-chip:#FBDADA;
          --pmm-amber:#D97706; --pmm-amber-pale:#FEF3E2;
          --bg:#EDF1F8; --surface:#FFFFFF; --surface-2:#F6F8FC;
          --ink:#0E1729; --ink-soft:#47506B; --ink-mute:#8A93AC; --border:#E1E6F0;
          --shadow:0 1px 2px rgba(16,40,90,.06), 0 8px 24px rgba(16,40,90,.06);
          --radius:16px;
        }
        *{box-sizing:border-box;}
        html,body{margin:0;padding:0;}
        body{background:var(--bg);color:var(--ink);font-family:'IBM Plex Sans',system-ui,sans-serif;-webkit-font-smoothing:antialiased;}
        .mono{font-family:'IBM Plex Mono',monospace;font-variant-numeric:tabular-nums;}
        h1,h2,h3{font-family:'Manrope',system-ui,sans-serif;margin:0;}
        a{color:inherit;}

        .topbar{background:linear-gradient(160deg,var(--pmm-blue-deep),var(--pmm-navy-dark));padding:18px 40px;}
        .topbar__inner{max-width:1180px;margin:0 auto;display:flex;align-items:center;gap:14px;}
        .brand-mark{width:38px;height:38px;border-radius:10px;background:#fff;display:flex;align-items:center;justify-content:center;font-family:'Manrope',sans-serif;font-weight:800;font-size:12px;color:var(--pmm-blue-deep);flex:0 0 auto;}
        .brand-name{color:#fff;font-weight:700;font-size:15px;}
        .brand-sub{color:rgba(255,255,255,.62);font-size:11.5px;margin-top:2px;}

        .shell{max-width:1180px;margin:0 auto;padding:36px 40px 60px;display:grid;grid-template-columns:300px 1fr;gap:24px;align-items:start;}

        .summary{position:sticky;top:24px;background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);box-shadow:var(--shadow);overflow:hidden;}
        .summary__head{padding:22px 22px 18px;border-bottom:1px solid var(--border);}
        .eyebrow{font-family:'IBM Plex Mono',monospace;font-size:10.5px;font-weight:600;letter-spacing:.14em;text-transform:uppercase;color:var(--pmm-blue-deep);}
        .ref-number{margin-top:8px;font-size:21px;font-weight:700;font-family:'IBM Plex Mono',monospace;letter-spacing:-.01em;word-break:break-all;}
        .status-pill{margin-top:14px;display:inline-flex;align-items:center;gap:7px;padding:7px 14px;border-radius:999px;font-size:12px;font-weight:700;letter-spacing:.02em;}
        .status-pill--green{background:var(--pmm-green-pale);color:var(--pmm-green);}
        .status-pill--red{background:var(--pmm-red-pale);color:var(--pmm-red);}
        .status-pill--amber{background:var(--pmm-amber-pale);color:var(--pmm-amber);}
        .status-pill--gray{background:#E7EAF2;color:var(--ink-mute);}
        .freshness{margin-top:12px;font-size:11.5px;color:var(--ink-mute);}
        .summary__stats{padding:18px 22px;display:flex;flex-direction:column;gap:16px;}
        .stat-row{display:flex;justify-content:space-between;align-items:baseline;gap:10px;}
        .stat-label{font-size:11.5px;color:var(--ink-soft);}
        .stat-value{font-size:15px;font-weight:700;text-align:right;}
        .summary__cta{padding:0 22px 18px;}
        .btn-primary{display:flex;align-items:center;justify-content:center;gap:8px;width:100%;background:var(--pmm-blue-deep);color:#fff;font-weight:700;font-size:13px;text-decoration:none;padding:12px 16px;border-radius:11px;transition:background .15s;}
        .btn-primary:hover{background:var(--pmm-navy-dark);}
        .summary__meta{padding:14px 22px 20px;font-size:11px;color:var(--ink-mute);line-height:1.6;border-top:1px solid var(--border);}

        .main{display:flex;flex-direction:column;gap:22px;min-width:0;}
        .panel{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);box-shadow:var(--shadow);overflow:hidden;}
        .panel__head{display:flex;align-items:center;justify-content:space-between;padding:20px 24px;border-bottom:1px solid var(--border);}
        .panel__head h2{font-size:15px;font-weight:700;}
        .panel__head .count{font-size:12px;color:var(--ink-mute);font-weight:600;}

        .invoice-table{width:100%;border-collapse:collapse;}
        .invoice-table th{text-align:left;font-size:10.5px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;color:var(--ink-mute);padding:12px 24px;background:var(--surface-2);}
        .invoice-table td{padding:14px 24px;font-size:13px;border-top:1px solid var(--border);}
        .invoice-table tr.is-discrepancia td{background:var(--pmm-red-pale);}
        .pill{display:inline-flex;align-items:center;padding:4px 11px;border-radius:999px;font-size:10.5px;font-weight:700;letter-spacing:.02em;}
        .pill--green{background:var(--pmm-green-pale);color:var(--pmm-green);}
        .pill--red{background:var(--pmm-red-chip);color:var(--pmm-red);}
        .pill--amber{background:var(--pmm-amber-pale);color:var(--pmm-amber);}
        .pill--gray{background:#E7EAF2;color:var(--ink-mute);}
        .invoice-cards{display:none;}

        .evidence-grid{display:grid;grid-template-columns:1fr 1fr;gap:22px;}
        .evidence-panel--normal .panel__head{background:var(--pmm-blue-pale);border-bottom-color:var(--pmm-blue-line);}
        .evidence-panel--normal .panel__head h2{color:var(--pmm-navy-dark);}
        .evidence-panel--discrepancia .panel__head{background:var(--pmm-red-pale);border-bottom-color:var(--pmm-red-line);}
        .evidence-panel--discrepancia .panel__head h2{color:var(--pmm-red);}
        .evidence-panel--discrepancia .panel__head .count{color:var(--pmm-red);}
        .photo-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(120px,1fr));gap:10px;padding:18px;}
        .photo-tile{aspect-ratio:4/3;border-radius:11px;border:1px solid var(--border);background:var(--surface-2);position:relative;overflow:hidden;cursor:pointer;}
        .photo-tile img{width:100%;height:100%;object-fit:cover;display:block;transition:transform .2s;}
        .photo-tile:hover img{transform:scale(1.06);}
        .photo-tile--discrepancia{border-color:var(--pmm-red-line);}
        .photo-tile__badge{position:absolute;top:6px;left:6px;z-index:1;background:rgba(220,38,38,.92);color:#fff;font-size:9px;font-weight:700;letter-spacing:.04em;text-transform:uppercase;padding:3px 7px;border-radius:999px;}
        .photo-tile__meta{position:absolute;right:0;bottom:0;left:0;z-index:1;padding:5px 7px;background:linear-gradient(transparent,rgba(6,10,22,.88));color:#fff;font-size:9px;line-height:1.25;text-align:center;}
        .empty-state{grid-column:1/-1;padding:34px 20px;text-align:center;color:var(--ink-mute);font-size:12.5px;}

        .footer-note{max-width:1180px;margin:8px auto 0;padding:0 40px 40px;font-size:11px;line-height:1.6;color:var(--ink-mute);text-align:center;}
        .footer-brand{margin-top:12px;font-size:10px;color:var(--ink-mute);letter-spacing:.05em;}

        .expired-shell{max-width:520px;margin:0 auto;padding:60px 20px;}
        .expired-card{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:40px 32px;text-align:center;box-shadow:var(--shadow);}
        .expired-card h2{font-size:18px;font-weight:700;}
        .expired-card p{font-size:13.5px;color:var(--ink-soft);line-height:1.65;margin:12px auto 0;max-width:36ch;}

        .viewer{position:fixed;inset:0;z-index:100;background:rgba(6,10,22,.94);display:flex;flex-direction:column;opacity:0;pointer-events:none;transition:opacity .2s;}
        .viewer.show{opacity:1;pointer-events:auto;}
        .viewer__top{display:flex;align-items:center;justify-content:space-between;padding:16px 22px;color:#fff;}
        .viewer__tag{display:inline-flex;align-items:center;gap:6px;font-size:11px;font-weight:700;letter-spacing:.04em;text-transform:uppercase;padding:5px 12px;border-radius:999px;background:rgba(255,255,255,.14);}
        .viewer__tag--discrepancia{background:var(--pmm-red);}
        .viewer__counter{font-size:12.5px;color:rgba(255,255,255,.72);font-family:'IBM Plex Mono',monospace;}
        .viewer__close{width:34px;height:34px;border-radius:50%;background:rgba(255,255,255,.12);border:none;color:#fff;font-size:16px;cursor:pointer;display:flex;align-items:center;justify-content:center;}
        .viewer__close:hover{background:rgba(255,255,255,.22);}
        .viewer__stage{flex:1;display:flex;align-items:center;justify-content:center;position:relative;padding:0 70px 24px;min-height:0;}
        .viewer__stage img{max-width:100%;max-height:100%;border-radius:10px;box-shadow:0 20px 60px rgba(0,0,0,.5);}
        .viewer__nav{position:absolute;top:50%;transform:translateY(-50%);width:46px;height:46px;border-radius:50%;background:rgba(255,255,255,.12);border:none;color:#fff;font-size:20px;cursor:pointer;display:flex;align-items:center;justify-content:center;}
        .viewer__nav:hover{background:rgba(255,255,255,.24);}
        .viewer__nav--prev{left:14px;}
        .viewer__nav--next{right:14px;}
        .viewer__bottom{display:flex;justify-content:center;padding:14px 22px 22px;}
        .viewer__download{display:inline-flex;align-items:center;gap:8px;background:#fff;color:var(--ink);font-weight:700;font-size:12.5px;text-decoration:none;padding:10px 18px;border-radius:999px;}

        @media (max-width: 860px){
          .topbar{padding:14px 18px;}
          .shell{grid-template-columns:1fr;padding:0 0 40px;gap:0;}
          .summary{position:static;border-radius:0;border-left:none;border-right:none;margin-top:-1px;}
          .main{padding:16px;gap:16px;}
          .evidence-grid{grid-template-columns:1fr;}
          .invoice-table{display:none;}
          .invoice-cards{display:flex;flex-direction:column;gap:10px;padding:16px;}
          .invoice-row{border:1px solid var(--border);border-radius:13px;padding:12px 14px;background:var(--surface-2);}
          .invoice-row.is-discrepancia{background:var(--pmm-red-pale);border-color:var(--pmm-red-line);}
          .invoice-row__top{display:flex;align-items:center;justify-content:space-between;gap:10px;}
          .invoice-row__num{font-size:13.5px;font-weight:700;}
          .invoice-row__facts{display:flex;gap:16px;margin-top:8px;font-size:11.5px;color:var(--ink-soft);flex-wrap:wrap;}
          .invoice-row__facts b{color:var(--ink);}
          .invoice-row__discrepancia{margin-top:8px;font-size:11.5px;color:var(--pmm-red);background:rgba(220,38,38,.08);border-radius:9px;padding:8px 10px;line-height:1.5;}
          .viewer__stage{padding:0 14px 16px;}
          .viewer__nav{width:38px;height:38px;font-size:16px;}
          .viewer__nav--prev{left:6px;}
          .viewer__nav--next{right:6px;}
          .footer-note{padding:0 16px 32px;}
        }
    ";
}
