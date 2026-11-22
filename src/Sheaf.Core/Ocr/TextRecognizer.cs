using System.Drawing;
using System.Drawing.Imaging;
using Sheaf.Core.Editing;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Sheaf.Core.Ocr;

/// <summary>An OCR language Windows has installed.</summary>
public sealed record OcrLanguage(string Tag, string Name);

/// <summary>
/// Reads text in images with the text recognition built into Windows (Windows.Media.Ocr): offline, no
/// download, in the languages installed in Windows' language settings.
/// </summary>
public static class TextRecognizer
{
    public static IReadOnlyList<OcrLanguage> Languages =>
        [.. OcrEngine.AvailableRecognizerLanguages.Select(language => new OcrLanguage(language.LanguageTag, language.DisplayName))];

    /// <summary>The language Windows would pick from the user's language list, or null when none can be read.</summary>
    public static OcrLanguage? DefaultLanguage =>
        OcrEngine.TryCreateFromUserProfileLanguages() is { } engine ? new OcrLanguage(engine.RecognizerLanguage.LanguageTag, engine.RecognizerLanguage.DisplayName) : null;

    /// <summary>The words in an image, with boxes in the image's pixels.</summary>
    public static async Task<List<PageWord>> ReadAsync(Bitmap image, string? languageTag, CancellationToken cancel = default)
    {
        OcrEngine engine = (languageTag is null
            ? OcrEngine.TryCreateFromUserProfileLanguages()
            : OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(languageTag)))
            ?? throw new ToolException("Windows has no text recognition for this language. Add a language with “Optical character recognition” in Windows Settings › Time & language › Language.");

        float scale = Math.Min(1f, OcrEngine.MaxImageDimension / (float)Math.Max(image.Width, image.Height));
        using Bitmap sized = scale < 1f ? new Bitmap(image, (int)(image.Width * scale), (int)(image.Height * scale)) : new Bitmap(image);
        using SoftwareBitmap bitmap = await ToSoftwareBitmapAsync(sized).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        OcrResult result = await engine.RecognizeAsync(bitmap).AsTask(cancel).ConfigureAwait(false);

        var words = new List<PageWord>();
        foreach (OcrLine line in result.Lines)
            foreach (OcrWord word in line.Words)
            {
                var box = word.BoundingRect;
                words.Add(new PageWord(word.Text, new RectangleF((float)box.X / scale, (float)box.Y / scale, (float)box.Width / scale, (float)box.Height / scale)));
            }
        return words;
    }

    static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap image)
    {
        using var memory = new MemoryStream();
        image.Save(memory, ImageFormat.Bmp);
        memory.Position = 0;
        using IRandomAccessStream stream = memory.AsRandomAccessStream();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask().ConfigureAwait(false);
    }
}
