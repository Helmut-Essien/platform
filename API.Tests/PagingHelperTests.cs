using Platform.Api.Helpers;
using Xunit;

namespace API.Tests;

public class PagingHelperTests
{
    [Theory]
    [InlineData(1, 25, 1, 25, 0)]
    [InlineData(0, 25, 1, 25, 0)]
    [InlineData(3, 10, 3, 10, 20)]
    public void Normalize_ClampsPageAndComputesSkip(int page, int pageSize, int expectedPage, int expectedSize, int expectedSkip)
    {
        var (normalizedPage, normalizedPageSize, skip) = PagingHelper.Normalize(page, pageSize);

        Assert.Equal(expectedPage, normalizedPage);
        Assert.Equal(expectedSize, normalizedPageSize);
        Assert.Equal(expectedSkip, skip);
    }

    [Fact]
    public void Normalize_ClampsPageSizeToMax()
    {
        var (_, pageSize, _) = PagingHelper.Normalize(1, 10_000);

        Assert.Equal(PagingHelper.MaxPageSize, pageSize);
    }

    [Theory]
    [InlineData(int.MaxValue, 100)]
    [InlineData(int.MaxValue, 25)]
    [InlineData(int.MinValue, int.MinValue)]
    public void Normalize_HugePageNeverOverflowsSkip(int page, int pageSize)
    {
        var (normalizedPage, normalizedPageSize, skip) = PagingHelper.Normalize(page, pageSize);

        Assert.InRange(normalizedPage, 1, PagingHelper.MaxPage);
        Assert.InRange(normalizedPageSize, 1, PagingHelper.MaxPageSize);
        Assert.True(skip >= 0);
    }
}

public class SearchHelperTests
{
    [Theory]
    [InlineData("100%", "%100\\%%")]
    [InlineData("a_b", "%a\\_b%")]
    [InlineData(@"c:\x", @"%c:\\x%")]
    [InlineData("  plain ", "%plain%")]
    public void ContainsPattern_EscapesLikeWildcards(string input, string expected) =>
        Assert.Equal(expected, Platform.Api.Helpers.SearchHelper.ContainsPattern(input));
}
