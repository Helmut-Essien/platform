using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class ProductPlanService(
    AppDbContext db,
    IAuditLogService auditLog,
    Email.IEmailOutboxService? outbox = null,
    Email.EmailTemplateService? templates = null,
    Microsoft.Extensions.Options.IOptions<Configuration.BillingSettings>? billingSettings = null) : IProductPlanService
{
    private const int MaxUsageRows = 500;

    private readonly string _defaultCurrency =
        BillingService.DefaultCurrencyOf(billingSettings?.Value ?? new Configuration.BillingSettings());

    /// <summary>Validates a default price and writes it onto the plan (null clears it).</summary>
    private void ApplyDefaultPrice(ProductPlan plan, PlanPriceDto? price)
    {
        if (price is null)
        {
            plan.DefaultAmount = null;
            plan.DefaultTaxAmount = 0;
            plan.DefaultCurrency = null;
            plan.DefaultPeriodMonths = null;
            return;
        }
        if (price.Amount <= 0)
            throw new InvalidOperationException("Default price must be above zero (clear it for no default price).");
        BillingService.EnsureValidMoney(price.Amount, "Default price");
        BillingService.EnsureValidMoney(price.TaxAmount, "Default tax");
        if (price.PeriodMonths is < 1 or > 36)
            throw new InvalidOperationException("Billing period must be 1 to 36 months.");
        plan.DefaultAmount = price.Amount;
        plan.DefaultTaxAmount = price.TaxAmount;
        plan.DefaultCurrency = string.IsNullOrWhiteSpace(price.Currency) ? _defaultCurrency : BillingService.NormalizeCurrency(price.Currency);
        plan.DefaultPeriodMonths = price.PeriodMonths;
    }

    private static object? DefaultPriceAudit(ProductPlan plan) => plan.DefaultAmount is null
        ? null
        : new { amount = plan.DefaultAmount, tax = plan.DefaultTaxAmount, currency = plan.DefaultCurrency, periodMonths = plan.DefaultPeriodMonths };

    public async Task<IReadOnlyList<ProductPlanDto>> ListAsync(string serviceProductId, CancellationToken cancellationToken = default)
    {
        await EnsureProductAsync(serviceProductId, cancellationToken);
        var plans = await db.ProductPlans
            .AsNoTracking()
            .Where(p => p.ServiceProductId == serviceProductId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Code)
            .ToListAsync(cancellationToken);
        return plans.Select(Map).ToList();
    }

    public async Task<ProductPlanDto> CreateAsync(
        string serviceProductId,
        CreateProductPlanRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var product = await EnsureProductAsync(serviceProductId, cancellationToken);

        var code = ProductPlanRules.NormalizeCode(request.Code);
        if (!ProductPlanRules.IsValidCode(code))
            throw new InvalidOperationException(
                "Plan code must be lowercase letters and digits, optionally separated by '-' or '_' (for example 'workshop').");

        var displayName = request.DisplayName.Trim();
        if (displayName.Length == 0)
            throw new InvalidOperationException("Display name is required.");
        if (!ProductPlanRules.IsValidDisplayName(displayName))
            throw new InvalidOperationException("Display name must be one line of text (no line breaks or control characters), up to 100 characters.");

        if (await db.ProductPlans.AnyAsync(p => p.ServiceProductId == serviceProductId && p.Code == code, cancellationToken))
            throw new InvalidOperationException($"{product.Code} already has a plan with code '{code}'.");
        if (await db.ProductPlans.CountAsync(p => p.ServiceProductId == serviceProductId, cancellationToken) >= ProductPlanRules.MaxPlansPerProduct)
            throw new InvalidOperationException($"A service can have at most {ProductPlanRules.MaxPlansPerProduct} plans.");

        var plan = new ProductPlan
        {
            ServiceProductId = serviceProductId,
            Code = code,
            DisplayName = displayName,
            SortOrder = request.SortOrder,
            IsActive = request.IsActive
        };
        ApplyDefaultPrice(plan, request.DefaultPrice);
        db.ProductPlans.Add(plan);
        try
        {
            // One save for the plan and its audit row: a plan can't exist without its audit entry.
            await auditLog.WriteAsync(AuditAction.ProductPlanCreated, performedBy, null, null, null,
                AuditJson.Serialize(new { serviceProductId, productCode = product.Code, code, plan.DisplayName, plan.IsActive, defaultPrice = DefaultPriceAudit(plan) }),
                ipAddress, cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresUniqueViolation.IsUniqueViolation(ex))
        {
            // Two admins adding the same code at once: the unique index wins.
            throw new InvalidOperationException($"{product.Code} already has a plan with code '{code}'.", ex);
        }
        catch (DbUpdateException ex)
        {
            // Not a duplicate code: the product was most likely deleted while the plan was being added (foreign key).
            if (!await db.ServiceProducts.AsNoTracking().AnyAsync(p => p.Id == serviceProductId, cancellationToken))
                throw new NotFoundException("Service product not found.");
            throw new InvalidOperationException("The plan could not be saved. Refresh and try again.", ex);
        }

        return Map(plan);
    }

    public async Task<ProductPlanDto> UpdateAsync(
        string serviceProductId,
        string planId,
        UpdateProductPlanRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var product = await EnsureProductAsync(serviceProductId, cancellationToken);
        var plan = await db.ProductPlans
            .FirstOrDefaultAsync(p => p.Id == planId && p.ServiceProductId == serviceProductId, cancellationToken)
            ?? throw new NotFoundException("Plan not found.");

        var displayName = request.DisplayName.Trim();
        if (displayName.Length == 0)
            throw new InvalidOperationException("Display name is required.");
        if (!ProductPlanRules.IsValidDisplayName(displayName))
            throw new InvalidOperationException("Display name must be one line of text (no line breaks or control characters), up to 100 characters.");

        var wasActive = plan.IsActive;
        // Null = unchanged, so an older client (or the active switch) can't wipe the default price.
        if (request.DefaultPrice is not null)
            ApplyDefaultPrice(plan, request.DefaultPrice);
        else if (request.ClearDefaultPrice)
            ApplyDefaultPrice(plan, null);
        plan.DisplayName = displayName;
        plan.SortOrder = request.SortOrder;
        plan.IsActive = request.IsActive;
        plan.UpdatedAt = DateTime.UtcNow;

        await auditLog.WriteAsync(AuditAction.ProductPlanUpdated, performedBy, null, null, null,
            AuditJson.Serialize(new
            {
                serviceProductId,
                productCode = product.Code,
                code = plan.Code,
                plan.DisplayName,
                plan.IsActive,
                activationChanged = wasActive != plan.IsActive,
                defaultPrice = DefaultPriceAudit(plan)
            }), ipAddress, cancellationToken);
        return Map(plan);
    }

    public Task DeleteAsync(
        string serviceProductId,
        string planId,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            var product = await EnsureProductAsync(serviceProductId, ct);
            // Exclusive plan lock: a license being created onto this plan holds the shared one, so either it
            // commits first (and the in-use check below sees it) or it waits and finds the plan gone.
            await ProductPlanRules.LockPlansAsync(db, serviceProductId, exclusive: true, ct);
            var plan = await db.ProductPlans
                .FirstOrDefaultAsync(p => p.Id == planId && p.ServiceProductId == serviceProductId, ct)
                ?? throw new NotFoundException("Plan not found.");

            var inUse = await db.Licenses.IgnoreQueryFilters()
                .AnyAsync(l => l.ServiceProductId == serviceProductId && l.PlanName.ToLower() == plan.Code, ct);
            if (inUse)
                throw new InvalidOperationException(
                    $"Licenses are on the '{plan.Code}' plan. Deactivate it instead (existing keys keep working), or map them to another plan first.");

            db.ProductPlans.Remove(plan);
            await auditLog.WriteAsync(AuditAction.ProductPlanDeleted, performedBy, null, null, null,
                AuditJson.Serialize(new { serviceProductId, productCode = product.Code, code = plan.Code }),
                ipAddress, ct);
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }, cancellationToken);

    public async Task<IReadOnlyList<ProductPlanUsageDto>> GetUsageAsync(string serviceProductId, CancellationToken cancellationToken = default)
    {
        await EnsureProductAsync(serviceProductId, cancellationToken);

        var rows = await db.Licenses
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.ServiceProductId == serviceProductId)
            .GroupBy(l => l.PlanName)
            .Select(g => new
            {
                PlanName = g.Key,
                Count = g.Count(),
                Remappable = g.Count(l => l.Status != LicenseStatus.Revoked)
            })
            .OrderByDescending(r => r.Count).ThenBy(r => r.PlanName)
            .Take(MaxUsageRows)
            .ToListAsync(cancellationToken);

        var activeCodes = (await db.ProductPlans
                .AsNoTracking()
                .Where(p => p.ServiceProductId == serviceProductId && p.IsActive)
                .Select(p => p.Code)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        return rows
            .OrderByDescending(r => r.Count).ThenBy(r => r.PlanName, StringComparer.Ordinal)
            .Select(r => new ProductPlanUsageDto
            {
                PlanName = r.PlanName,
                LicenseCount = r.Count,
                RemappableCount = r.Remappable,
                IsActivePlanCode = activeCodes.Contains(r.PlanName)
            })
            .ToList();
    }

    public async Task<RemapProductPlanResultDto> RemapAsync(
        string serviceProductId,
        RemapProductPlanRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var product = await EnsureProductAsync(serviceProductId, cancellationToken);

        var toCode = ProductPlanRules.NormalizeCode(request.ToCode);
        var target = await db.ProductPlans.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ServiceProductId == serviceProductId && p.Code == toCode, cancellationToken)
            ?? throw new InvalidOperationException($"{product.Code} has no plan with code '{toCode}'. Add it first.");
        if (!target.IsActive)
            throw new InvalidOperationException($"Plan '{toCode}' is inactive. Activate it before mapping licenses onto it.");

        var from = request.FromPlanName;
        if (string.IsNullOrWhiteSpace(from))
            throw new InvalidOperationException("The old plan name is required.");

        var revokedCount = await db.Licenses.IgnoreQueryFilters()
            .CountAsync(l => l.ServiceProductId == serviceProductId && l.PlanName == from && l.Status == LicenseStatus.Revoked, cancellationToken);

        // Already the code: nothing to do (also guards from == toCode).
        if (string.Equals(from, toCode, StringComparison.Ordinal))
            return new RemapProductPlanResultDto { FromPlanName = from, ToCode = toCode, DryRun = request.DryRun, SkippedRevokedCount = revokedCount };

        var query = db.Licenses.IgnoreQueryFilters()
            .Where(l => l.ServiceProductId == serviceProductId && l.PlanName == from && l.Status != LicenseStatus.Revoked);

        if (request.DryRun)
        {
            return new RemapProductPlanResultDto
            {
                FromPlanName = from, ToCode = toCode, DryRun = true,
                UpdatedCount = await query.CountAsync(cancellationToken),
                SkippedRevokedCount = revokedCount
            };
        }

        // One transaction: the matching rows are locked first (in id order, so two remaps can't deadlock), then
        // loaded, changed and saved together with their audit rows. A concurrent edit of one of these licenses
        // either committed before the lock (and is seen) or waits for the remap and then gets a retryable 409 for
        // its stale version; the remap itself never half-applies.
        var updated = await RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            if (db.Database.IsRelational())
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     SELECT 1 FROM "Licenses"
                     WHERE "ServiceProductId" = {serviceProductId} AND "PlanName" = {from} AND "Status" <> 'Revoked'
                     ORDER BY "Id" FOR UPDATE
                     """, ct);

            var licenses = await query.ToListAsync(ct);
            if (licenses.Count == 0)
                return 0;

            var now = DateTime.UtcNow;
            var audits = new List<AuditEntryRequest>(licenses.Count + 1);
            foreach (var license in licenses)
            {
                license.PlanName = toCode;
                license.UpdatedAt = now;
                audits.Add(new AuditEntryRequest(AuditAction.LicenseUpdated, performedBy, license.CustomerId, license.Id, null,
                    AuditJson.Serialize(new { planName = toCode, previousPlanName = from, source = "plan_remap" }), ipAddress));
            }
            audits.Add(new AuditEntryRequest(AuditAction.LicensePlansRemapped, performedBy, null, null, null,
                AuditJson.Serialize(new
                {
                    serviceProductId,
                    productCode = product.Code,
                    from,
                    to = toCode,
                    updated = licenses.Count,
                    skippedRevoked = revokedCount
                }), ipAddress));

            // A pending price change belongs to the plan it was scheduled for: moving the license cancels it.
            await PriceChanges.LicensePriceChangeOps.CancelPendingAsync(db, auditLog, outbox, templates, licenses,
                PriceChanges.LicensePriceChangeOps.ReasonPlanChanged, performedBy, ipAddress, notifyCustomer: true, ct,
                only: c => !string.Equals(c.PlanCode, toCode, StringComparison.Ordinal));
            await auditLog.WriteBatchAsync(audits, ct); // one SaveChanges: licenses + audit rows
            await db.SaveChangesAsync(ct);              // no-op when the audit writer already saved
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return licenses.Count;
        }, cancellationToken);

        // No validation cache to invalidate: validation reads PlanName from PostgreSQL on every check.
        return new RemapProductPlanResultDto
        {
            FromPlanName = from, ToCode = toCode, UpdatedCount = updated, SkippedRevokedCount = revokedCount
        };
    }

    private async Task<ServiceProduct> EnsureProductAsync(string serviceProductId, CancellationToken cancellationToken) =>
        await db.ServiceProducts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == serviceProductId, cancellationToken)
            ?? throw new NotFoundException("Service product not found.");

    internal static ProductPlanDto Map(ProductPlan plan) => new()
    {
        Id = plan.Id,
        ServiceProductId = plan.ServiceProductId,
        Code = plan.Code,
        DisplayName = plan.DisplayName,
        SortOrder = plan.SortOrder,
        IsActive = plan.IsActive,
        DefaultPrice = plan.DefaultAmount is > 0
            ? new PlanPriceDto
            {
                Amount = plan.DefaultAmount.Value,
                TaxAmount = plan.DefaultTaxAmount,
                Currency = plan.DefaultCurrency,
                PeriodMonths = plan.DefaultPeriodMonths ?? 12
            }
            : null
    };
}
