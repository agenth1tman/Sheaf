using PdfSharp.Pdf;
using Sheaf.Core.IO;
using Sheaf.Core.Pdf;

namespace Sheaf.Core.Tests;

public class PageOpsTests
{
    [Fact]
    public void MergeKeepsEveryPageInOrder()
    {
        using var temp = new TempFolder();
        string first = TestPdfs.WithWidths(temp.File("a.pdf"), 300, 400);
        string second = TestPdfs.WithWidths(temp.File("b.pdf"), 500);

        using (PdfDocument merged = PageOps.Merge([new PdfSource(first), new PdfSource(second)]))
            PdfFiles.Save(merged, temp.File("merged.pdf"));

        Assert.Equal([300, 400, 500], TestPdfs.PageWidths(temp.File("merged.pdf")));
    }

    [Fact]
    public void AssembleReordersAndTurnsPages()
    {
        using var temp = new TempFolder();
        string source = TestPdfs.WithWidths(temp.File("a.pdf"), 300, 400, 500);

        using (PdfDocument input = PdfFiles.OpenForImport(source))
        using (PdfDocument output = PageOps.Assemble(input, [new PagePick(2), new PagePick(0, 90)]))
            PdfFiles.Save(output, temp.File("out.pdf"));

        using PdfDocument result = PdfFiles.OpenForImport(temp.File("out.pdf"));
        Assert.Equal(2, result.PageCount);
        Assert.Equal(500, Math.Round(result.Pages[0].MediaBox.Width));
        Assert.Equal(90, result.Pages[1].Rotate);
    }

    [Fact]
    public void RotateAddsToExistingRotation()
    {
        using var temp = new TempFolder();
        string source = TestPdfs.WithPages(temp.File("a.pdf"), 2);

        using (PdfDocument document = PdfFiles.OpenForModify(source))
        {
            PageOps.Rotate(document, [1], 270);
            PageOps.Rotate(document, [1], 180);
            PdfFiles.Save(document, temp.File("out.pdf"));
        }

        using PdfDocument result = PdfFiles.OpenForImport(temp.File("out.pdf"));
        Assert.Equal(0, result.Pages[0].Rotate);
        Assert.Equal(90, result.Pages[1].Rotate);
    }

    [Fact]
    public void ChunksCoverEveryPage()
    {
        Assert.Equal([new PageSpan(1, 3), new PageSpan(4, 6), new PageSpan(7, 7)], PageOps.Chunks(7, 3));
        Assert.Equal([0, 2, 4], PageOps.Without(5, [1, 3]));
    }

    [Fact]
    public void UnreadableFileGivesPlainMessage()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("fake.pdf"), "not a pdf");
        var error = Assert.Throws<ToolException>(() => PdfFiles.OpenForImport(temp.File("fake.pdf")));
        Assert.Contains("can’t be read", error.Message);
    }

    [Fact]
    public void UniqueNamesNeverReplaceFiles()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("report.pdf"), "");
        Assert.Equal(temp.File("report (2).pdf"), OutputNames.UniqueFile(temp.Path, "report", ".pdf"));
        Assert.Equal("a-b", OutputNames.CleanName("a:b"));
    }
}
