namespace Platform.Api.Helpers;

public static class PagingHelper
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    public const int MaxPage = int.MaxValue / MaxPageSize;

    public static (int Page, int PageSize, int Skip) Normalize(int page, int pageSize)
    {
        // Cap the page so (page - 1) * pageSize cannot overflow into a negative OFFSET (500 from Postgres).
        page = Math.Clamp(page, 1, MaxPage);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        return (page, pageSize, (page - 1) * pageSize);
    }
}
