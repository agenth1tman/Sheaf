using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using PdfSharp.Drawing;
using Sheaf.Core.Fonts;
using Sheaf.Core.Office.BuiltIn;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using Pig = UglyToad.PdfPig;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Sheaf.Core.Tests;

/// <summary>The converters that work without Microsoft Office or LibreOffice.</summary>
public class BuiltInConverterTests
{
    static string PdfText(string path)
    {
        using var pdf = Pig.PdfDocument.Open(path);
        return string.Join("\n", pdf.GetPages().Select(page => string.Join(" ", page.GetWords().Select(word => word.Text))));
    }

    [Fact]
    public void WordDocumentsBecomePdfs()
    {
        using var temp = new TempFolder();
        string docx = temp.File("report.docx");
        using (var word = WordprocessingDocument.Create(docx, WordprocessingDocumentType.Document))
        {
            MainDocumentPart main = word.AddMainDocumentPart();
            static W.Run Text(string text, bool bold = false) =>
                new(new W.RunProperties(bold ? new W.Bold() : new W.Italic()), new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });
            var table = new W.Table(
                new W.TableProperties(new W.TableBorders(new W.TopBorder { Val = W.BorderValues.Single })),
                new W.TableGrid(new W.GridColumn { Width = "3000" }, new W.GridColumn { Width = "3000" }),
                new W.TableRow(new W.TableCell(new W.Paragraph(Text("Region", bold: true))), new W.TableCell(new W.Paragraph(Text("Total", bold: true)))),
                new W.TableRow(new W.TableCell(new W.Paragraph(Text("North"))), new W.TableCell(new W.Paragraph(Text("1200")))));
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center }), Text("Quarterly results", bold: true)),
                new W.Paragraph(Text("Revenue grew by twelve percent.")),
                table,
                new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })),
                new W.Paragraph(Text("Second page notes"))));
        }

        WordToPdf.Convert(docx, temp.File("report.pdf"));

        string text = PdfText(temp.File("report.pdf"));
        Assert.Contains("Quarterly results", text, StringComparison.Ordinal);
        Assert.Contains("North", text, StringComparison.Ordinal);
        Assert.Contains("1200", text, StringComparison.Ordinal);
        using var pdf = Pig.PdfDocument.Open(temp.File("report.pdf"));
        Assert.Equal(2, pdf.NumberOfPages);
    }

    [Fact]
    public void WorkbooksBecomePdfs()
    {
        using var temp = new TempFolder();
        string xlsx = temp.File("sales.xlsx");
        using (var workbook = SpreadsheetDocument.Create(xlsx, SpreadsheetDocumentType.Workbook))
        {
            WorkbookPart book = workbook.AddWorkbookPart();
            book.Workbook = new S.Workbook();
            WorksheetPart sheet = book.AddNewPart<WorksheetPart>();
            static S.Cell Text(string reference, string text) => new() { CellReference = reference, DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(text)) };
            static S.Cell Number(string reference, double value) => new() { CellReference = reference, CellValue = new S.CellValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            sheet.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(Text("A1", "Region"), Text("B1", "Total")) { RowIndex = 1 },
                new S.Row(Text("A2", "North"), Number("B2", 1200)) { RowIndex = 2 },
                new S.Row(Text("A3", "South"), Number("B3", 875.5)) { RowIndex = 3 }));
            book.Workbook.AppendChild(new S.Sheets(new S.Sheet { Id = book.GetIdOfPart(sheet), SheetId = 1, Name = "Sales" }));
        }

        ExcelToPdf.Convert(xlsx, temp.File("sales.pdf"));

        string text = PdfText(temp.File("sales.pdf"));
        Assert.Contains("Sales", text, StringComparison.Ordinal);
        Assert.Contains("South", text, StringComparison.Ordinal);
        Assert.Contains("875.5", text.Replace(",", "."), StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationsBecomePdfs()
    {
        using var temp = new TempFolder();
        string pptx = temp.File("deck.pptx");
        using (var presentation = PresentationDocument.Create(pptx, PresentationDocumentType.Presentation))
        {
            PresentationPart part = presentation.AddPresentationPart();
            SlidePart slide = part.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1, Name = "" }, new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(),
                new P.Shape(
                    new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 2, Name = "Title" }, new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
                    new P.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 457200, Y = 457200 }, new A.Extents { Cx = 8229600, Cy = 1143000 }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                        new A.SolidFill(new A.RgbColorModelHex { Val = "DCE6F2" })),
                    new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                        new A.Paragraph(new A.Run(new A.RunProperties { FontSize = 3200, Bold = true }, new A.Text("Launch plan"))))))));
            part.Presentation = new P.Presentation(
                new P.SlideIdList(new P.SlideId { Id = 256, RelationshipId = part.GetIdOfPart(slide) }),
                new P.SlideSize { Cx = 9144000, Cy = 6858000 });
        }

        PowerPointToPdf.Convert(pptx, temp.File("deck.pdf"));

        using var pdf = Pig.PdfDocument.Open(temp.File("deck.pdf"));
        Assert.Equal(720, Math.Round(pdf.GetPage(1).Width));
        Assert.Contains("Launch", pdf.GetPage(1).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfsBecomeWordDocuments()
    {
        using var temp = new TempFolder();
        string pdfPath = temp.File("letter.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            var page = document.AddPage();
            using XGraphics gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString("Dear neighbours", new XFont(EmbeddedFonts.PdfFamily, 20, XFontStyleEx.Bold), XBrushes.Black, 72, 100);
            gfx.DrawString("The garden party moves to Saturday.", new XFont(EmbeddedFonts.PdfFamily, 12), XBrushes.Black, 72, 140);
            document.Save(pdfPath);
        }

        PdfToWord.Convert(pdfPath, temp.File("letter.docx"));

        using var word = WordprocessingDocument.Open(temp.File("letter.docx"), false);
        string text = word.MainDocumentPart!.Document!.Body!.InnerText;
        Assert.Contains("Dear neighbours", text, StringComparison.Ordinal);
        Assert.Contains("garden party moves to Saturday", text, StringComparison.Ordinal);
        Assert.NotNull(word.MainDocumentPart.Document.Body.Descendants<W.Bold>().FirstOrDefault());
    }
}

public class OneNoteExportTests
{
    [Fact]
    public void PagesStackUnderTheAttachedPdf()
    {
        var pictures = new[] { new Office.OneNoteExport.PagePicture([1, 2, 3], 1240, 1754), new Office.OneNoteExport.PagePicture([4, 5], 1240, 1754) };
        var xml = System.Xml.Linq.XDocument.Parse(Office.OneNoteExport.PageXml("{page-id}", "Report", @"C:\Docs\Report.pdf", pictures));
        System.Xml.Linq.XNamespace one = "http://schemas.microsoft.com/office/onenote/2013/onenote";

        Assert.Equal("{page-id}", xml.Root!.Attribute("ID")!.Value);
        Assert.Equal("Report", xml.Root.Element(one + "Title")!.Value);
        Assert.Equal(@"C:\Docs\Report.pdf", xml.Root.Element(one + "InsertedFile")!.Attribute("pathSource")!.Value);
        var images = xml.Root.Elements(one + "Image").ToList();
        Assert.Equal(2, images.Count);
        double firstY = double.Parse(images[0].Element(one + "Position")!.Attribute("y")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        double secondY = double.Parse(images[1].Element(one + "Position")!.Attribute("y")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        double height = double.Parse(images[0].Element(one + "Size")!.Attribute("height")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(secondY >= firstY + height, "Pictures overlap.");
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), images[0].Element(one + "Data")!.Value);
    }
}
