using System.Collections.Concurrent;
using Microsoft.Win32;
using PdfSharp.Fonts;

namespace Sheaf.Core.Fonts;

/// <summary>
/// Geist and Geist Mono (SIL Open Font License 1.1) for the interface and for text Sheaf writes on pages, and
/// Sacramento (OFL, unmodified because its name is reserved) for typed signatures. Geist's internal family
/// names carry a "Sheaf " prefix so an installed Geist is never picked instead.
/// </summary>
public static class EmbeddedFonts
{
    public const string Regular = "Geist-Regular.ttf";
    public const string Medium = "Geist-Medium.ttf";
    public const string SemiBold = "Geist-SemiBold.ttf";
    public const string Bold = "Geist-Bold.ttf";
    public const string Mono = "GeistMono-Regular.ttf";
    public const string MonoMedium = "GeistMono-Medium.ttf";
    public const string Signature = "Sacramento-Regular.ttf";

    /// <summary>Family names to use with PDFsharp's XFont.</summary>
    public const string PdfFamily = "Geist";
    public const string PdfMediumFamily = "Geist Medium";
    public const string PdfSemiBoldFamily = "Geist SemiBold";
    public const string PdfMonoFamily = "Geist Mono";
    public const string PdfSignatureFamily = "Sacramento";

    public static IReadOnlyList<string> Files { get; } = [Regular, Medium, SemiBold, Bold, Mono, MonoMedium, Signature];

    static readonly ConcurrentDictionary<string, byte[]> Cache = new();
    static readonly Lock ResolverLock = new();
    static bool _resolverSet;

    public static byte[] Read(string fileName) => Cache.GetOrAdd(fileName, static name =>
    {
        using Stream stream = typeof(EmbeddedFonts).Assembly.GetManifestResourceStream("Fonts." + name)
            ?? throw new InvalidOperationException($"Embedded font {name} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    /// <summary>Lets PDFsharp draw text with the embedded fonts, or a font installed in Windows. Safe to call more than once.</summary>
    public static void UseForPdfOutput()
    {
        lock (ResolverLock)
        {
            if (_resolverSet) return;
            GlobalFontSettings.FontResolver = new PdfFontResolver();
            _resolverSet = true;
        }
    }

    sealed class PdfFontResolver : IFontResolver
    {
        const string SystemPrefix = "system:";

        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic)
        {
            if (familyName.Equals(PdfSignatureFamily, StringComparison.OrdinalIgnoreCase))
                return new FontResolverInfo(Signature, mustSimulateBold: bold, mustSimulateItalic: italic);
            if (familyName.Equals(PdfMediumFamily, StringComparison.OrdinalIgnoreCase))
                return new FontResolverInfo(bold ? SemiBold : Medium, mustSimulateBold: false, mustSimulateItalic: italic);
            if (familyName.Equals(PdfSemiBoldFamily, StringComparison.OrdinalIgnoreCase))
                return new FontResolverInfo(bold ? Bold : SemiBold, mustSimulateBold: false, mustSimulateItalic: italic);
            if (familyName.Equals(PdfMonoFamily, StringComparison.OrdinalIgnoreCase))
                return new FontResolverInfo(bold ? MonoMedium : Mono, mustSimulateBold: false, mustSimulateItalic: italic);
            if (!familyName.Equals(PdfFamily, StringComparison.OrdinalIgnoreCase) && SystemFonts.Find(familyName, bold, italic) is { } file)
                return new FontResolverInfo(SystemPrefix + file.Path, mustSimulateBold: bold && !file.IsBold, mustSimulateItalic: italic && !file.IsItalic);
            return new FontResolverInfo(bold ? Bold : Regular, mustSimulateBold: false, mustSimulateItalic: italic);
        }

        public byte[] GetFont(string faceName) =>
            faceName.StartsWith(SystemPrefix, StringComparison.Ordinal)
                ? File.ReadAllBytes(faceName[SystemPrefix.Length..])
                : Read(faceName);
    }
}

/// <summary>Finds the file of a font installed in Windows, for text people add to their own documents.</summary>
public static class SystemFonts
{
    public sealed record FontFile(string Path, bool IsBold, bool IsItalic);

    static readonly Lazy<Dictionary<string, string>> Registered = new(ReadRegistry);
    static readonly Lock RegisteredLock = new();

    /// <summary>Adds a font file people chose themselves, under its family name.</summary>
    public static void Register(string family, string path)
    {
        lock (RegisteredLock) Registered.Value[family] = path;
    }

    /// <summary>Families that make sense for document text and are installed here, in a fixed order.</summary>
    public static IReadOnlyList<string> DocumentFamilies { get; } =
        [.. new[] { "Arial", "Calibri", "Cambria", "Georgia", "Segoe UI", "Times New Roman", "Verdana", "Courier New" }
            .Where(family => Find(family, bold: false, italic: false) is not null)];

    public static FontFile? Find(string family, bool bold, bool italic)
    {
        string style = (bold, italic) switch { (true, true) => " Bold Italic", (true, false) => " Bold", (false, true) => " Italic", _ => "" };
        if (Lookup(family + style) is { } exact) return new FontFile(exact, bold, italic);
        return Lookup(family) is { } plain ? new FontFile(plain, false, false) : null;
    }

    static string? Lookup(string name)
    {
        lock (RegisteredLock) return Registered.Value.GetValueOrDefault(name);
    }

    /// <summary>"Arial Bold (TrueType)" → C:\Windows\Fonts\arialbd.ttf. Font collections (.ttc) are skipped.</summary>
    static Dictionary<string, string> ReadRegistry()
    {
        var fonts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        // Fonts installed for everyone, then those installed for this user only (their paths are full).
        foreach (RegistryKey root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using RegistryKey? key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (key is null) continue;
            foreach (string valueName in key.GetValueNames())
            {
                if (key.GetValue(valueName) is not string file || !file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
                string name = valueName.Replace("(TrueType)", "", StringComparison.OrdinalIgnoreCase).Trim();
                string path = Path.IsPathRooted(file) ? file : Path.Combine(folder, file);
                if (File.Exists(path)) fonts.TryAdd(name, path);
            }
        }
        return fonts;
    }
}
