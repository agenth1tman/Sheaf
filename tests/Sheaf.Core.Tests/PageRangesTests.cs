using Sheaf.Core.Pdf;

namespace Sheaf.Core.Tests;

public class PageRangesTests
{
    [Fact]
    public void ParsesSinglesRangesAndOpenEnds()
    {
        Assert.True(PageRanges.TryParse("1-3, 5;  8 – ", out var spans, out _));
        Assert.Equal([new PageSpan(1, 3), new PageSpan(5, 5), new PageSpan(8, PageSpan.ToEnd)], spans);
    }

    [Fact]
    public void ParsesRangeFromStart()
    {
        Assert.True(PageRanges.TryParse("-4", out var spans, out _));
        Assert.Equal([new PageSpan(1, 4)], spans);
    }

    [Theory]
    [InlineData("", "Enter the pages")]
    [InlineData("1-2-3", "isn’t a page")]
    [InlineData("abc", "isn’t a page")]
    [InlineData("0", "start at 1")]
    [InlineData("9-4", "runs backwards")]
    public void RejectsBadInput(string text, string messagePart)
    {
        Assert.False(PageRanges.TryParse(text, out _, out string error));
        Assert.Contains(messagePart, error);
    }

    [Fact]
    public void FitClosesOpenRangesAndRejectsMissingPages()
    {
        PageRanges.TryParse("2-, 1", out var spans, out _);
        Assert.Equal([new PageSpan(2, 6), new PageSpan(1, 1)], PageRanges.Fit(spans, 6, "a.pdf"));

        PageRanges.TryParse("3-9", out var tooFar, out _);
        var error = Assert.Throws<ToolException>(() => PageRanges.Fit(tooFar, 6, "a.pdf"));
        Assert.Contains("no page 9", error.Message);
    }

    [Fact]
    public void IndexesKeepOrderAndDropRepeats()
    {
        Assert.Equal([4, 0, 1, 2], PageRanges.ToIndexes([new PageSpan(5, 5), new PageSpan(1, 3), new PageSpan(2, 2)]));
    }
}
