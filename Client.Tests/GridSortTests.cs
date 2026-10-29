using MudBlazor;
using Platform.Client.Helpers;
using Xunit;

namespace Client.Tests;

public class GridSortTests
{
    private static readonly Dictionary<string, string> Map =
        new(StringComparer.OrdinalIgnoreCase) { ["ContactEmail"] = "email", ["Name"] = "name" };

    private static GridState<object> State(string sortBy, bool desc) => new()
    {
        SortDefinitions = [new SortDefinition<object>(sortBy, desc, 0, _ => null!)]
    };

    [Fact]
    public void NoSort_ReturnsNull() =>
        Assert.Equal((null, false), GridSort.From(new GridState<object>(), Map));

    [Theory]
    [InlineData("ContactEmail", true, "email")]
    [InlineData("contactemail", false, "email")]
    public void KnownColumn_MapsToApiKey(string column, bool desc, string expected) =>
        Assert.Equal((expected, desc), GridSort.From(State(column, desc), Map));

    [Fact]
    public void UnknownColumn_IsIgnored() =>
        Assert.Equal((null, false), GridSort.From(State("3f2a-guid", true), Map));
}
