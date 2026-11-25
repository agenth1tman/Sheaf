using System.Drawing.Drawing2D;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Document;

/// <summary>Crops a picture: drag the edges or corners of the box, or drag inside it to move it.</summary>
internal sealed class ImageCropDialog : DialogForm
{
    readonly CropBox _box;
    readonly ChoiceGroup<float> _shape = new("Shape", horizontal: true, ("Free", 0f), ("Square", 1f), ("4:3", 4 / 3f), ("16:9", 16 / 9f));

    ImageCropDialog(Image image, string title = "Crop Image", RectangleF? start = null) : base(title, "Drag the edges or corners of the box, or drag inside it to move it. Arrow keys move it; with Shift they resize it.", 480)
    {
        _box = new CropBox(image) { Height = 320 };
        if (start is { } part) _box.Start(part);
        AddContent(_box);
        AddContent(new FormField("Shape", _shape), 14);
        AddButtons("Crop");
        _shape.Changed += (_, _) => _box.Aspect = _shape.Value;
        Shown += (_, _) => _box.Focus();
    }

    /// <summary>The part of the picture to keep, as fractions of its width and height; null when cancelled or unchanged.</summary>
    public static RectangleF? Ask(IWin32Window? owner, Image image)
    {
        using var dialog = new ImageCropDialog(image);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        RectangleF part = dialog._box.Part;
        return part.Width > 0.999f && part.Height > 0.999f ? null : part;
    }

    /// <summary>
    /// The part of something to keep, starting from the part kept now, as fractions of its width and height; null
    /// only when cancelled (the whole of it is a choice too: it takes a crop off).
    /// </summary>
    public static RectangleF? AskPart(IWin32Window? owner, Image picture, string title, RectangleF start)
    {
        using var dialog = new ImageCropDialog(picture, title, start);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._box.Part : null;
    }

    /// <summary>The picture with the crop box over it; outside the box is dimmed.</summary>
    sealed class CropBox : Control
    {
        const float Smallest = 0.04f;
        readonly Image _image;
        RectangleF _part = new(0, 0, 1, 1);
        float _aspect;
        int _grip = -1;
        PointF _pressPart;
        RectangleF _pressRect;

        public CropBox(Image image)
        {
            _image = image;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "Crop box";
        }

        public RectangleF Part => _part;

        public void Start(RectangleF part) => _part = RectangleF.Intersect(part, new RectangleF(0, 0, 1, 1));

        /// <summary>Width over height the box keeps, in the picture's pixels; 0 for any shape.</summary>
        public float Aspect
        {
            get => _aspect;
            set
            {
                _aspect = value;
                if (value > 0) _part = Fit(_part);
                Changed();
            }
        }

        float PixelAspect => _image.Width / (float)Math.Max(1, _image.Height);

        /// <summary>The largest box of the chosen shape that fits inside <paramref name="within"/>, centred on it.</summary>
        RectangleF Fit(RectangleF within)
        {
            float ratio = _aspect / PixelAspect;
            float width = within.Width, height = width / ratio;
            if (height > within.Height) (height, width) = (within.Height, within.Height * ratio);
            return new RectangleF(within.X + (within.Width - width) / 2, within.Y + (within.Height - height) / 2, width, height);
        }

        RectangleF ImageArea
        {
            get
            {
                float scale = Math.Min((Width - 16f) / _image.Width, (Height - 16f) / _image.Height);
                float width = _image.Width * scale, height = _image.Height * scale;
                return new RectangleF((Width - width) / 2, (Height - height) / 2, width, height);
            }
        }

        RectangleF Box
        {
            get
            {
                RectangleF area = ImageArea;
                return new RectangleF(area.X + _part.X * area.Width, area.Y + _part.Y * area.Height, _part.Width * area.Width, _part.Height * area.Height);
            }
        }

        // Grips: 0-3 corners (top left, top right, bottom right, bottom left), 4-7 edges (top, right, bottom, left).
        PointF[] Grips()
        {
            RectangleF b = Box;
            float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
            return [new(b.Left, b.Top), new(b.Right, b.Top), new(b.Right, b.Bottom), new(b.Left, b.Bottom),
                    new(cx, b.Top), new(b.Right, cy), new(cx, b.Bottom), new(b.Left, cy)];
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Current.Canvas);
            RectangleF area = ImageArea, box = Box;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(_image, area);
            using (var dim = new Region(area))
            {
                dim.Exclude(box);
                using var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
                g.FillRegion(shade, dim);
            }
            using (var thirds = new Pen(Color.FromArgb(110, 255, 255, 255), 1))
                for (int i = 1; i < 3; i++)
                {
                    g.DrawLine(thirds, box.X + box.Width * i / 3, box.Top, box.X + box.Width * i / 3, box.Bottom);
                    g.DrawLine(thirds, box.Left, box.Y + box.Height * i / 3, box.Right, box.Y + box.Height * i / 3);
                }
            using (var edge = new Pen(Color.White, 1.5f)) g.DrawRectangle(edge, box.X, box.Y, box.Width, box.Height);
            float size = LogicalToDeviceUnits(9);
            foreach (PointF grip in Grips())
            {
                var handle = new RectangleF(grip.X - size / 2, grip.Y - size / 2, size, size);
                g.FillRectangle(Brushes.White, handle);
                g.DrawRectangle(Pens.Black, handle.X, handle.Y, handle.Width, handle.Height);
            }
            if (Focused && ShowFocusCues) Draw.FocusRing(g, Rectangle.Round(box), 0, LogicalToDeviceUnits(2));
        }

        int GripAt(Point point)
        {
            PointF[] grips = Grips();
            float reach = LogicalToDeviceUnits(10);
            for (int i = 0; i < grips.Length; i++)
                if (Math.Abs(point.X - grips[i].X) <= reach && Math.Abs(point.Y - grips[i].Y) <= reach) return i;
            return Box.Contains(point) ? 8 : -1;
        }

        PointF ToPart(Point point)
        {
            RectangleF area = ImageArea;
            return new PointF((point.X - area.X) / area.Width, (point.Y - area.Y) / area.Height);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            _grip = GripAt(e.Location);
            _pressPart = ToPart(e.Location);
            _pressRect = _part;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_grip < 0 || e.Button != MouseButtons.Left)
            {
                Cursor = GripAt(e.Location) switch { 0 or 2 => Cursors.SizeNWSE, 1 or 3 => Cursors.SizeNESW, 4 or 6 => Cursors.SizeNS, 5 or 7 => Cursors.SizeWE, 8 => Cursors.SizeAll, _ => Cursors.Default };
                return;
            }
            PointF at = ToPart(e.Location);
            float dx = at.X - _pressPart.X, dy = at.Y - _pressPart.Y;
            _part = _grip == 8 ? Shift(_pressRect, dx, dy) : Drag(_pressRect, _grip, dx, dy);
            Changed();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _grip = -1;
        }

        static RectangleF Shift(RectangleF rect, float dx, float dy) =>
            new(Math.Clamp(rect.X + dx, 0, 1 - rect.Width), Math.Clamp(rect.Y + dy, 0, 1 - rect.Height), rect.Width, rect.Height);

        /// <summary>Moves the edges a grip holds; with a shape chosen, the other side follows to keep it.</summary>
        RectangleF Drag(RectangleF rect, int grip, float dx, float dy)
        {
            float left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom;
            if (grip is 0 or 3 or 7) left = Math.Clamp(left + dx, 0, right - Smallest);
            if (grip is 1 or 2 or 5) right = Math.Clamp(right + dx, left + Smallest, 1);
            if (grip is 0 or 1 or 4) top = Math.Clamp(top + dy, 0, bottom - Smallest);
            if (grip is 2 or 3 or 6) bottom = Math.Clamp(bottom + dy, top + Smallest, 1);
            var moved = RectangleF.FromLTRB(left, top, right, bottom);
            if (_aspect <= 0) return moved;
            // Keep the shape: the dragged side decides the size, the box stays inside the picture.
            float ratio = _aspect / PixelAspect;
            bool byWidth = grip is 5 or 7 || (grip < 4 && Math.Abs(dx) >= Math.Abs(dy));
            float width = byWidth ? moved.Width : moved.Height * ratio, height = width / ratio;
            float x = grip is 0 or 3 or 7 ? moved.Right - width : grip is 4 or 6 ? moved.X + (moved.Width - width) / 2 : moved.X;
            float y = grip is 0 or 1 or 4 ? moved.Bottom - height : grip is 5 or 7 ? moved.Y + (moved.Height - height) / 2 : moved.Y;
            var shaped = new RectangleF(x, y, width, height);
            return shaped.Left >= -0.0001f && shaped.Top >= -0.0001f && shaped.Right <= 1.0001f && shaped.Bottom <= 1.0001f && width >= Smallest && height >= Smallest ? shaped : rect;
        }

        protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            const float Step = 0.01f;
            float dx = e.KeyCode switch { Keys.Left => -Step, Keys.Right => Step, _ => 0 };
            float dy = e.KeyCode switch { Keys.Up => -Step, Keys.Down => Step, _ => 0 };
            if (dx == 0 && dy == 0) return;
            _part = e.Shift ? Drag(_part, 2, dx, dy) : Shift(_part, dx, dy);
            Changed();
        }

        void Changed()
        {
            Invalidate();
            AccessibleDescription = $"Keeps {Math.Round(_part.Width * 100)} % of the width and {Math.Round(_part.Height * 100)} % of the height";
            AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }
}
