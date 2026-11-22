using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Sheaf.Core.Editing;
using Sheaf.Core.Fonts;
using Sheaf.Core.Pdf;

namespace Sheaf.Core.Ocr;

/// <summary>
/// Writes recognised words onto a page as invisible text, each stretched over the word it reads, so the page
/// can be searched, selected and copied while it looks exactly the same.
/// </summary>
public static class TextLayer
{
    static readonly Regex BeginText = new(@"(?<=^|\s)BT(?=\s)", RegexOptions.Compiled);

    /// <param name="words">Words with boxes in the page's displayed coordinates, in points.</param>
    public static int Add(PdfPage page, IEnumerable<PageWord> words)
    {
        var list = words.Where(word => word.Text.Trim().Length > 0 && word.Bounds.Width > 0.5f && word.Bounds.Height > 0.5f).ToList();
        if (list.Count == 0) return 0;
        using (PageCanvas canvas = PageCanvas.Open(page))
        {
            XGraphics gfx = canvas.Graphics;
            foreach (PageWord word in list)
            {
                double size = Math.Max(1, word.Bounds.Height * 0.85);
                var font = new XFont(EmbeddedFonts.PdfFamily, size);
                double natural = gfx.MeasureString(word.Text, font).Width;
                if (natural <= 0) continue;
                XGraphicsState state = gfx.Save();
                gfx.TranslateTransform(word.Bounds.Left, word.Bounds.Bottom - word.Bounds.Height * 0.18);
                gfx.ScaleTransform(word.Bounds.Width / natural, 1);
                gfx.DrawString(word.Text, font, XBrushes.Black, 0, 0, XStringFormats.BaseLineLeft);
                gfx.Restore(state);
            }
        }
        MakeTextInvisible(page);
        return list.Count;
    }

    /// <summary>
    /// Switches the text of the content stream just written to render mode 3 (neither filled nor stroked).
    /// PDFsharp has no setting for it, and a transparent colour still prints in some viewers.
    /// </summary>
    static void MakeTextInvisible(PdfPage page)
    {
        var contents = page.Contents.Elements;
        if (contents.Count == 0 || contents.GetDictionary(contents.Count - 1) is not { Stream: { } stream } layer) return;
        string operators = Encoding.Latin1.GetString(stream.UnfilteredValue);
        layer.Elements.Remove("/Filter");
        layer.Elements.Remove("/DecodeParms");
        stream.Value = Encoding.Latin1.GetBytes(BeginText.Replace(operators, "BT 3 Tr"));
    }
}
