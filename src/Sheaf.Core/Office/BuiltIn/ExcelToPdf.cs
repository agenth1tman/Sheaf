using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Sheaf.Core.Office.BuiltIn;

/// <summary>
/// Turns a workbook (.xlsx) into a PDF without Excel: each visible sheet as a table of its used cells, with
/// the values Excel shows (formula results included), bold text, column widths and merged cells. Charts,
/// pictures and conditional colours are not drawn.
/// </summary>
public static class ExcelToPdf
{
    const int MaxRows = 5000, MaxColumns = 60;
    const double BaseFontSize = 9, MinFontSize = 6, PointsPerCharacter = 5.6;

    public static void Convert(string input, string output, CancellationToken cancel = default)
    {
        using SpreadsheetDocument workbook = Open(input);
        WorkbookPart part = workbook.WorkbookPart ?? throw new ToolException($"“{Path.GetFileName(input)}” has no workbook inside.");
        var document = new Document();
        document.Styles[StyleNames.Normal]!.Font.Name = "Calibri";
        var reader = new SheetReader(part);
        foreach (S.Sheet sheet in part.Workbook?.Sheets?.Elements<S.Sheet>() ?? [])
        {
            cancel.ThrowIfCancellationRequested();
            if (sheet.State?.Value is { } state && state != S.SheetStateValues.Visible) continue;
            if (sheet.Id?.Value is not { } id || part.GetPartById(id) is not WorksheetPart worksheet) continue;
            reader.AddSheet(document, sheet.Name?.Value ?? "Sheet", worksheet, cancel);
        }
        if (document.Sections.Count == 0) document.AddSection().AddParagraph("This workbook has no visible sheets with cells.");
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(output);
    }

    static SpreadsheetDocument Open(string input)
    {
        try
        {
            return SpreadsheetDocument.Open(input, false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or IOException or FileFormatException)
        {
            throw new ToolException($"“{Path.GetFileName(input)}” can’t be read. The built-in converter reads workbooks saved as .xlsx; save .xls files as .xlsx first.", ex);
        }
    }

    sealed class SheetReader(WorkbookPart workbook)
    {
        readonly List<string> _strings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().Select(item => item.InnerText).ToList() ?? [];
        readonly S.Stylesheet? _styles = workbook.WorkbookStylesPart?.Stylesheet;

        public void AddSheet(Document document, string name, WorksheetPart part, CancellationToken cancel)
        {
            var cells = new Dictionary<(int Row, int Column), (string Text, bool Bold, bool Number)>();
            int lastRow = -1, lastColumn = -1;
            foreach (S.Row row in part.Worksheet?.GetFirstChild<S.SheetData>()?.Elements<S.Row>() ?? [])
            {
                cancel.ThrowIfCancellationRequested();
                foreach (S.Cell cell in row.Elements<S.Cell>())
                {
                    if (cell.CellReference?.Value is not { } reference) continue;
                    var (r, c) = Position(reference);
                    if (r >= MaxRows || c >= MaxColumns) continue;
                    var (text, number) = Text(cell);
                    if (text.Length == 0) continue;
                    cells[(r, c)] = (text, Bold(cell), number);
                    lastRow = Math.Max(lastRow, r);
                    lastColumn = Math.Max(lastColumn, c);
                }
            }
            if (lastRow < 0) return;

            var widths = Enumerable.Range(0, lastColumn + 1).Select(c => ColumnWidth(part, c)).ToList();
            Section section = document.AddSection();
            double portrait = 595.28 - 2 * 40;
            bool landscape = widths.Sum() > portrait;
            section.PageSetup.PageWidth = Unit.FromPoint(landscape ? 841.89 : 595.28);
            section.PageSetup.PageHeight = Unit.FromPoint(landscape ? 595.28 : 841.89);
            section.PageSetup.LeftMargin = section.PageSetup.RightMargin = section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromPoint(40);
            double available = (landscape ? 841.89 : 595.28) - 80;
            double scale = Math.Min(1, available / widths.Sum());
            double fontSize = Math.Max(MinFontSize, BaseFontSize * scale);

            Paragraph heading = section.AddParagraph(name);
            heading.Format.Font.Size = 13;
            heading.Format.Font.Bold = true;
            heading.Format.SpaceAfter = Unit.FromPoint(8);

            Table table = section.AddTable();
            table.Borders.Width = 0.4;
            table.Borders.Color = new MigraDoc.DocumentObjectModel.Color(200, 200, 200);
            table.Format.Font.Size = fontSize;
            foreach (double width in widths) table.AddColumn(Unit.FromPoint(Math.Max(10, width * scale)));
            var merges = Merges(part);
            for (int r = 0; r <= lastRow; r++)
            {
                Row row = table.AddRow();
                row.TopPadding = row.BottomPadding = Unit.FromPoint(1);
                for (int c = 0; c <= lastColumn; c++)
                {
                    if (merges.TryGetValue((r, c), out int span)) row.Cells[c].MergeRight = Math.Min(span, lastColumn - c);
                    if (!cells.TryGetValue((r, c), out var value)) continue;
                    Paragraph paragraph = row.Cells[c].AddParagraph(value.Text);
                    paragraph.Format.Font.Bold = value.Bold;
                    if (value.Number) paragraph.Format.Alignment = ParagraphAlignment.Right;
                }
            }
        }

        static (int Row, int Column) Position(string reference)
        {
            int column = 0, i = 0;
            for (; i < reference.Length && char.IsAsciiLetter(reference[i]); i++) column = column * 26 + char.ToUpperInvariant(reference[i]) - 'A' + 1;
            int row = int.TryParse(reference.AsSpan(i), out int parsed) ? parsed : 1;
            return (row - 1, column - 1);
        }

        double ColumnWidth(WorksheetPart part, int column)
        {
            S.Column? definition = part.Worksheet?.GetFirstChild<S.Columns>()?.Elements<S.Column>()
                .FirstOrDefault(item => item.Min?.Value <= column + 1 && item.Max?.Value >= column + 1);
            if (definition?.Hidden?.Value == true) return 0.1;
            double characters = definition?.Width?.Value ?? part.Worksheet?.GetFirstChild<S.SheetFormatProperties>()?.DefaultColumnWidth?.Value ?? 8.43;
            return characters * PointsPerCharacter;
        }

        static Dictionary<(int, int), int> Merges(WorksheetPart part)
        {
            var merges = new Dictionary<(int, int), int>();
            foreach (S.MergeCell merge in part.Worksheet?.GetFirstChild<S.MergeCells>()?.Elements<S.MergeCell>() ?? [])
            {
                string[] corners = merge.Reference?.Value?.Split(':') ?? [];
                if (corners.Length != 2) continue;
                var (top, left) = Position(corners[0]);
                var (_, right) = Position(corners[1]);
                if (right > left) merges[(top, left)] = right - left;
            }
            return merges;
        }

        (string Text, bool Number) Text(S.Cell cell)
        {
            string raw = cell.CellValue?.Text ?? "";
            if (cell.DataType?.Value == S.CellValues.SharedString)
                return (int.TryParse(raw, out int index) && index >= 0 && index < _strings.Count ? _strings[index] : "", false);
            if (cell.DataType?.Value == S.CellValues.InlineString) return (cell.InlineString?.InnerText ?? "", false);
            if (cell.DataType?.Value == S.CellValues.Boolean) return (raw == "1" ? "TRUE" : "FALSE", false);
            if (cell.DataType?.Value == S.CellValues.String || cell.DataType?.Value == S.CellValues.Error) return (raw, false);
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return (raw, false);
            return (FormatNumber(number, NumberFormat(cell)), true);
        }

        (uint Id, string? Code) NumberFormat(S.Cell cell)
        {
            if (cell.StyleIndex?.Value is not uint styleIndex) return (0, null);
            S.CellFormat? format = _styles?.CellFormats?.Elements<S.CellFormat>().ElementAtOrDefault((int)styleIndex);
            uint id = format?.NumberFormatId?.Value ?? 0;
            string? code = _styles?.NumberingFormats?.Elements<S.NumberingFormat>().FirstOrDefault(item => item.NumberFormatId?.Value == id)?.FormatCode?.Value;
            return (id, code);
        }

        static string FormatNumber(double value, (uint Id, string? Code) format)
        {
            var culture = CultureInfo.CurrentCulture;
            string code = format.Code ?? "";
            bool date = format.Id is >= 14 and <= 22 or >= 45 and <= 47
                || (code.Length > 0 && !code.Contains('0') && !code.Contains('#') && (code.Contains('d') || code.Contains('y') || code.Contains("mm") || code.Contains('h')));
            if (date && value is > -657435 and < 2958466)
            {
                DateTime moment = DateTime.FromOADate(value);
                return moment.TimeOfDay == TimeSpan.Zero ? moment.ToString("d", culture) : value < 1 ? moment.ToString("t", culture) : moment.ToString("g", culture);
            }
            if (format.Id is 9 or 10 || code.Contains('%')) return (value * 100).ToString(format.Id == 9 ? "0" : "0.00", culture) + " %";
            if (format.Id is 3 or 4 || code.Contains("#,##")) return value.ToString(format.Id == 3 || !code.Contains('.') ? "#,##0" : "#,##0.00", culture);
            if (format.Id is 2 || code.Contains("0.00")) return value.ToString("0.00", culture);
            return Math.Round(value, 10).ToString("G", culture);
        }

        bool Bold(S.Cell cell)
        {
            if (cell.StyleIndex?.Value is not uint styleIndex) return false;
            uint? fontId = _styles?.CellFormats?.Elements<S.CellFormat>().ElementAtOrDefault((int)styleIndex)?.FontId?.Value;
            return fontId is uint id && _styles?.Fonts?.Elements<S.Font>().ElementAtOrDefault((int)id)?.Bold is { } bold && (bold.Val?.Value ?? true);
        }
    }
}
