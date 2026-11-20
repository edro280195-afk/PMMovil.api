using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ClosedXML.Excel;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public static class EntryPanelExport
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly string[] Headers =
    {
        "Referencia", "Fecha", "Hora", "Tipo", "Cliente", "Manejo", "Transporte", "Guía", "Caja", "Bultos",
        "Recibió", "Capturó", "Mercancía dañada", "Fotos de arribo", "Fotos de daño", "Estado"
    };

    private static readonly double[] Widths = { 18, 12, 8, 18, 34, 22, 28, 20, 16, 9, 26, 14, 12, 10, 10, 14 };

    public static string FileName(DateTime now)
    {
        return $"entradas-{now:yyyyMMdd-HHmm}.xlsx";
    }

    public static string TypeLabel(string type)
    {
        return type switch
        {
            "C" => "Entrada de bodega",
            "D" => "Otra entrada (D)",
            _ => type
        };
    }

    public static byte[] Build(IReadOnlyList<EntryRow> rows, EntryPanelFilter filter, DateTime generatedAt, bool truncated)
    {
        using var workbook = new XLWorkbook();
        BuildEntriesSheet(workbook.Worksheets.Add("Entradas"), rows);
        BuildFiltersSheet(workbook.Worksheets.Add("Filtros"), rows.Count, filter, generatedAt, truncated);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void BuildEntriesSheet(IXLWorksheet sheet, IReadOnlyList<EntryRow> rows)
    {
        for (int column = 0; column < Headers.Length; column++)
        {
            IXLCell cell = sheet.Cell(1, column + 1);
            cell.Value = Headers[column];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1d3f72");
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            sheet.Column(column + 1).Width = Widths[column];
        }

        for (int index = 0; index < rows.Count; index++)
        {
            EntryRow row = rows[index];
            int line = index + 2;
            sheet.Cell(line, 1).Value = row.Reference;
            if (row.CreatedAt is { } createdAt)
            {
                sheet.Cell(line, 2).Value = createdAt.Date;
                sheet.Cell(line, 3).Value = createdAt.TimeOfDay;
            }

            sheet.Cell(line, 4).Value = TypeLabel(row.Type);
            sheet.Cell(line, 5).Value = row.ClientName.Length > 0 ? row.ClientName : row.ClientId;
            sheet.Cell(line, 6).Value = row.HandlingName.Length > 0 ? row.HandlingName : row.HandlingId;
            sheet.Cell(line, 7).Value = row.CarrierName.Length > 0 ? row.CarrierName : row.CarrierId;
            sheet.Cell(line, 8).Value = row.Waybill;
            sheet.Cell(line, 9).Value = row.Container;
            sheet.Cell(line, 10).Value = row.Packages;
            sheet.Cell(line, 11).Value = row.ReceivedBy;
            sheet.Cell(line, 12).Value = row.CreatedBy;
            sheet.Cell(line, 13).Value = row.Damaged ? "Sí" : "No";
            sheet.Cell(line, 14).Value = row.ArrivalPhotos;
            sheet.Cell(line, 15).Value = row.DamagePhotos;
            sheet.Cell(line, 16).Value = row.Incomplete ? "Por completar" : "Completa";
        }

        if (rows.Count > 0)
        {
            sheet.Range(2, 2, rows.Count + 1, 2).Style.DateFormat.Format = "dd/mm/yyyy";
            sheet.Range(2, 3, rows.Count + 1, 3).Style.DateFormat.Format = "hh:mm";
            sheet.Range(2, 10, rows.Count + 1, 10).Style.NumberFormat.Format = "#,##0.###";
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, Math.Max(1, rows.Count + 1), Headers.Length).SetAutoFilter();
        sheet.Row(1).Height = 22;
    }

    private static void BuildFiltersSheet(IXLWorksheet sheet, int rowCount, EntryPanelFilter filter, DateTime generatedAt, bool truncated)
    {
        var lines = new List<(string Name, string Value)>
        {
            ("Generado", generatedAt.ToString("dd/MM/yyyy HH:mm")),
            ("Periodo", $"{filter.From:dd/MM/yyyy} al {filter.ToExclusive.AddDays(-1):dd/MM/yyyy}"),
            ("Tipo", filter.Type.Length == 0 ? "Todas" : TypeLabel(filter.Type)),
            ("Clientes (claves)", ListOrAll(filter.Clients)),
            ("Manejos (claves)", ListOrAll(filter.Handlings)),
            ("Recibió", ListOrAll(filter.Receivers)),
            ("Transportes (claves)", ListOrAll(filter.Carriers)),
            ("Fotos", filter.Photos switch
            {
                "with" => "Con fotos de arribo",
                "without" => "Sin fotos de arribo",
                "incomplete" => "Por completar",
                _ => "Todas"
            }),
            ("Solo con daño", filter.DamagedOnly ? "Sí" : "No"),
            ("Búsqueda", string.IsNullOrWhiteSpace(filter.Text) ? "Ninguna" : filter.Text!),
            ("Filas exportadas", rowCount.ToString("N0"))
        };
        if (truncated)
            lines.Add(("Aviso", $"El resultado superaba {EntryPanelRules.MaxExportRows:N0} filas: se exportaron las primeras. Acota el periodo para ver el resto."));

        sheet.Cell(1, 1).Value = "Filtro";
        sheet.Cell(1, 2).Value = "Valor";
        sheet.Range(1, 1, 1, 2).Style.Font.Bold = true;
        sheet.Range(1, 1, 1, 2).Style.Fill.BackgroundColor = XLColor.FromHtml("#e6edf7");
        for (int index = 0; index < lines.Count; index++)
        {
            sheet.Cell(index + 2, 1).Value = lines[index].Name;
            sheet.Cell(index + 2, 2).Value = lines[index].Value;
        }

        sheet.Column(1).Width = 24;
        sheet.Column(2).Width = 72;
    }

    private static string ListOrAll(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "Todos" : string.Join(", ", values);
    }
}
