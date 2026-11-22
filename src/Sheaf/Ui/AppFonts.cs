using System.Drawing.Text;
using Sheaf.Core.Fonts;
using Sheaf.Platform;

namespace Sheaf.Ui;

/// <summary>
/// Geist for everything, Geist Mono for numbers that change. Unpacked from the exe once and loaded from files
/// (AddMemoryFont files some weights under the wrong family). Every font is created once here and reused.
/// Sizes are in points so they scale with DPI: 9.75 pt is 13 px at 100 %.
/// </summary>
internal static class AppFonts
{
    static readonly PrivateFontCollection Collection = new();

    static AppFonts()
    {
        FontFamily regular, medium, semiBold, mono, monoMedium, signature;
        try
        {
            foreach (string file in EmbeddedFonts.Files)
                Collection.AddFontFile(Unpack(file));
            regular = Family("Sheaf Geist");
            medium = Family("Sheaf Geist Medium");
            semiBold = Family("Sheaf Geist SemiBold");
            mono = Family("Sheaf Geist Mono");
            monoMedium = Family("Sheaf Geist Mono Medium");
            signature = Family("Sacramento");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ActivityLog.Error("loading the embedded fonts; using the system sans serif", ex);
            regular = medium = semiBold = mono = monoMedium = signature = FontFamily.GenericSansSerif;
        }

        Body = new Font(regular, 9.75f);
        BodyMedium = new Font(medium, 9.75f);
        Strong = new Font(semiBold, 9.75f);
        Small = new Font(regular, 9f);
        SmallMedium = new Font(medium, 9f);
        SmallStrong = new Font(semiBold, 9f);
        Caption = new Font(regular, 8.25f);
        Section = new Font(semiBold, 11.25f);
        Title = new Font(semiBold, 16.5f);
        Display = new Font(semiBold, 22f);
        Mono = new Font(mono, 9f);
        MonoMedium = new Font(monoMedium, 9f);
        MonoSmall = new Font(mono, 8.25f);
        SignatureFamily = signature;
        RegularFamily = regular;
        SemiBoldFamily = semiBold;
        if (Directory.Exists(AddedFolder))
            foreach (string file in Directory.EnumerateFiles(AddedFolder, "*.ttf")) Load(file);
    }

    // Fonts people added for text on pages, each from its own file, by family name.
    static readonly List<PrivateFontCollection> AddedCollections = [];
    static readonly Dictionary<string, FontFamily> Added = new(StringComparer.OrdinalIgnoreCase);

    static string AddedFolder => Path.Combine(AppPaths.FontsFolder, "Added");

    public static IReadOnlyList<string> AddedFamilies => [.. Added.Keys.Order(StringComparer.CurrentCultureIgnoreCase)];

    public static FontFamily? AddedFamily(string name) => Added.GetValueOrDefault(name);

    /// <summary>
    /// Keeps a copy of a TrueType font with the app's data and makes it usable for text, on screen and in saved
    /// PDFs. Returns its family name, or null when the file isn't a font this can use.
    /// </summary>
    public static string? AddFont(string file)
    {
        try
        {
            Directory.CreateDirectory(AddedFolder);
            string copy = Path.Combine(AddedFolder, Path.GetFileName(file));
            if (!File.Exists(copy)) File.Copy(file, copy);
            string? family = Load(copy);
            if (family is null) File.Delete(copy);
            return family;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error($"adding the font {Path.GetFileName(file)}", ex);
            return null;
        }
    }

    static string? Load(string path)
    {
        var collection = new PrivateFontCollection();
        try
        {
            collection.AddFontFile(path);
            if (collection.Families is not [var family, ..]) { collection.Dispose(); return null; }
            if (Added.ContainsKey(family.Name)) { collection.Dispose(); return family.Name; }
            AddedCollections.Add(collection);
            Added[family.Name] = family;
            Sheaf.Core.Fonts.SystemFonts.Register(family.Name, path);
            return family.Name;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            collection.Dispose();
            ActivityLog.Error($"loading the font {Path.GetFileName(path)}", ex);
            return null;
        }
    }

    public static Font Body { get; }
    public static Font BodyMedium { get; }
    public static Font Strong { get; }
    public static Font Small { get; }
    public static Font SmallMedium { get; }
    public static Font SmallStrong { get; }
    public static Font Caption { get; }
    public static Font Section { get; }
    public static Font Title { get; }
    public static Font Display { get; }
    public static Font Mono { get; }
    public static Font MonoMedium { get; }
    public static Font MonoSmall { get; }
    public static FontFamily SignatureFamily { get; }
    public static FontFamily RegularFamily { get; }
    public static FontFamily SemiBoldFamily { get; }

    static FontFamily Family(string name) =>
        Collection.Families.FirstOrDefault(family => family.Name == name)
        ?? throw new InvalidOperationException($"Font family {name} didn’t load.");

    /// <summary>Writes the font next to the settings, only when the bytes differ from what is already there.</summary>
    static string Unpack(string fileName)
    {
        byte[] bytes = EmbeddedFonts.Read(fileName);
        Directory.CreateDirectory(AppPaths.FontsFolder);
        string path = Path.Combine(AppPaths.FontsFolder, fileName);
        var existing = new FileInfo(path);
        if (!existing.Exists || existing.Length != bytes.Length || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(path, bytes);
        return path;
    }
}
