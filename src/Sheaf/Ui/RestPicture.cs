using System.Drawing.Imaging;

namespace Sheaf.Ui;

/// <summary>
/// A control's look at rest (not hovered, pressed or focused) kept as a picture, so showing a page of many such
/// controls copies pictures instead of drawing their text and shapes again: Home has dozens, and drawing them all
/// held up the tab closing on the way back to it. It is redrawn when the size, theme or DPI changes, or on
/// <see cref="Clear"/> when what it shows changes.
/// </summary>
internal sealed class RestPicture : IDisposable
{
    Bitmap? _picture;
    (Size Size, Palette Palette, int Dpi) _key;

    public void Clear()
    {
        _picture?.Dispose();
        _picture = null;
    }

    /// <summary>Draws the control: from the picture when it is at rest, live while anything is changing it.</summary>
    public void Paint(Control owner, Graphics g, bool atRest, Action<Graphics> paint)
    {
        if (!atRest || owner.Width <= 0 || owner.Height <= 0)
        {
            paint(g);
            return;
        }
        var key = (owner.Size, Theme.Current, owner.DeviceDpi);
        if (_picture is null || _key != key)
        {
            Clear();
            // Opaque: GDI text needs something solid beneath it.
            _picture = new Bitmap(owner.Width, owner.Height, PixelFormat.Format32bppRgb);
            using (Graphics picture = Graphics.FromImage(_picture)) paint(picture);
            _key = key;
        }
        g.DrawImageUnscaled(_picture, 0, 0);
    }

    public void Dispose() => Clear();
}
