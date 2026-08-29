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
}
