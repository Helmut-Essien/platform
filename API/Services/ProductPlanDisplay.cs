using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;

namespace Platform.Api.Services;

/// <summary>
/// What a person reads for a license's plan. <c>License.PlanName</c> stores the plan <i>code</i> (the contract with
/// products: validate answer and token claim); wherever a human reads it, the product's plan <i>display name</i> is
/// shown instead. A name that is not exactly a plan code of that product (an unmapped legacy name such as "Pro", a
/// deleted plan, a product without plans) is shown as stored. Inactive plans still show their display name.
/// </summary>
internal static class ProductPlanDisplay
{
    /// <summary>Display name for one license: a single indexed lookup (unique index product + code).</summary>
    internal static async Task<string> NameAsync(
        AppDbContext db, string serviceProductId, string planName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(planName))
            return planName;
        var display = await db.ProductPlans.AsNoTracking()
            .Where(p => p.ServiceProductId == serviceProductId && p.Code == planName)
            .Select(p => p.DisplayName)
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(display) ? planName : display;
    }

    /// <summary>code -> display name for every plan of the given products, in one query (no per-row lookups).</summary>
    internal static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadAsync(
        AppDbContext db, IEnumerable<string> serviceProductIds, CancellationToken cancellationToken)
    {
        var ids = serviceProductIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var rows = await db.ProductPlans.AsNoTracking()
            .Where(p => ids.Contains(p.ServiceProductId))
            .Select(p => new { p.ServiceProductId, p.Code, p.DisplayName })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => r.ServiceProductId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, string>)g.ToDictionary(r => r.Code, r => r.DisplayName, StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    internal static string Resolve(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> lookup, string serviceProductId, string planName) =>
        lookup.TryGetValue(serviceProductId, out var plans)
        && plans.TryGetValue(planName, out var display)
        && !string.IsNullOrWhiteSpace(display)
            ? display
            : planName;
}
