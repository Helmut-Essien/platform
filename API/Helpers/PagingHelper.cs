namespace Platform.Api.Helpers;

public static class PagingHelper
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    public static (int Page, int PageSize, int Skip) Normalize(int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        return (page, pageSize, (page - 1) * pageSize);
    }
}
