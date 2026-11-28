using System.Drawing.Text;
using Sheaf.Core.Editing;
using Sheaf.Ui;
using Sheaf.Ui.Controls;
using FormField = Sheaf.Ui.Controls.FormField;

namespace Sheaf.Pages.Document;

/// <summary>
/// Types an equation the quick way (<see cref="MathText"/>): powers with ^, subscripts with _, symbols by name, and a
/// preview of how it will look as it is typed.
/// </summary>
internal sealed class EquationDialog : DialogForm
{
    readonly InputBox _input = Make.Input("e.g. E = mc^2", maxLength: 400);
    readonly Preview _preview = new() { Height = 84 };

    EquationDialog()
        : base("Insert Equation", "Type it the quick way. The preview shows how it will look.", 460)
    {
        AddContent(new FormField("Equation", _input,
            "x^2 or e^{-kt} raises, v_0 or x_{max} lowers, \\frac{a}{b} stacks a fraction. \\alpha \\Delta \\theta \\pi \\mu \\sum \\int \\infty and other names give symbols; " +
            "<= >= != ~= -> +- * sqrt( give ≤ ≥ ≠ ≈ → ± × √("), 18);
        AddContent(_preview, 12);
        _input.Box.TextChanged += (_, _) => _preview.Source = _input.Text;
        AddButtons("Insert", () => _input.Text.Trim().Length > 0);
        Shown += (_, _) => _input.Focus();
    }

    /// <summary>The equation in stretches, raised, lowered or on the line; null when cancelled.</summary>
    public static List<(string Text, TextScript Script)>? Ask(IWin32Window? owner)
    {
        using var dialog = new EquationDialog();
        return dialog.ShowDialog(owner) == DialogResult.OK ? MathText.Parse(dialog._input.Text.Trim()) : null;
    }

    /// <summary>The equation drawn as the text box will draw it, on paper.</summary>
    sealed class Preview : Control
    {
        string _source = "";

        public Preview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.StaticText;
            AccessibleName = "Preview";
        }

        public string Source
        {
            get => _source;
            set
            {
                _source = value;
                AccessibleDescription = MathText.Plain(value);
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Draw.BackgroundOf(this));
            var paper = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.Box(g, paper, LogicalToDeviceUnits(Theme.Radius), Color.White, Theme.Current.Line2);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float size = LogicalToDeviceUnits(20);
            using var normal = new Font(MathText.SymbolFont, size, GraphicsUnit.Pixel);
            using var small = new Font(MathText.SymbolFont, size * TextLayout.ScriptScale, GraphicsUnit.Pixel);
            using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap };
            using var part = new Font(MathText.SymbolFont, size * TextLayout.FractionScale, GraphicsUnit.Pixel);
            float x = LogicalToDeviceUnits(12), top = (Height - normal.GetHeight(g)) / 2;
            var runs = MathText.Parse(_source);
            for (int i = 0; i < runs.Count; i++)
            {
                var (text, script) = runs[i];
                if (script == TextScript.Over && i + 1 < runs.Count && runs[i + 1].Script == TextScript.Under)
                {
                    // A fraction, stacked as the page shows it: the same proportions as TextLayout's.
                    string below = runs[++i].Text;
                    float over = g.MeasureString(text, part, PointF.Empty, format).Width, under = g.MeasureString(below, part, PointF.Empty, format).Width;
                    float pad = size * 0.1f, across = Math.Max(over, under) + pad * 2;
                    g.DrawString(text, part, Brushes.Black, x + (across - over) / 2, top - size * 0.17f, format);
                    g.DrawString(below, part, Brushes.Black, x + (across - under) / 2, top + size * 0.68f, format);
                    using var bar = new Pen(Color.Black, Math.Max(1, size * 0.05f));
                    g.DrawLine(bar, x + pad / 2, top + size * 0.62f, x + across - pad / 2, top + size * 0.62f);
                    x += across;
                    continue;
                }
                Font font = script == TextScript.Normal ? normal : script is TextScript.Over or TextScript.Under ? part : small;
                float y = top + (script == TextScript.Sub ? size * 0.5f : 0);
                g.DrawString(text, font, Brushes.Black, x, y, format);
                x += g.MeasureString(text, font, PointF.Empty, format).Width;
            }
        }
    }
}
