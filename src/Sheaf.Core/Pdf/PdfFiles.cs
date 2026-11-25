using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Sheaf.Core.Pdf;

/// <summary>A PDF on disk and the password that opens it, if it has one.</summary>
public sealed record PdfSource(string Path, string? Password = null);

/// <summary>Opens and saves PDFs, turning library errors into messages a person can act on.</summary>
public static class PdfFiles
{
    public static PdfDocument OpenForImport(string path, string? password = null) =>
        Open(path, PdfDocumentOpenMode.Import, password);

    public static PdfDocument OpenForModify(string path, string? password = null) =>
        Open(path, PdfDocumentOpenMode.Modify, password);

    /// <summary>How many pages a PDF has.</summary>
    public static int PageCount(string path, string? password = null)
    {
        using PdfDocument document = OpenForImport(path, password);
        return document.PageCount;
    }

    /// <summary>Writes next to the target first, so a failed or cancelled save never leaves half a file.</summary>
    public static void Save(PdfDocument document, string path)
    {
        string partial = path + ".partial";
        try
        {
            document.Save(partial);
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(partial);
            throw new ToolException(
                $"Can’t save “{Path.GetFileName(path)}” in {Path.GetDirectoryName(path)}. " +
                "Check that the folder exists and you can write to it.", ex);
        }
    }

    static PdfDocument Open(string path, PdfDocumentOpenMode mode, string? password)
    {
        string name = Path.GetFileName(path);
        bool askedForPassword = false;
        void RefusePassword(PdfPasswordProviderArgs args)
        {
            askedForPassword = true;
            args.Abort = true;
        }

        try
        {
            // PDFsharp returns null rather than throwing when the password request is refused.
            PdfDocument? document = password is null
                ? PdfReader.Open(path, mode, RefusePassword)
                : PdfReader.Open(path, password, mode, RefusePassword);
            if (document is null) throw PasswordProblem(name, password);
            SortBoxCorners(document);
            return document;
        }
        catch (Exception ex) when (askedForPassword)
        {
            throw PasswordProblem(name, password, ex);
        }
        catch (Exception ex) when (ex is not ToolException)
        {
            throw Describe(ex, name);
        }
    }

    static readonly string[] BoxKeys = ["/MediaBox", "/CropBox", "/BleedBox", "/TrimBox", "/ArtBox"];

    /// <summary>
    /// Puts each page box's corners in the usual order, lower left then upper right. A PDF may give them either way
    /// round (some writers put the top first), but PDFsharp, and Sheaf with it, would read a negative page size.
    /// </summary>
    static void SortBoxCorners(PdfDocument document)
    {
        foreach (PdfPage page in document.Pages)
            foreach (string key in BoxKeys)
            {
                if (!page.Elements.ContainsKey(key)) continue;
                PdfRectangle box = page.Elements.GetRectangle(key);
                if (box.X1 <= box.X2 && box.Y1 <= box.Y2) continue;
                page.Elements.SetRectangle(key, new PdfRectangle(
                    new XPoint(Math.Min(box.X1, box.X2), Math.Min(box.Y1, box.Y2)), new XPoint(Math.Max(box.X1, box.X2), Math.Max(box.Y1, box.Y2))));
            }
    }

    internal static PasswordException PasswordProblem(string name, string? password, Exception? inner = null) =>
        password is null
            ? new PasswordException($"“{name}” is password-protected. Remove the password with Unlock PDF first.", inner)
            : new PasswordException($"The password doesn’t unlock “{name}”. Check it and try again.", inner);

    internal static ToolException Describe(Exception ex, string name) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException =>
            new ToolException($"“{name}” is no longer there. It may have been moved or deleted.", ex),
        UnauthorizedAccessException =>
            new ToolException($"Windows won’t let Sheaf read “{name}”.", ex),
        IOException =>
            new ToolException($"“{name}” is open in another program. Close it there and try again.", ex),
        _ => new ToolException($"“{name}” can’t be read. It may be damaged or not a PDF.", ex),
    };

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
