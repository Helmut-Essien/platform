using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;

namespace Platform.Api.Services;

/// <summary>
/// The rules that tie <c>License.PlanName</c> to a product's plan catalog (<c>ProductPlan</c>).
/// <para>
/// Migration safety: the catalog is enforced <b>per product, and only once the product has at least one plan
/// defined</b>. A product with no plans keeps accepting free text (trimmed, case preserved), exactly like before the
/// catalog existed, so deploying this change cannot block editing any existing license. Enforcement for a product
/// starts the moment its first plan is added, so seed plans and map the old names in one sitting.
/// </para>
/// </summary>
internal static partial class ProductPlanRules
{
    internal const int MaxCodeLength = 50;

    [GeneratedRegex("^[a-z0-9]+(?:[-_][a-z0-9]+)*$")]
    private static partial Regex CodePattern();

    /// <summary>Trim + lowercase (invariant). The only normalization a plan code ever gets.</summary>
    internal static string NormalizeCode(string? value) => (value ?? "").Trim().ToLowerInvariant();

    internal static bool IsValidCode(string normalized) =>
        normalized.Length is > 0 and <= MaxCodeLength && CodePattern().IsMatch(normalized);

    /// <summary>
    /// Returns the plan name to store for a license of <paramref name="serviceProductId"/>.
    /// <list type="bullet">
    /// <item>Product has no plans: the trimmed text, unchanged otherwise (legacy free text).</item>
    /// <item>Product has plans: the lowercase code of an <b>active</b> plan, or an error.</item>
    /// <item>Exception for edits: when the request names the license's <i>current</i> plan (ignoring case and
    /// surrounding spaces) the current value is kept untouched even if it is not an active plan, so a license on
    /// a deactivated plan, or on an old name not yet mapped, can still have its expiry, devices or price edited.</item>
    /// </list>
    /// </summary>
    internal static async Task<string> ResolveLicensePlanAsync(
        AppDbContext db,
        string serviceProductId,
        string productCode,
        string? requested,
        string? currentPlanName,
        CancellationToken cancellationToken)
    {
        var trimmed = (requested ?? "").Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException("Plan name is required.");

        var plans = await db.ProductPlans
            .AsNoTracking()
            .Where(p => p.ServiceProductId == serviceProductId)
            .Select(p => new { p.Code, p.IsActive })
            .ToListAsync(cancellationToken);

        if (plans.Count == 0)
            return trimmed;

        var code = trimmed.ToLowerInvariant();
        if (plans.Any(p => p.IsActive && p.Code == code))
            return code;

        if (currentPlanName is not null
            && string.Equals(currentPlanName.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return currentPlanName;
        }

        var active = plans.Where(p => p.IsActive).Select(p => p.Code).Order(StringComparer.Ordinal).ToList();
        throw new InvalidOperationException(active.Count == 0
            ? $"{productCode} has no active plans. Add or activate a plan on the service before issuing licenses."
            : $"'{trimmed}' is not an active plan of {productCode}. Choose one of: {string.Join(", ", active)}.");
    }
}
