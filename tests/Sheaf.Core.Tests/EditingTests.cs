using System.Drawing;
using PdfSharp.Pdf;
using Sheaf.Core.Editing;
using Sheaf.Core.Pdf;
using Sheaf.Core.Rendering;

namespace Sheaf.Core.Tests;

public class EditingTests
{
    [Fact]
    public void PagesCanBeReorderedTurnedAddedAndRemoved()
    {
        using var temp = new TempFolder();
        string source = TestPdfs.WithWidths(temp.File("a.pdf"), 300, 400, 500);
        EditDocument document = DocumentStore.Open(source, temp.File("work"), null);

        document.Pages.Reverse();
        document.Pages.RemoveAt(1);
        document.Pages.Insert(1, new PageEntry { SourcePage = -1, Width = 200, Height = 200 });
        document.Pages[0].Rotation = 90;
        DocumentStore.Save(document, temp.File("out.pdf"), keepEditable: false);

        Assert.Equal([500, 200, 300], TestPdfs.PageWidths(temp.File("out.pdf")));
        using PdfDocument result = PdfFiles.OpenForImport(temp.File("out.pdf"));
        Assert.Equal(90, result.Pages[0].Rotate);
    }

    [Fact]
    public async Task ObjectsAreFlattenedAndComeBackEditable()
    {
        using var temp = new TempFolder();
        string source = TestPdfs.WithWidths(temp.File("a.pdf"), 400, 400);
        EditDocument document = DocumentStore.Open(source, temp.File("work"), null);
        document.Pages[0].Objects.Add(new PageObject { Kind = MarkKind.Rectangle, X = 20, Y = 20, Width = 100, Height = 60, StrokeColor = Color.Red.ToArgb(), FillColor = Color.Red.ToArgb() });
        document.Pages[1].Objects.Add(new PageObject { Kind = MarkKind.Text, X = 40, Y = 40, Width = 200, Height = 40, Text = "Approved" });
        document.Pages[1].Objects.Add(new PageObject { Kind = MarkKind.Note, X = 300, Y = 30, Width = 20, Height = 20, Comment = "Check this" });
        DocumentStore.Save(document, temp.File("out.pdf"), keepEditable: true);

        using (var preview = await PdfPreview.OpenAsync(temp.File("out.pdf")))
        using (Bitmap page = await preview.RenderAsync(0, 400))
        {
            Color inside = page.GetPixel(70, 50);
            Assert.True(inside.R > 200 && inside.G < 80, $"Rectangle not drawn: {inside}");
        }

        EditDocument reopened = DocumentStore.Open(temp.File("out.pdf"), temp.File("work2"), null);
        Assert.Single(reopened.Pages[0].Objects);
        Assert.Equal(2, reopened.Pages[1].Objects.Count);
        Assert.Equal("Approved", reopened.Pages[1].Objects.First(o => o.Kind == MarkKind.Text).Text);

        using (var preview = await PdfPreview.OpenAsync(reopened.Sources[0]))
        using (Bitmap clean = await preview.RenderAsync(0, 400))
        {
            Assert.True(clean.GetPixel(70, 50).GetBrightness() > 0.9f, "The stored layer was not removed from the working copy.");
        }
        using PdfDocument stripped = PdfFiles.OpenForImport(reopened.Sources[0]);
        Assert.Null(stripped.Pages[1].Elements.GetArray("/Annots")?.Elements.Count is > 0 ? stripped.Pages[1].Elements.GetArray("/Annots") : null);
    }

    [Fact]
    public void FormFieldsAreReadAndFilled()
    {
        using var temp = new TempFolder();
        string source = TestPdfs.WithForm(temp.File("form.pdf"));
        EditDocument document = DocumentStore.Open(source, temp.File("work"), null);

        Assert.Equal(2, document.Fields.Count);
        FormField name = document.Fields.Single(field => field.Name == "Name");
        Assert.Equal(FieldKind.Text, name.Kind);
        Assert.Equal(120, name.Bounds.Y, 1);
        Assert.Equal(FieldKind.CheckBox, document.Fields.Single(field => field.Name == "Agree").Kind);

        document.FieldValues["Name"] = "Ada Lovelace";
        document.FieldValues["Agree"] = "true";
        DocumentStore.Save(document, temp.File("out.pdf"), keepEditable: false);

        using PdfDocument result = PdfFiles.OpenForImport(temp.File("out.pdf"));
        var values = FormFields.Read(result).ToDictionary(field => field.Name, field => field.Value);
        Assert.Equal("Ada Lovelace", values["Name"]);
        Assert.Equal("true", values["Agree"]);
    }

    [Fact]
    public async Task RoundedRectanglesKeepTheirCornersClear()
    {
        using var temp = new TempFolder();
        string source = TestPdfs.WithWidths(temp.File("a.pdf"), 400);
        EditDocument document = DocumentStore.Open(source, temp.File("work"), null);
        document.Pages[0].Objects.Add(new PageObject { Kind = MarkKind.Rectangle, X = 20, Y = 20, Width = 200, Height = 200, CornerRadius = 60, StrokeColor = Color.Red.ToArgb(), FillColor = Color.Red.ToArgb() });
        DocumentStore.Save(document, temp.File("out.pdf"), keepEditable: true);

        using (var preview = await PdfPreview.OpenAsync(temp.File("out.pdf")))
        using (Bitmap page = await preview.RenderAsync(0, 400))
        {
            Assert.True(page.GetPixel(24, 24).GetBrightness() > 0.9f, "The corner was filled.");
            Assert.True(page.GetPixel(120, 120).R > 200 && page.GetPixel(120, 120).G < 80, "The middle wasn't filled.");
        }
        EditDocument reopened = DocumentStore.Open(temp.File("out.pdf"), temp.File("work2"), null);
        Assert.Equal(60, reopened.Pages[0].Objects[0].CornerRadius);
    }

    [Fact]
    public void ProtectedFileKeepsItsPassword()
    {
        using var temp = new TempFolder();
        string source = TestPdfs.WithPages(temp.File("a.pdf"), 1);
        using (PdfDocument document = PdfFiles.OpenForModify(source))
        {
            PdfSecurity.Protect(document, "pw");
            PdfFiles.Save(document, temp.File("locked.pdf"));
        }
        EditDocument edit = DocumentStore.Open(temp.File("locked.pdf"), temp.File("work"), "pw");
        edit.Pages[0].Objects.Add(new PageObject { Kind = MarkKind.Text, X = 10, Y = 10, Width = 100, Height = 20, Text = "x" });
        DocumentStore.Save(edit, temp.File("saved.pdf"), keepEditable: false);
        Assert.Throws<PasswordException>(() => PdfFiles.OpenForImport(temp.File("saved.pdf")));
        using PdfDocument opened = PdfFiles.OpenForImport(temp.File("saved.pdf"), "pw");
        Assert.Equal(1, opened.PageCount);
    }
}
