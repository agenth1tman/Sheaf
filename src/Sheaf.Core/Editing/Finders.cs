using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using Sheaf.Core.Pdf;
using Sheaf.Core.Rendering;
using ZXing;
using ZXing.Common;
using Pig = UglyToad.PdfPig;

namespace Sheaf.Core.Editing;

/// <summary>A word on a page, with its box in the page's displayed coordinates.</summary>
public sealed record PageWord(string Text, RectangleF Bounds);

/// <summary>Something found on a page of a source file: what it is, and the box to mark.</summary>
public sealed record Found(int SourcePage, RectangleF Bounds, string Text);

/// <summary>What the Redact panel can look for besides typed text.</summary>
public enum FindKind { Emails, PhoneNumbers, BankAndCardNumbers }

/// <summary>Reads the words of a PDF with PdfPig, and finds text, patterns and QR codes or barcodes to redact.</summary>
public static class Finders
{
    static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    static readonly Regex Phone = new(@"(?<![\w.])\+?\(?\d[\d ()./-]{6,}\d(?![\w])", RegexOptions.Compiled);
    static readonly Regex Iban = new(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){3,7}(?: ?[A-Z0-9]{1,3})?\b", RegexOptions.Compiled);
    static readonly Regex Card = new(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)", RegexOptions.Compiled);

    /// <summary>The words of every page of a PDF (without a password), in reading order as the file stores them.</summary>
    public static List<List<PageWord>> ReadWords(string path, CancellationToken cancel = default)
    {
        var pages = new List<List<PageWord>>();
        using var sharp = PdfFiles.OpenForImport(path);
        using var pig = Pig.PdfDocument.Open(path);
        for (int i = 0; i < pig.NumberOfPages && i < sharp.PageCount; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var words = new List<PageWord>();
            foreach (Pig.Content.Word word in pig.GetPage(i + 1).GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                var box = word.BoundingBox;
                // PdfPig's boxes run from the baseline to the cap height; reach down to descenders and up to accents.
                double height = box.Top - box.Bottom;
                XPoint a = PageCanvas.FromPdf(sharp.Pages[i], new XPoint(box.Left, box.Bottom - height * 0.3));
                XPoint b = PageCanvas.FromPdf(sharp.Pages[i], new XPoint(box.Right, box.Top + height * 0.15));
                words.Add(new PageWord(word.Text, RectangleF.FromLTRB((float)Math.Min(a.X, b.X), (float)Math.Min(a.Y, b.Y), (float)Math.Max(a.X, b.X), (float)Math.Max(a.Y, b.Y))));
            }
            pages.Add(words);
        }
        return pages;
    }

    /// <summary>Every place a phrase appears, ignoring case and line breaks; one box per line it covers.</summary>
    public static List<Found> FindText(IReadOnlyList<List<PageWord>> pages, string phrase)
    {
        string trimmed = phrase.Trim();
        if (trimmed.Length == 0) return [];
        string pattern = string.Join(@"\s+", trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
        return Find(pages, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), _ => true);
    }

    public static List<Found> FindPattern(IReadOnlyList<List<PageWord>> pages, FindKind kind) => kind switch
    {
        FindKind.Emails => Find(pages, Email, _ => true),
        FindKind.PhoneNumbers => Find(pages, Phone, match => match.Count(char.IsAsciiDigit) is >= 8 and <= 15 && !Card.IsMatch(match)),
        _ => [.. Find(pages, Iban, _ => true), .. Find(pages, Card, match => Luhn(match))],
    };

    static List<Found> Find(IReadOnlyList<List<PageWord>> pages, Regex regex, Func<string, bool> accept)
    {
        var found = new List<Found>();
        for (int page = 0; page < pages.Count; page++)
        {
            List<PageWord> words = pages[page];
            var text = new StringBuilder();
            var starts = new List<int>();
            foreach (PageWord word in words)
            {
                starts.Add(text.Length);
                text.Append(word.Text).Append(' ');
            }
            foreach (Match match in regex.Matches(text.ToString()))
            {
                if (!accept(match.Value)) continue;
                int end = match.Index + match.Length;
                var covered = Enumerable.Range(0, words.Count)
                    .Where(i => starts[i] < end && starts[i] + words[i].Text.Length > match.Index)
                    .Select(i => words[i]).ToList();
                foreach (RectangleF line in LineBoxes(covered))
                    found.Add(new Found(page, line, match.Value.Trim()));
            }
        }
        return found;
    }

    /// <summary>Joins the boxes of words that sit on the same line, with a little room around them.</summary>
    public static IEnumerable<RectangleF> LineBoxes(IEnumerable<PageWord> words)
    {
        var lines = new List<RectangleF>();
        foreach (PageWord word in words)
        {
            RectangleF box = word.Bounds;
            int same = lines.FindIndex(line => Math.Abs(line.Top + line.Height / 2 - (box.Top + box.Height / 2)) < Math.Max(line.Height, box.Height) / 2);
            if (same >= 0) lines[same] = RectangleF.Union(lines[same], box);
            else lines.Add(box);
        }
        return lines.Select(line => RectangleF.Inflate(line, 1.5f, 1.5f));
    }

    static bool Luhn(string number)
    {
        var digits = number.Where(char.IsAsciiDigit).Select(c => c - '0').Reverse().ToList();
        if (digits.Count is < 13 or > 19) return false;
        int sum = digits.Select((digit, i) => i % 2 == 1 ? (digit * 2 > 9 ? digit * 2 - 9 : digit * 2) : digit).Sum();
        return sum % 10 == 0;
    }

    /// <summary>QR codes and barcodes on the given pages of a PDF, found in renders of the pages.</summary>
    public static async Task<List<Found>> FindCodesAsync(string path, IEnumerable<int> pages, CancellationToken cancel = default)
    {
        const int RenderWidth = 2000;
        var found = new List<Found>();
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true },
        };
        using PdfPreview preview = await PdfPreview.OpenAsync(path).ConfigureAwait(false);
        foreach (int page in pages.Distinct())
        {
            cancel.ThrowIfCancellationRequested();
            SizeF size = preview.GetPageSize(page);
            using Bitmap bitmap = await preview.RenderAsync(page, RenderWidth, cancel).ConfigureAwait(false);
            float scale = size.Width / bitmap.Width;
            Result[]? results = reader.DecodeMultiple(Luminance(bitmap));
            foreach (Result result in results ?? [])
            {
                if (result.ResultPoints is not { Length: > 0 } points) continue;
                RectangleF box = CodeBox(result.BarcodeFormat, points);
                found.Add(new Found(page, new RectangleF(box.X * scale, box.Y * scale, box.Width * scale, box.Height * scale), Describe(result.BarcodeFormat)));
            }
        }
        return found;
    }

    static RGBLuminanceSource Luminance(Bitmap bitmap)
    {
        var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] bytes = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return new RGBLuminanceSource(bytes, data.Width, data.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// The whole symbol around the points ZXing reports: QR and similar codes report the centres of their
    /// corner squares, so the box grows by a quarter on each side; barcodes report the ends of one scan line.
    /// </summary>
    static RectangleF CodeBox(BarcodeFormat format, ResultPoint[] points)
    {
        float left = points.Min(p => p.X), top = points.Min(p => p.Y), right = points.Max(p => p.X), bottom = points.Max(p => p.Y);
        float width = right - left, height = bottom - top;
        if (format is BarcodeFormat.QR_CODE or BarcodeFormat.DATA_MATRIX or BarcodeFormat.AZTEC or BarcodeFormat.MAXICODE)
        {
            float side = Math.Max(width, height);
            float grow = side * 0.3f;
            return RectangleF.FromLTRB(left - grow, top - grow, right + grow, bottom + grow);
        }
        float tall = Math.Max(height, width * 0.45f);
        float middle = (top + bottom) / 2;
        return RectangleF.FromLTRB(left - width * 0.06f, middle - tall / 2, right + width * 0.06f, middle + tall / 2);
    }

    static string Describe(BarcodeFormat format) => format switch
    {
        BarcodeFormat.QR_CODE => "QR code",
        BarcodeFormat.DATA_MATRIX => "Data Matrix code",
        BarcodeFormat.AZTEC => "Aztec code",
        BarcodeFormat.PDF_417 => "PDF417 code",
        _ => "Barcode",
    };
}
