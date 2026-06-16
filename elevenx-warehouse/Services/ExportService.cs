using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ElevenX.Warehouse.Services;

/// <summary>ตารางข้อมูลสำหรับ export</summary>
public record ExportTable(string Title, IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows, string? Subtitle = null);

public interface IExportService
{
    byte[] ToExcel(ExportTable table);
    byte[] ToPdf(ExportTable table);
}

public class ExportService : IExportService
{
    private static bool _fontsRegistered;
    private static readonly object _lock = new();

    public ExportService(IWebHostEnvironment env)
    {
        if (_fontsRegistered) return;
        lock (_lock)
        {
            if (_fontsRegistered) return;
            var fontDir = Path.Combine(env.WebRootPath ?? "wwwroot", "fonts");
            foreach (var f in new[] { "Kanit-Regular.ttf", "Kanit-SemiBold.ttf" })
            {
                var path = Path.Combine(fontDir, f);
                if (File.Exists(path))
                    using (var fs = File.OpenRead(path))
                        QuestPDF.Drawing.FontManager.RegisterFont(fs);
            }
            _fontsRegistered = true;
        }
    }

    public byte[] ToExcel(ExportTable table)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(Sanitize(table.Title));

        var colCount = table.Headers.Count;
        var row = 1;

        // Title
        ws.Cell(row, 1).Value = table.Title;
        ws.Range(row, 1, row, colCount).Merge();
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 14;
        row++;

        if (!string.IsNullOrEmpty(table.Subtitle))
        {
            ws.Cell(row, 1).Value = table.Subtitle;
            ws.Range(row, 1, row, colCount).Merge();
            ws.Cell(row, 1).Style.Font.FontColor = XLColor.Gray;
            row++;
        }
        row++; // เว้น 1 บรรทัด

        // Header
        var headerRow = row;
        for (var c = 0; c < colCount; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = table.Headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1B2D4A");
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }
        row++;

        // Data
        foreach (var r in table.Rows)
        {
            for (var c = 0; c < colCount && c < r.Length; c++)
                ws.Cell(row, c + 1).Value = r[c];
            row++;
        }

        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public byte[] ToPdf(ExportTable table)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontFamily("Kanit").FontSize(9).FontColor("#1A2A3F"));

                page.Header().Column(col =>
                {
                    col.Item().Text(table.Title).FontSize(15).SemiBold().FontColor("#0A1428");
                    if (!string.IsNullOrEmpty(table.Subtitle))
                        col.Item().Text(table.Subtitle).FontSize(9).FontColor("#5B6B82");
                    col.Item().PaddingTop(4).LineHorizontal(1).LineColor("#2D7FF9");
                });

                page.Content().PaddingVertical(8).Table(t =>
                {
                    t.ColumnsDefinition(cd =>
                    {
                        for (var i = 0; i < table.Headers.Count; i++) cd.RelativeColumn();
                    });

                    t.Header(h =>
                    {
                        foreach (var head in table.Headers)
                            h.Cell().Background("#111E33").Padding(6).Text(head).FontColor("#FFFFFF").SemiBold().FontSize(9);
                    });

                    var alt = false;
                    foreach (var r in table.Rows)
                    {
                        var bg = alt ? "#F2F5FA" : "#FFFFFF";
                        foreach (var cell in r)
                            t.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#D9E0EC").Padding(5).Text(cell ?? "").FontSize(8.5f);
                        alt = !alt;
                    }
                });

                page.Footer().AlignRight().Text(txt =>
                {
                    txt.Span("ElevenX • พิมพ์เมื่อ " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + "  |  หน้า ").FontSize(8).FontColor("#5B6B82");
                    txt.CurrentPageNumber().FontSize(8).FontColor("#5B6B82");
                    txt.Span(" / ").FontSize(8).FontColor("#5B6B82");
                    txt.TotalPages().FontSize(8).FontColor("#5B6B82");
                });
            });
        });

        return doc.GeneratePdf();
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { ':', '\\', '/', '?', '*', '[', ']' }))
            name = name.Replace(c, ' ');
        return name.Length > 31 ? name[..31] : name;
    }
}
