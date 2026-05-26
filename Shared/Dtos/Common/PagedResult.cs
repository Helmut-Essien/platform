namespace Platform.Shared.Dtos.Common;

public class PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public int TotalCount { get; init; }

    public int Page { get; init; }

    public int PageSize { get; init; }
}
