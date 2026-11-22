using PdfSharp.Pdf;

namespace Sheaf.Core.Pdf;

/// <param name="Metadata">Author, title, dates, the producing app and the XMP metadata stream.</param>
/// <param name="Comments">Notes, highlights and other markup; links and form fields stay.</param>
/// <param name="Scripts">JavaScript and actions that run when the file or a page opens.</param>
/// <param name="Attachments">Files embedded in the PDF.</param>
public sealed record CleanOptions(bool Metadata, bool Comments, bool Scripts, bool Attachments);

/// <summary>Removes what a reader doesn't see but a PDF can carry. The document must be opened for modifying.</summary>
public static class Sanitizer
{
    static readonly HashSet<string> KeptAnnotations = ["/Link", "/Widget"];

    /// <summary>Returns how many items were removed.</summary>
    public static int Clean(PdfDocument document, CleanOptions options)
    {
        int removed = 0;
        PdfDictionary catalog = document.Internals.Catalog;
        PdfDictionary? names = catalog.Elements.GetDictionary("/Names");

        if (options.Metadata)
        {
            removed += document.Info.Elements.Count;
            document.Info.Elements.Clear();
            removed += Remove(catalog, "/Metadata") + Remove(catalog, "/PieceInfo");
        }
        if (options.Scripts)
        {
            removed += Remove(catalog, "/OpenAction") + Remove(catalog, "/AA");
            if (names is not null) removed += Remove(names, "/JavaScript");
        }
        if (options.Attachments && names is not null)
            removed += Remove(names, "/EmbeddedFiles");

        foreach (PdfPage page in document.Pages)
            removed += CleanPage(page, options);
        return removed;
    }

    static int CleanPage(PdfPage page, CleanOptions options)
    {
        int removed = 0;
        if (options.Metadata) removed += Remove(page, "/Metadata") + Remove(page, "/PieceInfo");
        if (options.Scripts) removed += Remove(page, "/AA");

        PdfArray? annotations = page.Elements.GetArray("/Annots");
        if (annotations is null) return removed;
        for (int i = annotations.Elements.Count - 1; i >= 0; i--)
        {
            if (annotations.Elements.GetDictionary(i) is not { } annotation) continue;
            string subtype = annotation.Elements.GetName("/Subtype");
            bool attachment = subtype == "/FileAttachment";
            if ((options.Comments && !KeptAnnotations.Contains(subtype) && !attachment) || (options.Attachments && attachment))
            {
                annotations.Elements.RemoveAt(i);
                removed++;
                continue;
            }
            if (options.Scripts && IsScript(annotation.Elements.GetDictionary("/A")))
                removed += Remove(annotation, "/A");
            if (options.Scripts) removed += Remove(annotation, "/AA");
        }
        return removed;
    }

    static bool IsScript(PdfDictionary? action) =>
        action is not null && action.Elements.GetName("/S") is "/JavaScript" or "/Launch" or "/SubmitForm" or "/ImportData";

    static int Remove(PdfDictionary dictionary, string key) =>
        dictionary.Elements.ContainsKey(key) && dictionary.Elements.Remove(key) ? 1 : 0;
}
