using System.Drawing;
using Sheaf.Core.Editing;
using Sheaf.Core.Rendering;

namespace Sheaf.Core.Tests;

public class MathTextTests
{
    [Fact]
    public void PowersAndSubscriptsAreRaisedAndLowered()
    {
        Assert.Equal([("E = mc", TextScript.Normal), ("2", TextScript.Super)], MathText.Parse("E = mc^2"));
        Assert.Equal([("v", TextScript.Normal), ("0", TextScript.Sub), (" + at", TextScript.Normal)], MathText.Parse("v_0 + at"));
        Assert.Equal([("e", TextScript.Normal), ("-kt", TextScript.Super)], MathText.Parse("e^{-kt}"));
        Assert.Equal([("x", TextScript.Normal), ("max", TextScript.Sub)], MathText.Parse("x_{max}"));
    }

    [Fact]
    public void NamesAndKeyPairsBecomeSymbols()
    {
        Assert.Equal("α + β ≤ π", MathText.Plain(@"\alpha + \beta <= \pi"));
        Assert.Equal("Δ E ≈ 2 × 10³", MathText.Plain(@"\Delta E ~= 2 * 10³"));
        Assert.Equal("x ≠ y → √(z) ± 1", MathText.Plain("x != y -> sqrt(z) +- 1"));
        Assert.Equal([("λ", TextScript.Normal), ("θ", TextScript.Sub)], MathText.Parse(@"\lambda_\theta"));
        // An unknown name and an escaped character are left as typed.
        Assert.Equal(@"\foo ^", MathText.Plain(@"\foo \^"));
    }

    [Fact]
    public void FractionsStackTheirTopOverTheirBottom()
    {
        Assert.Equal([("v = ", TextScript.Normal), ("d", TextScript.Over), ("t", TextScript.Under)], MathText.Parse(@"v = \frac{d}{t}"));
        // Spaces inside don't break the fraction; scripts inside stay in it; two side by side stay two.
        Assert.Equal([("a + b", TextScript.Over), ("2", TextScript.Under)], MathText.Parse(@"\frac{a + b}{2}"));
        Assert.Equal([("x2", TextScript.Over), ("y", TextScript.Under)], MathText.Parse(@"\frac{x^2}{y}"));
        Assert.Equal(4, MathText.Parse(@"\frac{1}{2}\frac{3}{4}").Count);

        var item = new PageObject
        {
            Kind = MarkKind.Text, FontSize = 10, Text = "x=abcd",
            Spans = [new TextSpan { Length = 2 }, new TextSpan { Length = 1, Script = TextScript.Over }, new TextSpan { Length = 3, Script = TextScript.Under }],
        };
        // Every character one point wide, every line ten high.
        TextLine line = Assert.Single(TextLayout.Lay(item, 100, (text, _) => text.Length, _ => 10));
        TextPiece over = line.Pieces.Single(piece => piece.Style.Script == TextScript.Over && !piece.Bar);
        TextPiece under = line.Pieces.Single(piece => piece.Style.Script == TextScript.Under);
        TextPiece bar = line.Pieces.Single(piece => piece.Bar);
        // Centred on each other, the bar under both, and the line taller to make room.
        Assert.Equal(over.X + over.Width / 2, under.X + under.Width / 2, 3);
        Assert.True(bar.X <= under.X && bar.X + bar.Width >= under.X + under.Width);
        Assert.True(line.Lead > 0 && line.Height > 10);
    }

    [Fact]
    public async Task AFractionIsDrawnStackedInThePdf()
    {
        using var temp = new TempFolder();
        EditDocument edit = DocumentStore.CreateBlank(temp.File("work"), 200, 200);
        var runs = MathText.Parse(@"\frac{1}{2}");
        edit.Pages[0].Objects.Add(new PageObject
        {
            Kind = MarkKind.Text, X = 20, Y = 20, Width = 160, Height = 160, FontSize = 80, StrokeColor = Color.Black.ToArgb(),
            Text = string.Concat(runs.Select(run => run.Text)),
            Spans = [.. runs.Select(run => new TextSpan { Length = run.Text.Length, Script = run.Script, Color = Color.Black.ToArgb() })],
        });
        string path = temp.File("fraction.pdf");
        DocumentStore.Save(edit, path, keepEditable: false);
        using var preview = await PdfPreview.OpenAsync(path);
        using Bitmap page = await preview.RenderAsync(0, 200);
        // Ink in three bands down the page: the 1, the bar, the 2, with paper between.
        var rows = Enumerable.Range(0, page.Height).Select(y => Enumerable.Range(0, page.Width).Any(x => page.GetPixel(x, y).GetBrightness() < 0.4f)).ToList();
        int bands = rows.Where((ink, y) => ink && (y == 0 || !rows[y - 1])).Count();
        Assert.Equal(3, bands);
    }

    [Fact]
    public void RaisedTextAndSymbolsKeepTheirStyleInTheLayout()
    {
        var item = new PageObject
        {
            Kind = MarkKind.Text, Text = "x2 α",
            Spans = [new TextSpan { Length = 1 }, new TextSpan { Length = 1, Script = TextScript.Super }, new TextSpan { Length = 2, Font = MathText.SymbolFont }],
        };
        Assert.True(TextLayout.IsMixed(item));
        var runs = TextLayout.Runs(item);
        Assert.Equal(TextScript.Super, runs[1].Style.Script);
        Assert.Equal(MathText.SymbolFont, TextLayout.FamilyOf(item, runs[2].Style));
        Assert.Equal(item.FontSize * TextLayout.ScriptScale, TextLayout.SizeOf(item, runs[1].Style), 3);
        Assert.Equal(0, TextLayout.Drop(item, runs[1].Style));
        Assert.True(TextLayout.Drop(item, runs[1].Style with { Script = TextScript.Sub }) > 0);
    }

    [Fact]
    public async Task AnEquationIsSavedAndComesBack()
    {
        using var temp = new TempFolder();
        EditDocument edit = DocumentStore.CreateBlank(temp.File("work"), 300, 200);
        var runs = MathText.Parse(@"\Delta x^2");
        var item = new PageObject
        {
            Kind = MarkKind.Text, X = 20, Y = 20, Width = 260, Height = 120, FontSize = 60, StrokeColor = Color.Black.ToArgb(),
            Text = string.Concat(runs.Select(run => run.Text)),
            Spans = [.. runs.Select(run => new TextSpan { Length = run.Text.Length, Script = run.Script, Font = MathText.SymbolFont, Color = Color.Black.ToArgb() })],
        };
        edit.Pages[0].Objects.Add(item);
        string path = temp.File("equation.pdf");
        DocumentStore.Save(edit, path, keepEditable: true);

        using var preview = await PdfPreview.OpenAsync(path);
        using Bitmap page = await preview.RenderAsync(0, 300);
        bool drawn = false;
        for (int y = 0; y < page.Height && !drawn; y++)
            for (int x = 0; x < page.Width && !drawn; x++) drawn = page.GetPixel(x, y).GetBrightness() < 0.3f;
        Assert.True(drawn, "The equation is drawn.");
        PageObject back = Assert.Single(DocumentStore.Open(path, temp.File("again"), null).Pages[0].Objects);
        Assert.Equal(TextScript.Super, back.Spans[^1].Script);
        Assert.Equal(MathText.SymbolFont, back.Spans[0].Font);
    }
}
