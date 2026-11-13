using System.Text;

using Olymp.WebApp.Components.Staff;

using Xunit;

namespace Olymp.Tests.WebApp.Staff;

public class TestPreviewTests
{
    [Fact]
    public void SmallTextRetainsWhitespaceAndLineEndings()
    {
        const string input = "  text\r\n\tvalue\n";
        var preview = TestPreview.Create(Encoding.UTF8.GetBytes(input), Encoding.UTF8.GetByteCount(input));
        Assert.Equal(input, preview.Text);
        Assert.False(preview.Truncated);
    }

    [Fact]
    public void LimitsLinesAndDoesNotRejectUtf8SplitAtPrefixBoundary()
    {
        var lines = TestPreview.Create(Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Repeat("line", 300))), 1499);
        Assert.True(lines.Truncated);
        Assert.Equal(200, lines.Text!.Split('\n').Length);
        byte[] data = Encoding.UTF8.GetBytes(new string('a', TestPreview.ByteLimit - 1) + "Я");
        var prefix = TestPreview.Create(data[..TestPreview.ByteLimit], data.Length);
        Assert.NotNull(prefix.Text);
        Assert.Equal(TestPreview.ByteLimit - 1, prefix.Text.Length);
        Assert.True(prefix.Truncated);
        Assert.Null(TestPreview.Create([0, 1, 2], 3).Text);
    }
}
