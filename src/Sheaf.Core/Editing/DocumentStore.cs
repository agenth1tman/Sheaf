using System.Text.Json;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using Sheaf.Core.Pdf;

namespace Sheaf.Core.Editing;

/// <summary>
/// Opens PDFs for editing and saves edits. With "keep editable", Sheaf flattens its objects into each page as
/// usual (so every reader shows them) and also stores them, with a pointer to the content stream it added, so
/// the next time Sheaf opens the file it removes that stream and the objects can be moved and changed again.
/// </summary>
public static class DocumentStore
{
    const string ObjectsKey = "/SheafObjects";
    const string OverlayKey = "/SheafOverlay";
    static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    /// <summary>A blank A4 document.</summary>
    public static EditDocument CreateBlank(string workFolder) => CreateBlank(workFolder, 595.28f, 841.89f);

    /// <summary>A document of one blank page of the given size in points (an image of w × h pixels is 0.75 w × 0.75 h).</summary>
    public static EditDocument CreateBlank(string workFolder, float width, float height)
    {
        Directory.CreateDirectory(workFolder);
        string basePath = Path.Combine(workFolder, "base.pdf");
        using (var document = new PdfDocument())
        {
            PdfPage page = document.AddPage();
            page.Width = XUnit.FromPoint(width);
            page.Height = XUnit.FromPoint(height);
            document.Save(basePath);
        }
        return new EditDocument
        {
            Sources = [basePath],
            Pages = [new PageEntry { Source = 0, SourcePage = 0, Width = width, Height = height }],
        };
    }

    /// <summary>
    /// Copies the file into <paramref name="workFolder"/> without Sheaf's own layer and without a password,
    /// and returns its pages with the objects Sheaf stored in it.
    /// </summary>
    public static EditDocument Open(string path, string workFolder, string? password)
    {
        Directory.CreateDirectory(workFolder);
        string basePath = Path.Combine(workFolder, "base.pdf");
        var document = new EditDocument { Sources = [basePath], Password = password };

        using (PdfDocument pdf = PdfFiles.OpenForModify(path, password))
        {
            for (int i = 0; i < pdf.PageCount; i++)
            {
                PdfPage page = pdf.Pages[i];
                XSize size = PageCanvas.VisualSize(page);
                document.Pages.Add(new PageEntry
                {
                    Source = 0,
                    SourcePage = i,
                    Width = (float)size.Width,
                    Height = (float)size.Height,
                    Objects = TakeStoredObjects(page),
                });
            }
            document.Fields = FormFields.Read(pdf);
            document.FieldValues = document.Fields.ToDictionary(field => field.Name, field => field.Value);
            if (password is not null) PdfSecurity.RemoveProtection(pdf);
            PdfFiles.Save(pdf, basePath);
        }
        return document;
    }

    /// <summary>Adds another PDF as a source of pages, for inserting pages from a file.</summary>
    public static IReadOnlyList<PageEntry> AddSource(EditDocument document, string path, string workFolder)
    {
        string copy = Path.Combine(workFolder, $"source-{document.Sources.Count}.pdf");
        var entries = new List<PageEntry>();
        using (PdfDocument pdf = PdfFiles.OpenForModify(path))
        {
            int source = document.Sources.Count;
            for (int i = 0; i < pdf.PageCount; i++)
            {
                XSize size = PageCanvas.VisualSize(pdf.Pages[i]);
                entries.Add(new PageEntry { Source = source, SourcePage = i, Width = (float)size.Width, Height = (float)size.Height, Objects = TakeStoredObjects(pdf.Pages[i]) });
            }
            PdfFiles.Save(pdf, copy);
        }
        document.Sources.Add(copy);
        return entries;
    }

    /// <summary>Writes the document to <paramref name="path"/>. Objects are flattened; with <paramref name="keepEditable"/> they are also stored.</summary>
    public static void Save(EditDocument document, string path, bool keepEditable, CancellationToken cancel = default)
    {
        var imports = new Dictionary<int, PdfDocument>();
        var images = new List<XImage>();
        try
        {
            using PdfDocument output = PdfFiles.OpenForModify(document.Sources[0]);
            List<PdfPage> pages = AssemblePages(output, document, imports);
            for (int i = 0; i < pages.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                PageEntry entry = document.Pages[i];
                PdfPage page = pages[i];
                // Before the objects go on, so they aren't mirrored with it.
                PageOps.Mirror(page, entry.MirrorX, entry.MirrorY);
                if (entry.IsBlank && entry.Background != unchecked((int)0xFFFFFFFF)) PageOps.Paint(page, System.Drawing.Color.FromArgb(entry.Background));
                var visible = entry.Objects.Where(o => !o.Hidden).ToList();
                if (visible.Count > 0)
                {
                    int contentsBefore = page.Contents.Elements.Count;
                    images.AddRange(OverlayWriter.Draw(page, visible));
                    if (keepEditable) Store(page, entry.Objects, contentsBefore);
                }
                page.Rotate = PageOps.NormalizeRotation(page.Rotate + entry.Rotation);
            }
            FormFields.Apply(output, document.ChangedFieldValues());
            if (document.Password is { } password) PdfSecurity.Protect(output, password);
            PdfFiles.Save(output, path);
        }
        finally
        {
            foreach (PdfDocument import in imports.Values) import.Dispose();
            foreach (XImage image in images) image.Dispose();
        }
    }

    /// <summary>Arranges the output's pages to match the document: originals kept in place, others imported or created.</summary>
    static List<PdfPage> AssemblePages(PdfDocument output, EditDocument document, Dictionary<int, PdfDocument> imports)
    {
        var originals = output.Pages.Cast<PdfPage>().ToList();
        var used = new HashSet<int>();
        var final = new List<PdfPage>();
        foreach (PageEntry entry in document.Pages)
        {
            PdfPage page;
            if (entry.IsBlank)
            {
                page = output.AddPage();
                page.Width = XUnit.FromPoint(entry.Width);
                page.Height = XUnit.FromPoint(entry.Height);
            }
            else if (entry.Source == 0 && used.Add(entry.SourcePage))
            {
                page = originals[entry.SourcePage];
            }
            else
            {
                if (!imports.TryGetValue(entry.Source, out PdfDocument? source))
                    imports[entry.Source] = source = PdfFiles.OpenForImport(document.Sources[entry.Source]);
                page = output.AddPage(source.Pages[entry.SourcePage]);
            }
            final.Add(page);
        }

        foreach (PdfPage page in originals.Where(page => !final.Contains(page)))
            output.Pages.Remove(page);
        for (int target = 0; target < final.Count; target++)
        {
            int current = IndexOf(output, final[target]);
            if (current != target) output.Pages.MovePage(current, target);
        }
        return final;
    }

    static int IndexOf(PdfDocument document, PdfPage page)
    {
        for (int i = 0; i < document.PageCount; i++)
            if (ReferenceEquals(document.Pages[i], page) || document.Pages[i].Reference == page.Reference) return i;
        return -1;
    }

    static void Store(PdfPage page, List<PageObject> objects, int contentsBefore)
    {
        if (page.Contents.Elements.Count > contentsBefore)
            page.Elements[OverlayKey] = page.Contents.Elements[page.Contents.Elements.Count - 1];
        page.Elements.SetString(ObjectsKey, JsonSerializer.Serialize(objects, Json));
    }

    /// <summary>Reads Sheaf's stored objects from a page and removes its flattened layer and comments.</summary>
    static List<PageObject> TakeStoredObjects(PdfPage page)
    {
        if (!page.Elements.ContainsKey(ObjectsKey)) return [];
        List<PageObject> objects;
        try
        {
            objects = JsonSerializer.Deserialize<List<PageObject>>(page.Elements.GetString(ObjectsKey), Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }

        if (page.Elements[OverlayKey] is PdfReference overlay)
        {
            var contents = page.Contents.Elements;
            for (int i = contents.Count - 1; i >= 0; i--)
                if (contents[i] is PdfReference reference && reference.ObjectID == overlay.ObjectID) contents.RemoveAt(i);
        }
        page.Elements.Remove(OverlayKey);
        page.Elements.Remove(ObjectsKey);

        PdfArray? annotations = page.Elements.GetArray("/Annots");
        if (annotations is not null)
            for (int i = annotations.Elements.Count - 1; i >= 0; i--)
                if (annotations.Elements.GetDictionary(i)?.Elements.ContainsKey(OverlayWriter.AnnotationIdKey) == true)
                    annotations.Elements.RemoveAt(i);
        return objects;
    }
}
