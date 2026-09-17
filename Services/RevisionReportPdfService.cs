using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Data;

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PMMovil.Api.Services;

/// <summary>
/// Genera el reporte de revisión física en PDF real (QuestPDF, ya estaba en
/// el .csproj sin usarse en ningún archivo). De uso interno por ahora: el
/// correo automático al cliente (<see cref="RevisionReportService"/>) no
/// cambia -- este es un documento nuevo y aparte, pendiente de que dirección
/// lo valide antes de mandarse a algún cliente.
///
/// Reutiliza las mismas consultas que ya arma RevisionReportService para el
/// correo, más la bitácora de revision_actividad y las fotos de
/// discrepancia (ntipodocto = 103) que la app ya sube.
/// </summary>
public class RevisionReportPdfService
{
    private const string PmmBlue = "#00338D";
    private const string PmmBlueDark = "#002266";
    private const string PmmLime = "#C4D600";
    private const string Ink = "#0A1730";
    private const string InkMuted = "#46587D";
    private const string Line = "#DDE7F5";
    private const string BadBg = "#FCE9E7";
    private const string BadText = "#B02D22";
    private const string OkBg = "#E4F5EC";
    private const string OkText = "#1F7A4C";

    private readonly BaseRepository _db;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<RevisionReportPdfService> _logger;
    private readonly string _fotosBasePath;

    public RevisionReportPdfService(
        BaseRepository db,
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<RevisionReportPdfService> logger)
    {
        _db = db;
        _env = env;
        _logger = logger;
        // Misma clave que ya usa FotoController -- las fotos de discrepancia
        // viven en el mismo disco/carpeta por cliente.
        _fotosBasePath = config["PhotoSettings:BasePath"] ?? "F:\\Trafico\\fotos";
    }

    public async Task<(byte[]? Pdf, string? Error)> GenerarPdfAsync(string creferencia)
    {
        using IDbConnection conn = _db.CreateConnection();

        dynamic? general = await conn.QueryFirstOrDefaultAsync<object>(@"
            SELECT
                f.cref_agrupada,
                MAX(f.cclienteid) AS cidcte,
                MAX(cl.cnombre) AS nombreCliente,
                MAX(p.cnombre) AS nombreProveedor,
                -- Igual que en RevisionReportService: el LEFT JOIN a revdet de
                -- abajo repite cada factura una vez por partida, así que un
                -- SUM(f.totbultos) directo cuenta el mismo bulto varias veces.
                -- (No se usa hoy -- el PDF calcula totalFis/totalDoc aparte,
                -- sumando en C# sobre la lista de facturas ya agregada con
                -- MAX -- pero se deja correcto para no dejar una trampa aquí.)
                (SELECT COALESCE(SUM(f2.totbultos), 0) FROM fact f2 WHERE f2.cref_agrupada = f.cref_agrupada) AS bultosFisicos,
                (SELECT COALESCE(SUM(f2.totbultosdoc), 0) FROM fact f2 WHERE f2.cref_agrupada = f.cref_agrupada) AS bultosDocumento,
                MAX(f.usurev) AS usuarioRevisor,
                MAX(td.ubicacion) AS ubicacion,
                (SELECT COALESCE(SUM(TIMESTAMPDIFF(SECOND, finiciorevision, ffinrevision)), 0)
                 FROM traftiemposrevision WHERE creferencia = f.cref_agrupada
                   AND finiciorevision IS NOT NULL AND ffinrevision IS NOT NULL) AS segundosTotales,
                (SELECT MIN(finiciorevision) FROM traftiemposrevision
                 WHERE creferencia = f.cref_agrupada AND YEAR(finiciorevision) >= 2000) AS inicio,
                (SELECT MAX(ffinrevision) FROM traftiemposrevision
                 WHERE creferencia = f.cref_agrupada AND YEAR(ffinrevision) >= 2000) AS fin,
                (SELECT usurevisa FROM traftiemposrevision
                 WHERE creferencia = f.cref_agrupada AND YEAR(ffinrevision) >= 2000
                 ORDER BY ffinrevision DESC LIMIT 1) AS supervisorCierre
            FROM fact f
            LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid
            LEFT JOIN revdet rd ON rd.cnofact = f.cnofact
            LEFT JOIN provs p ON p.cidprov = rd.cprovid
            LEFT JOIN trafdet td ON td.creferencia = f.cref_agrupada
            WHERE f.cref_agrupada = @cref
            GROUP BY f.cref_agrupada",
            new { cref = creferencia });

        if (general == null)
        {
            return (null, "No se encontró información de revisión para esa entrada.");
        }

        // Se extraen a variables tipadas aquí mismo: pasar el "dynamic" de
        // Dapper directo dentro de un lambda de QuestPDF hace que toda la
        // llamada se resuelva en tiempo de ejecución (DLR) en vez de en
        // compilación, y ahí es donde tronaba con
        // "Cannot implicitly convert type 'void' to 'object'" -- probado
        // contra una entrada real.
        string nombreCliente = (string)(general.nombreCliente ?? "N/D");
        string nombreProveedor = (string)(general.nombreProveedor ?? "N/D");
        string ubicacion = (string)(general.ubicacion ?? "N/D");

        List<dynamic> facturas = (await conn.QueryAsync<dynamic>(@"
            SELECT
                f.cnofact,
                MAX(f.status_rev) AS status_rev,
                MAX(f.totbultos) AS totbultos,
                MAX(f.totbultosdoc) AS totbultosdoc,
                MAX(f.discrepancias) AS discrepancias,
                COUNT(rd.idrevdet) AS totalPartidas,
                SUM(rd.revisada) AS partidasRevisadas
            FROM fact f
            LEFT JOIN revdet rd ON rd.cnofact = f.cnofact
            WHERE f.cref_agrupada = @cref
            GROUP BY f.cnofact",
            new { cref = creferencia })).ToList();

        List<dynamic> bitacora;
        using (IDbConnection connUtileria = _db.CreatePmmutileriaConnection())
        {
            bitacora = (await connUtileria.QueryAsync<dynamic>(@"
                SELECT usuario, tipo, detalle, fecha
                FROM revision_actividad
                WHERE creferencia = @cref
                  AND NOT (tipo = 'CAMBIO_ACTIVIDAD' AND COALESCE(detalle, '') = 'Actividad en revisión')
                ORDER BY fecha ASC
                LIMIT 200",
                new { cref = creferencia })).ToList();
        }

        List<dynamic> fotos = (await conn.QueryAsync<dynamic>(
            "SELECT idfotos, cextension, nlineno, usucrea FROM fotos WHERE creferencia = @cref AND ntipodocto = 103 ORDER BY nlineno",
            new { cref = creferencia })).ToList();

        string carpetaCliente = await conn.ExecuteScalarAsync<string?>(
            "SELECT COALESCE(NULLIF(carpeta,''), cidcte) FROM clientes WHERE cidcte = @id LIMIT 1",
            new { id = (string)(general.cidcte ?? "") }) ?? (string)(general.cidcte ?? "");

        byte[]? logo = LoadLogo();

        int totalDoc = facturas.Sum(f => (int)(f.totbultosdoc ?? 0));
        int totalFis = facturas.Sum(f => (int)(f.totbultos ?? 0));
        int totalPartidas = facturas.Sum(f => (int)(f.totalPartidas ?? 0));
        int totalPartidasRev = facturas.Sum(f => (int?)f.partidasRevisadas ?? 0);
        int conDiscrepancia = facturas.Count(f => (int)(f.status_rev ?? 0) == 2);
        bool conforme = conDiscrepancia == 0;
        DateTime? inicio = general.inicio as DateTime?;
        DateTime? fin = general.fin as DateTime?;
        int minutosTotales = (inicio.HasValue && fin.HasValue) ? (int)Math.Round((fin.Value - inicio.Value).TotalMinutes) : 0;

        byte[] pdf = Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(28);
                page.DefaultTextStyle(t => t.FontFamily("Arial").FontSize(9).FontColor(Ink));

                page.Header().Element(c => ComposeHeader(c, logo, creferencia));

                page.Content().PaddingTop(10).Column(col =>
                {
                    col.Spacing(12);

                    col.Item().Element(c => ComposeIdentificacion(c, creferencia, nombreCliente, nombreProveedor, ubicacion));
                    col.Item().Element(c => ComposeResultado(c, conforme, conDiscrepancia, facturas.Count, totalDoc - totalFis));
                    col.Item().Element(c => ComposeCotejo(c, totalDoc, totalFis, totalPartidasRev, totalPartidas));
                    col.Item().Element(c => ComposeFacturas(c, facturas));

                    List<dynamic> conDisc = facturas.Where(f => (int)(f.status_rev ?? 0) == 2).ToList();
                    if (conDisc.Count > 0) col.Item().Element(c => ComposeDiscrepancias(c, conDisc));
                    if (fotos.Count > 0) col.Item().Element(c => ComposeFotos(c, fotos, carpetaCliente, creferencia));

                    col.Item().Element(c => ComposeBitacora(c, bitacora));
                    col.Item().Element(c => ComposeFirmas(c, (string)(general.usuarioRevisor ?? ""), (string)(general.supervisorCierre ?? "")));
                });

                page.Footer().Element(c => ComposeFooter(c, minutosTotales));
            });
        }).GeneratePdf();

        return (pdf, null);
    }

    private byte[]? LoadLogo()
    {
        try
        {
            string path = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "assets", "imex_logo.png");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo cargar el logo IMEX para el reporte PDF.");
            return null;
        }
    }

    private static void ComposeHeader(IContainer container, byte[]? logo, string creferencia)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    if (logo != null) left.Item().Height(46).Image(logo).FitArea();
                    left.Item().PaddingTop(6).Text("IMEX Forwarding Agency Inc.").FontSize(10).Bold().FontColor(Ink);
                    left.Item().Text("Grupo PMM · Agencia aduanal · Revisión física de mercancía").FontSize(8).FontColor(InkMuted);
                });
                row.ConstantItem(200).Column(right =>
                {
                    right.Item().AlignRight().Text("Reporte de revisión física").FontSize(13).Bold().FontColor(PmmBlue);
                    right.Item().AlignRight().Text("PHYSICAL INSPECTION REPORT").FontSize(7).FontColor(InkMuted);
                    right.Item().PaddingTop(6).AlignRight().Text($"Folio RV-{creferencia}").FontSize(8).FontColor(InkMuted);
                    right.Item().AlignRight().Text($"Emitido {DateTime.Now:dd/MM/yyyy HH:mm}").FontSize(8).FontColor(InkMuted);
                    right.Item().PaddingTop(4).AlignRight().Element(e => Badge(e, "USO INTERNO · PENDIENTE DE VALIDAR"));
                });
            });
            col.Item().PaddingTop(6).Height(3).Row(row =>
            {
                row.RelativeItem(3).Background(PmmBlue);
                row.RelativeItem(2).Background(PmmLime);
            });
        });
    }

    private static void Badge(IContainer container, string text)
    {
        container.Background("#FDF1DD").Border(1).BorderColor("#EFCE95").Padding(3)
            .Text(text).FontSize(6.5f).Bold().FontColor("#A76400");
    }

    private static void ComposeIdentificacion(IContainer container, string creferencia, string nombreCliente, string nombreProveedor, string ubicacion)
    {
        container.Column(col =>
        {
            SectionTitle(col, "Identificación de la entrada");
            col.Item().Border(1).BorderColor(Line).Padding(8).Row(row =>
            {
                InfoCell(row, "Referencia", creferencia);
                InfoCell(row, "Cliente", nombreCliente);
                InfoCell(row, "Proveedor", nombreProveedor);
                InfoCell(row, "Ubicación", ubicacion);
            });
        });
    }

    private static void InfoCell(RowDescriptor row, string label, string value)
    {
        row.RelativeItem().Column(c =>
        {
            c.Item().Text(label.ToUpperInvariant()).FontSize(6.5f).Bold().FontColor(InkMuted);
            c.Item().Text(string.IsNullOrWhiteSpace(value) ? "—" : value).FontSize(9).Bold().FontColor(Ink);
        });
    }

    private static void ComposeResultado(IContainer container, bool conforme, int conDiscrepancia, int totalFacturas, int diferenciaBultos)
    {
        string bg = conforme ? OkBg : BadBg;
        string fg = conforme ? OkText : BadText;
        string titulo = conforme ? "Conforme — sin diferencias" : "Con diferencias contra documento";
        string detalle = conforme
            ? "Todas las facturas cotejaron sin discrepancias."
            : $"{conDiscrepancia} de {totalFacturas} facturas presentan discrepancia" +
              (diferenciaBultos != 0 ? $". Diferencia de {Math.Abs(diferenciaBultos)} bultos contra lo documentado." : ".");

        container.Column(col =>
        {
            SectionTitle(col, "Resultado");
            col.Item().Background(bg).Padding(10).Text(t =>
            {
                t.Span(titulo + "  ").Bold().FontSize(10).FontColor(fg);
                t.Span(detalle).FontSize(8.5f).FontColor(Ink);
            });
        });
    }

    private static void ComposeCotejo(IContainer container, int totalDoc, int totalFis, int partidasRev, int partidasTotal)
    {
        container.Column(col =>
        {
            SectionTitle(col, "Cotejo de bultos — físico contra documento");
            col.Item().Row(row =>
            {
                Figure(row, "Bultos en documento", totalDoc.ToString(), false);
                Figure(row, "Bultos contados", totalFis.ToString(), false);
                Figure(row, "Diferencia", (totalFis - totalDoc).ToString("+#;-#;0"), totalFis != totalDoc);
                Figure(row, "Partidas revisadas", $"{partidasRev}/{partidasTotal}", false);
            });
        });
    }

    private static void Figure(RowDescriptor row, string label, string value, bool alerta)
    {
        row.RelativeItem().Padding(2).Border(1).BorderColor(alerta ? "#EEB4AD" : Line)
            .Background(alerta ? BadBg : "#F8FAFE").Padding(8).Column(c =>
            {
                c.Item().Text(label.ToUpperInvariant()).FontSize(6.5f).Bold().FontColor(alerta ? BadText : InkMuted);
                c.Item().Text(value).FontSize(15).Bold().FontColor(alerta ? BadText : Ink);
            });
    }

    private static void ComposeFacturas(IContainer container, List<dynamic> facturas)
    {
        container.Column(col =>
        {
            SectionTitle(col, "Detalle por factura");
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(2);
                    c.RelativeColumn(1);
                    c.RelativeColumn(1);
                    c.RelativeColumn(1);
                    c.RelativeColumn(1.4f);
                    c.RelativeColumn(2);
                });

                table.Header(h =>
                {
                    HeaderCell(h.Cell(), "Factura");
                    HeaderCell(h.Cell(), "Doc.");
                    HeaderCell(h.Cell(), "Contado");
                    HeaderCell(h.Cell(), "Dif.");
                    HeaderCell(h.Cell(), "Partidas");
                    HeaderCell(h.Cell(), "Resultado");
                });

                foreach (dynamic f in facturas)
                {
                    int doc = (int)(f.totbultosdoc ?? 0);
                    int fis = (int)(f.totbultos ?? 0);
                    int status = (int)(f.status_rev ?? 0);
                    bool bad = status == 2;
                    string bg = bad ? BadBg : "#FFFFFF";

                    Cell(table, (string)f.cnofact, bg);
                    Cell(table, doc.ToString(), bg);
                    Cell(table, fis.ToString(), bg);
                    Cell(table, (fis - doc).ToString("+#;-#;0"), bg);
                    Cell(table, $"{f.partidasRevisadas ?? 0}/{f.totalPartidas ?? 0}", bg);
                    table.Cell().Background(bg).BorderBottom(1).BorderColor(Line).Padding(4)
                        .Text(bad ? "Con discrepancia" : "Conforme").FontSize(8).Bold()
                        .FontColor(bad ? BadText : OkText);
                }
            });
        });
    }

    private static void HeaderCell(IContainer cell, string text) =>
        cell.Background(PmmBlue).Padding(4).Text(text).FontSize(7).Bold().FontColor("#FFFFFF");

    private static void Cell(TableDescriptor table, string text, string bg) =>
        table.Cell().Background(bg).BorderBottom(1).BorderColor(Line).Padding(4).Text(text).FontSize(8);

    private static void ComposeDiscrepancias(IContainer container, List<dynamic> conDiscrepancia)
    {
        container.Column(col =>
        {
            SectionTitle(col, "Discrepancias encontradas");
            foreach (dynamic f in conDiscrepancia)
            {
                col.Item().Border(1).BorderColor("#EEB4AD").Background("#FDF6F5").Padding(8).Column(c =>
                {
                    c.Item().Text((string)f.cnofact).Bold().FontSize(9).FontColor(BadText);
                    string motivo = string.IsNullOrWhiteSpace((string?)f.discrepancias)
                        ? "Sin detalle capturado por el revisor."
                        : (string)f.discrepancias;
                    c.Item().PaddingTop(2).Text(motivo).FontSize(8.5f);
                });
            }
        });
    }

    private void ComposeFotos(IContainer container, List<dynamic> fotos, string carpetaCliente, string creferencia)
    {
        container.Column(col =>
        {
            SectionTitle(col, "Evidencia fotográfica");
            col.Item().Row(row =>
            {
                foreach (dynamic foto in fotos.Take(8))
                {
                    string ext = PhotoFileNameResolver.NormalizeExtension((string?)foto.cextension);
                    int linea = (int)foto.nlineno;
                    string? path = PhotoFileNameResolver.FindExistingPath(
                        _fotosBasePath,
                        carpetaCliente,
                        creferencia,
                        ntipodocto: 103,
                        nlineno: linea,
                        extension: ext);

                    row.RelativeItem().Padding(2).Column(c =>
                    {
                        if (path != null)
                        {
                            try
                            {
                                c.Item().Height(90).Border(1).BorderColor(Line).Image(File.ReadAllBytes(path)).FitArea();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "No se pudo incrustar la foto {Path} en el reporte.", path);
                                c.Item().Height(90).Border(1).BorderColor(Line).Background("#F8FAFE")
                                    .AlignCenter().AlignMiddle().Text("Imagen no disponible").FontSize(7).FontColor(InkMuted);
                            }
                        }
                        else
                        {
                            c.Item().Height(90).Border(1).BorderColor(Line).Background("#F8FAFE")
                                .AlignCenter().AlignMiddle().Text("Imagen no disponible").FontSize(7).FontColor(InkMuted);
                        }
                        c.Item().PaddingTop(2).Text($"Foto #{linea}").FontSize(6.5f).FontColor(InkMuted);
                    });
                }
            });
        });
    }

    private static void ComposeBitacora(IContainer container, List<dynamic> bitacora)
    {
        container.Column(col =>
        {
            SectionTitle(col, "Bitácora de la revisión");
            if (bitacora.Count == 0)
            {
                col.Item().Text("Sin actividad registrada.").FontSize(8).FontColor(InkMuted);
                return;
            }
            foreach (dynamic ev in bitacora)
            {
                DateTime fecha = (DateTime)ev.fecha;
                string usuario = (string)(ev.usuario ?? "");
                string detalle = (string)(ev.detalle ?? (string)ev.tipo);
                col.Item().Row(row =>
                {
                    row.ConstantItem(42).Text(fecha.ToString("HH:mm")).FontSize(7.5f).FontColor(InkMuted);
                    row.RelativeItem().Text(t =>
                    {
                        t.Span(usuario + " ").Bold().FontSize(7.5f).FontColor(PmmBlue);
                        t.Span(detalle).FontSize(7.5f).FontColor(Ink);
                    });
                });
            }
        });
    }

    private static void ComposeFirmas(IContainer container, string revisor, string supervisor)
    {
        container.Column(col =>
        {
            SectionTitle(col, "Responsables");
            col.Item().PaddingTop(20).Row(row =>
            {
                Firma(row, revisor, "Revisó la mercancía");
                Firma(row, supervisor, "Cerró la revisión");
            });
        });
    }

    private static void Firma(RowDescriptor row, string nombre, string rol)
    {
        row.RelativeItem().Column(c =>
        {
            c.Item().BorderTop(1).BorderColor(InkMuted).PaddingTop(4)
                .Text(string.IsNullOrWhiteSpace(nombre) ? "—" : nombre).FontSize(9).Bold().AlignCenter();
            c.Item().Text(rol.ToUpperInvariant()).FontSize(6.5f).FontColor(InkMuted).AlignCenter();
        });
    }

    private static void ComposeFooter(IContainer container, int minutosTotales)
    {
        container.BorderTop(1).BorderColor(Line).PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Text(
                $"Generado automáticamente por PMMovil el {DateTime.Now:dd/MM/yyyy HH:mm}. " +
                (minutosTotales > 0 ? $"Tiempo total de revisión: {minutosTotales} min." : ""))
                .FontSize(7).FontColor(InkMuted);
            row.ConstantItem(60).AlignRight().Text(x =>
            {
                x.CurrentPageNumber().FontSize(7).FontColor(InkMuted);
                x.Span(" / ").FontSize(7).FontColor(InkMuted);
                x.TotalPages().FontSize(7).FontColor(InkMuted);
            });
        });
    }

    private static void SectionTitle(ColumnDescriptor col, string text)
    {
        col.Item().PaddingBottom(3).BorderBottom(1).BorderColor(PmmBlue)
            .Text(text.ToUpperInvariant()).FontSize(8).Bold().FontColor(PmmBlue);
    }
}
