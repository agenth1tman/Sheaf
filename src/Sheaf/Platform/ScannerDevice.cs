using System.Runtime.InteropServices;
using Sheaf.Core;

namespace Sheaf.Platform;

/// <summary>
/// Scans a page with a scanner through Windows Image Acquisition, the scanner support built into Windows, using
/// Windows' own scan dialog (paper size, colour and resolution are chosen there).
/// </summary>
internal static class ScannerDevice
{
    const int ScannerDeviceType = 1, ColorIntent = 1, MaximizeQuality = 131072;
    const string PngFormat = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";
    const int NoDevice = unchecked((int)0x80210015), Offline = unchecked((int)0x80210005), Busy = unchecked((int)0x80210006);

    /// <summary>Scans one page into a PNG file and returns its path; null when cancelled.</summary>
    public static string? ScanPage(string folder)
    {
        Type type = Type.GetTypeFromProgID("WIA.CommonDialog")
            ?? throw new ToolException("Scanning isn’t available: Windows Image Acquisition is missing from this PC.");
        object? dialog = null, image = null;
        try
        {
            dialog = Activator.CreateInstance(type);
            image = ((dynamic)dialog!).ShowAcquireImage(ScannerDeviceType, ColorIntent, MaximizeQuality, PngFormat, false, true, false);
            if (image is null) return null;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"scan-{Guid.NewGuid():N}.png");
            ((dynamic)image).SaveFile(path);
            return path;
        }
        catch (COMException ex)
        {
            throw new ToolException(ex.HResult switch
            {
                NoDevice => "No scanner found. Connect one and turn it on, or use Camera or Photos instead.",
                Offline => "The scanner is offline. Turn it on and try again.",
                Busy => "The scanner is busy. Wait for it to finish, then try again.",
                _ => $"The scanner stopped: {ex.Message}",
            }, ex);
        }
        finally
        {
            if (image is not null) Marshal.FinalReleaseComObject(image);
            if (dialog is not null) Marshal.FinalReleaseComObject(dialog);
        }
    }
}
