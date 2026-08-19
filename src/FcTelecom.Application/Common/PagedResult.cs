using Microsoft.EntityFrameworkCore;

namespace FcTelecom.Application.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < TotalPages;

    public static PagedResult<T> Empty(int pageSize = 50) => new([], 0, 1, pageSize);
}

public static class QueryableExtensions
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;

    /// <summary>
    /// Applies paging and returns the page together with the total count.
    /// </summary>
    /// <remarks>
    /// The page size is clamped rather than trusted. An unclamped page size is a denial of
    /// service with a friendly query-string interface — <c>?pageSize=1000000</c> on a list
    /// view will find out how much memory the app tier has.
    /// </remarks>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);

        int total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        List<T> items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<T>(items, total, page, pageSize);
    }
}

/// <summary>
/// Raised when a caller is authenticated but lacks the permission for what they asked for.
/// Translated to a 403 at the endpoint layer, and always logged as a security event.
/// </summary>
public sealed class PermissionDeniedException(string permission)
    : Exception($"The current user does not hold the '{permission}' permission.")
{
    public string Permission { get; } = permission;
}

/// <summary>Raised when a requested record does not exist, or exists and the caller may not see it.</summary>
/// <remarks>
/// Deliberately the same exception for both cases. Distinguishing "not found" from
/// "forbidden" tells an attacker that a record with that ID exists, which is information
/// they did not have a moment ago.
/// </remarks>
public sealed class RecordNotFoundException(string entityType, object id)
    : Exception($"No {entityType} was found with ID '{id}'.")
{
    public string EntityType { get; } = entityType;

    public object Id { get; } = id;
}
