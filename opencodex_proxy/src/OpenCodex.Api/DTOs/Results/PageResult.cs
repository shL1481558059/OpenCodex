namespace OpenCodex.Api.DTOs.Results;

public sealed class PageResult<T>
{
    public PageResult(
        IReadOnlyList<T> items,
        int page,
        int pageSize,
        long totalCount,
        int totalPages)
    {
        Items = items;
        Page = page;
        PageSize = pageSize;
        TotalCount = totalCount;
        TotalPages = totalPages;
    }

    public IReadOnlyList<T> Items { get; }

    public int Page { get; }

    public int PageSize { get; }

    public long TotalCount { get; }

    public int TotalPages { get; }

    public static PageResult<T> Create(
        IReadOnlyList<T> items,
        int page,
        int pageSize,
        long totalCount)
    {
        var safePageSize = pageSize <= 0 ? 1 : pageSize;
        var totalPages = totalCount <= 0
            ? 0
            : (int)Math.Ceiling(totalCount / (double)safePageSize);

        return new PageResult<T>(
            items,
            page <= 0 ? 1 : page,
            safePageSize,
            totalCount < 0 ? 0 : totalCount,
            totalPages);
    }
}
