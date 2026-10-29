using System.Linq.Expressions;
using Platform.Api.Entities;

namespace Platform.Api.Helpers;

/// <summary>
/// Whitelisted server-side sorting for paged lists. Unknown sort keys are ignored (default order is used),
/// so clients can never sort by arbitrary columns.
/// </summary>
public static class SortHelper
{
    public static readonly IReadOnlyDictionary<string, Expression<Func<Customer, object?>>> CustomerSorts =
        new Dictionary<string, Expression<Func<Customer, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = c => c.Name,
            ["email"] = c => c.ContactEmail,
            ["phone"] = c => c.ContactPhone,
            ["status"] = c => c.IsSuspended,
            ["created"] = c => c.CreatedAt
        };

    public static readonly IReadOnlyDictionary<string, Expression<Func<License, object?>>> LicenseSorts =
        new Dictionary<string, Expression<Func<License, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["customer"] = l => l.Customer.Name,
            ["service"] = l => l.ServiceProduct.Code,
            ["plan"] = l => l.PlanName,
            ["status"] = l => l.Status,
            ["expires"] = l => l.ExpiresAt,
            ["keySent"] = l => l.LicenseKeySentAt,
            ["created"] = l => l.CreatedAt
        };

    public static readonly IReadOnlyDictionary<string, Expression<Func<Invoice, object?>>> InvoiceSorts =
        new Dictionary<string, Expression<Func<Invoice, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["number"] = i => i.InvoiceNumber,
            ["customer"] = i => i.Customer.Name,
            ["total"] = i => i.TotalAmount,
            ["status"] = i => i.Status,
            ["issueDate"] = i => i.IssueDate,
            ["dueDate"] = i => i.DueDate
        };

    /// <summary>Returns true and the key selector when <paramref name="sortBy"/> is whitelisted.</summary>
    public static bool TryGet<T>(
        IReadOnlyDictionary<string, Expression<Func<T, object?>>> whitelist,
        string? sortBy,
        out Expression<Func<T, object?>> selector)
    {
        selector = null!;
        if (string.IsNullOrWhiteSpace(sortBy))
            return false;
        if (!whitelist.TryGetValue(sortBy.Trim(), out var found))
            return false;
        selector = found;
        return true;
    }

    /// <summary>
    /// Applies a whitelisted primary sort, or <paramref name="defaultOrder"/> when the key is unknown/empty.
    /// </summary>
    public static IOrderedQueryable<T> Apply<T>(
        IQueryable<T> query,
        IReadOnlyDictionary<string, Expression<Func<T, object?>>> whitelist,
        string? sortBy,
        bool descending,
        Func<IQueryable<T>, IOrderedQueryable<T>> defaultOrder)
    {
        if (!TryGet(whitelist, sortBy, out var selector))
            return defaultOrder(query);
        return descending ? query.OrderByDescending(selector) : query.OrderBy(selector);
    }
}
