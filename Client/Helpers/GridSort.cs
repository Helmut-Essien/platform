using MudBlazor;

namespace Platform.Client.Helpers;

/// <summary>Maps the data grid's active sort column to an API sort key (the API whitelists keys again).</summary>
public static class GridSort
{
    /// <param name="state">Grid state passed to ServerData.</param>
    /// <param name="columnToKey">Column title or property name to API sort key; case-insensitive.</param>
    /// <param name="columns">
    /// The grid's rendered columns. MudBlazor TemplateColumns sort under a random GUID property name,
    /// so the column is looked up to resolve its Title.
    /// </param>
    public static (string? SortBy, bool Descending) From<T>(
        GridState<T> state,
        IReadOnlyDictionary<string, string> columnToKey,
        IEnumerable<Column<T>>? columns = null)
    {
        var definition = state.SortDefinitions?.FirstOrDefault();
        if (definition is null || string.IsNullOrEmpty(definition.SortBy))
            return (null, false);

        var title = columns?.FirstOrDefault(c => c.PropertyName == definition.SortBy)?.Title;
        if (!string.IsNullOrEmpty(title) && columnToKey.TryGetValue(title, out var byTitle))
            return (byTitle, definition.Descending);

        return columnToKey.TryGetValue(definition.SortBy, out var key)
            ? (key, definition.Descending)
            : (null, false);
    }
}
