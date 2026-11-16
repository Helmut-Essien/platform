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

    /// <summary>Trim + lowercase A-Z only. The only normalization a plan code ever gets. ASCII-only on purpose:
    /// culture-aware lowering would fold look-alikes (Kelvin sign, dotted capital I) onto ordinary letters.</summary>
    internal static string NormalizeCode(string? value) => ToLowerAscii((value ?? "").Trim());

    internal static string ToLowerAscii(string value)
    {
        foreach (var c in value)
        {
            if (c is >= 'A' and <= 'Z')
                return string.Create(value.Length, value, static (span, v) =>
                {
                    for (var i = 0; i < v.Length; i++)
                        span[i] = v[i] is >= 'A' and <= 'Z' ? (char)(v[i] + 32) : v[i];
                });
        }
        return value;
    }

    /// <summary>
    /// Row-locks the product's plans so deleting or renaming/deactivating a plan cannot interleave with a license
    /// being created or edited onto it: license create and update take a shared lock (inside their transaction),
    /// plan deletion an exclusive one. Lock order everywhere: plan rows first, then license rows. A no-op outside a
    /// transaction or on a non-relational database.
    /// </summary>
    internal static async Task LockPlansAsync(AppDbContext db, string serviceProductId, bool exclusive, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is null)
            return;
        if (exclusive)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "ProductPlans" WHERE "ServiceProductId" = {serviceProductId} FOR UPDATE""", cancellationToken);
        else
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "ProductPlans" WHERE "ServiceProductId" = {serviceProductId} FOR SHARE""", cancellationToken);
    }

    /// <summary>Max plans per service: keeps the service list payload and the dropdowns small.</summary>
    internal const int MaxPlansPerProduct = 100;

    /// <summary>Display names end up in emails, PDFs and invoice descriptions: a single printable line only.</summary>
    internal static bool IsValidDisplayName(string trimmed) =>
        trimmed.Length is > 0 and <= 100 && !trimmed.Any(IsDisallowedInDisplayName);

    /// <summary>Control characters, Unicode line/paragraph separators and bidi overrides (which can make a name read backwards).</summary>
    private static bool IsDisallowedInDisplayName(char c) =>
        char.IsControl(c)
        || c is '\u2028' or '\u2029'
        || c is >= '\u202A' and <= '\u202E'
        || c is >= '\u2066' and <= '\u2069';

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

        await LockPlansAsync(db, serviceProductId, exclusive: false, cancellationToken);
        var plans = await db.ProductPlans
            .AsNoTracking()
            .Where(p => p.ServiceProductId == serviceProductId)
            .Select(p => new { p.Code, p.IsActive })
            .ToListAsync(cancellationToken);

        if (plans.Count == 0)
            return trimmed;

        var code = ToLowerAscii(trimmed);
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
