using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Platform.Api.Services.Email;

namespace Platform.Api.Services;

public partial class LicenseService(
    AppDbContext db,
    IBillingService billing,
    IAuditLogService auditLog,
    ILicenseKeyDeliveryService licenseKeyDelivery,
    ILicenseDenyListService denyList,
    IEmailOutboxService outbox,
    EmailTemplateService templates,
    Microsoft.Extensions.Options.IOptions<Platform.Api.Configuration.BillingSettings>? billingSettings = null,
    Renewals.PayLinkService? payLinks = null) : ILicenseService
{
    private readonly Platform.Api.Configuration.BillingSettings _settings =
        billingSettings?.Value ?? new Platform.Api.Configuration.BillingSettings();

    private string _defaultCurrency => BillingService.DefaultCurrencyOf(_settings);

    internal const string TrialAlreadyUsed =
        "This customer has already had a free trial of that service. Issue a paid or complimentary license instead.";

    private int TrialMonths => Math.Clamp(_settings.TrialMonths, 1, 12);

    private int TrialMaxMonths => Math.Clamp(_settings.TrialMaxTotalMonths, TrialMonths, 24);

    /// <summary>Latest trial end allowed for a trial that started at <paramref name="startedAt"/>.</summary>
    internal static DateTime? TrialCap(DateTime? startedAt, int maxMonths) =>
        startedAt is null
            ? null
            : Renewals.PeriodMath.AddMonths(DateTime.SpecifyKind(startedAt.Value.Date, DateTimeKind.Utc), maxMonths);

    public Task<LicenseDto> CreateAsync(
        CreateLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
        if (request.Trial && request.Complimentary)
            throw new InvalidOperationException("A license can be a free trial or complimentary, not both.");

        var customer = await db.Customers.FindAsync([request.CustomerId], ct)
            ?? throw new NotFoundException("Customer not found.");

        if (customer.IsSuspended)
            throw new InvalidOperationException("Cannot issue license for a suspended customer.");

        _ = await db.ServiceProducts.FindAsync([request.ServiceProductId], ct)
            ?? throw new NotFoundException("Service product not found.");

        var openLicenseExists = await db.Licenses
            .IgnoreQueryFilters()
            .AnyAsync(l => l.CustomerId == request.CustomerId
                && l.ServiceProductId == request.ServiceProductId
                && l.Status != LicenseStatus.Revoked, ct);
        if (openLicenseExists)
            throw new InvalidOperationException(
                "This customer already has a license for that service. Renew, activate, or revoke it before issuing another.");

        if (request.Trial && await TrialUsedAsync(request.CustomerId, request.ServiceProductId, ct))
            throw new InvalidOperationException(TrialAlreadyUsed);

        var now = DateTime.UtcNow;
        var license = new License
        {
            CustomerId = request.CustomerId,
            ServiceProductId = request.ServiceProductId,
            Status = LicenseStatus.Pending,
            ExpiresAt = DateTimeNormalizer.ToUtc(request.ExpiresAt),
            PlanName = request.PlanName,
            CreatedAt = now,
            UpdatedAt = now
        };
        if (request.Complimentary)
        {
            MakeFree(license);
            if (DateTimeNormalizer.IsPastExpiry(license.ExpiresAt, now))
                throw new InvalidOperationException("Expiry must be in the future.");
        }
        else
        {
            ApplyBilling(license, request.Billing, _defaultCurrency);
        }

        if (request.Trial)
        {
            // Trial end is a calendar date (valid through that day), like admin-set expiries.
            license.IsTrial = true;
            license.TrialStartedAt = now;
            license.ExpiresAt = Renewals.PeriodMath.AddMonths(DateTime.SpecifyKind(now.Date, DateTimeKind.Utc), TrialMonths);
        }
        SetAnchor(license);

        db.Licenses.Add(license);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresUniqueViolation.IsUniqueViolation(ex))
        {
            // Two admins issuing at once: the open-license or the one-trial index rejected the second.
            throw new InvalidOperationException(
                request.Trial && PostgresUniqueViolation.ConstraintName(ex) == "IX_Licenses_OneTrialPerCustomerService"
                    ? TrialAlreadyUsed
                    : "This customer already has a license for that service. Renew, activate, or revoke it before issuing another.",
                ex);
        }

        await auditLog.WriteAsync(AuditAction.LicenseIssued, performedBy, request.CustomerId, license.Id,
            null, AuditJson.Serialize(new
            {
                planName = request.PlanName,
                kind = request.Trial ? "trial" : request.Complimentary ? "complimentary" : "paid"
            }), ipAddress, ct);

        if (request.Trial || request.Complimentary)
        {
            // Both start straight away: same activation path as the manual action (key queued via the outbox).
            await LicenseActivation.ActivateAsync(license, licenseKeyDelivery, deliverKey: true, now, ct);
            await db.SaveChangesAsync(ct);
            await auditLog.WriteAsync(
                request.Trial ? AuditAction.LicenseTrialStarted : AuditAction.LicenseMadeComplimentary,
                performedBy, request.CustomerId, license.Id, null,
                AuditJson.Serialize(new { expiresAt = license.ExpiresAt, price = license.BillingAmount }),
                ipAddress, ct);
        }
        else if (request.CreateInvoice)
        {
            // No amounts typed in: bill the license's own price for its first period.
            var fromPrice = request.InvoiceSubtotal == 0 && request.InvoiceTaxAmount == 0 && license.BillingAmount is > 0;
            var subtotal = fromPrice ? license.BillingAmount!.Value : request.InvoiceSubtotal;
            var tax = fromPrice ? license.BillingTaxAmount : request.InvoiceTaxAmount;
            var currency = fromPrice ? license.BillingCurrency : request.InvoiceCurrency;
            var description = license.BillingAmount is > 0
                ? $"License {license.PlanName}: first {PeriodLabel(license.BillingPeriodMonths)}"
                : $"License {license.PlanName}";
            // A priced first invoice activates the Pending license once it is fully paid.
            await billing.CreateInvoiceForLicenseAsync(
                license,
                subtotal,
                tax,
                currency,
                request.InvoiceDueDate,
                description,
                performedBy,
                request.SendInvoice ? InvoiceStatus.Sent : InvoiceStatus.Draft,
                ipAddress,
                ct,
                activatesLicense: subtotal + tax > 0);
        }

        var result = await MapLicenseAsync(license.Id, cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to load created license.");
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return result;
        }, cancellationToken);

    public async Task<LicenseDto?> GetAsync(
        string id,
        bool includeSuspendedCustomers = false,
        CancellationToken cancellationToken = default)
    {
        return await MapLicenseAsync(id, includeSuspendedCustomers, cancellationToken);
    }

    public async Task<PagedResult<LicenseDto>> ListAsync(
        string? customerId = null,
        bool includeSuspendedCustomers = false,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        int? expiringWithinDays = null,
        string? serviceProductId = null,
        LicenseStatus? status = null,
        string? search = null,
        string? sortBy = null,
        bool sortDesc = false,
        CancellationToken cancellationToken = default,
        LicenseKind? kind = null)
    {
        var (normalizedPage, normalizedPageSize, skip) = PagingHelper.Normalize(page, pageSize);

        var query = (includeSuspendedCustomers
                ? db.Licenses.IgnoreQueryFilters()
                : db.Licenses)
            .AsNoTracking()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .AsQueryable();

        if (customerId is not null)
            query = query.Where(l => l.CustomerId == customerId);

        if (expiringWithinDays is > 0)
        {
            var threshold = DateTime.UtcNow.AddDays(expiringWithinDays.Value);
            query = query.Where(l =>
                l.Status == LicenseStatus.Active
                && l.ExpiresAt.HasValue
                && l.ExpiresAt.Value <= threshold);
        }

        if (!string.IsNullOrEmpty(serviceProductId))
            query = query.Where(l => l.ServiceProductId == serviceProductId);

        if (status is not null)
            query = query.Where(l => l.Status == status);

        query = kind switch
        {
            LicenseKind.Trial => query.Where(l => l.IsTrial),
            LicenseKind.Complimentary => query.Where(l => l.IsComplimentary),
            LicenseKind.Paid => query.Where(l => !l.IsTrial && !l.IsComplimentary),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = SearchHelper.Normalize(search).ToLower();
            query = query.Where(l =>
                l.Customer.Name.ToLower().Contains(term)
                || l.PlanName.ToLower().Contains(term)
                || (l.ServiceProduct != null && l.ServiceProduct.Code.ToLower().Contains(term)));
        }

        // Id tiebreaker keeps paging stable for licenses created in the same instant.
        var ordered = SortHelper.Apply(query, SortHelper.LicenseSorts, sortBy, sortDesc,
                q => q.OrderByDescending(l => l.CreatedAt))
            .ThenByDescending(l => l.Id);

        var totalCount = await ordered.CountAsync(cancellationToken);

        var licenses = await ordered
            .Skip(skip)
            .Take(normalizedPageSize)
            .ToListAsync(cancellationToken);

        var ids = licenses.Select(l => l.Id).ToList();
        var invoiceQuery = includeSuspendedCustomers
            ? db.Invoices.IgnoreQueryFilters()
            : db.Invoices;
        var invoiceRows = ids.Count == 0
            ? []
            : await invoiceQuery
                .AsNoTracking()
                .Where(i => i.LicenseId != null && ids.Contains(i.LicenseId))
                .Select(i => new InvoiceRow(i.LicenseId!, i.Id, i.CreatedAt, i.IsActivationInvoice, i.Status, i.InvoiceNumber))
                .ToListAsync(cancellationToken);

        var invoiceLookup = invoiceRows
            .GroupBy(i => i.LicenseId)
            .ToDictionary(g => g.Key, Summarize);

        return new PagedResult<LicenseDto>
        {
            Items = licenses.Select(l => MapLicense(l, invoiceLookup.GetValueOrDefault(l.Id), TrialMaxMonths)).ToList(),
            TotalCount = totalCount,
            Page = normalizedPage,
            PageSize = normalizedPageSize
        };
    }

    public Task<LicenseDto> ActivateAsync(
        string id,
        ActivateLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
        // Serialises with automatic activation on payment (BillingService locks the same rows). Lock order is
        // invoice row, then license row (as in RecordReceipt/ReverseReceipt), so the two can't deadlock.
        var activationInvoiceId = await db.Invoices
            .IgnoreQueryFilters()
            .Where(i => i.LicenseId == id && i.IsActivationInvoice && i.Status != InvoiceStatus.Void)
            .Select(i => i.Id)
            .FirstOrDefaultAsync(ct);
        if (activationInvoiceId is not null && db.Database.IsRelational())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "Invoices" WHERE "Id" = {activationInvoiceId} FOR UPDATE""", ct);
        await LockLicenseRowAsync(id, ct);
        var license = await db.Licenses
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new NotFoundException("License not found.");

        if (license.Customer.IsSuspended)
            throw new InvalidOperationException("Customer is suspended.");

        if (license.Status is not (LicenseStatus.Pending or LicenseStatus.Suspended or LicenseStatus.Expired))
            throw new InvalidOperationException($"Cannot activate license in status {license.Status}.");

        var now = DateTime.UtcNow;
        if (DateTimeNormalizer.IsPastExpiry(license.ExpiresAt, now))
            throw new InvalidOperationException("License expiry is in the past. Renew the license before activating.");

        // B4: a license that already has a key (Suspended/Expired) is being restored, not issued.
        // The request flags default to "email key + create invoice", so they are ignored here;
        // use Rotate Key / Renew for those actions explicitly.
        var restoring = license.Status != LicenseStatus.Pending && !string.IsNullOrEmpty(license.LicenseKeyHash);
        var emailKey = request.EmailLicenseKey && !restoring;
        var createInvoice = request.CreateInvoice && !restoring && !license.IsComplimentary;

        // A live activation invoice already bills this license: activating early (or after a payment that could
        // not auto-activate, e.g. the customer was suspended then) must not bill it twice.
        var activationInvoice = license.Status == LicenseStatus.Pending
            ? await db.Invoices
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.LicenseId == id && i.IsActivationInvoice && i.Status != InvoiceStatus.Void, ct)
            : null;
        var openActivationInvoice = createInvoice && activationInvoice is not null;
        if (openActivationInvoice)
            createInvoice = false;

        // Already paid but never applied: give it the term the payment bought, and tie it to the invoice so a
        // later reversal suspends it like an auto-activated one.
        if (activationInvoice is { Status: InvoiceStatus.Paid, ActivationAppliedAt: null })
        {
            var paidAt = await db.Receipts
                .Where(r => r.InvoiceId == activationInvoice.Id && r.Status == ReceiptStatus.Posted)
                .MaxAsync(r => (DateTime?)r.PaidAt, ct) ?? now;
            var (newExpiry, anchorDay) = LicenseActivation.ExpiryAfterPayment(license.ExpiresAt, activationInvoice.ActivationPeriodMonths, paidAt, now);
            if (newExpiry != license.ExpiresAt)
            {
                license.ExpiresAt = newExpiry;
                license.BillingAnchorDay = anchorDay ?? newExpiry?.Day;
            }
            activationInvoice.ActivationAppliedAt = now;
        }

        await LicenseActivation.ActivateAsync(license, licenseKeyDelivery, emailKey, now, ct);

        await db.SaveChangesAsync(ct);

        await auditLog.WriteAsync(AuditAction.LicenseActivated, performedBy, license.CustomerId, license.Id,
            null, AuditJson.Serialize(new
            {
                licenseKeyQueued = emailKey,
                restored = restoring,
                invoiceSkippedOpenActivationInvoice = openActivationInvoice
            }), ipAddress, ct);

        if (createInvoice)
        {
            await billing.CreateInvoiceForLicenseAsync(
                license, request.Subtotal, request.TaxAmount, request.Currency,
                request.DueDate, request.Description, performedBy,
                request.SendInvoice ? InvoiceStatus.Sent : InvoiceStatus.Draft,
                ipAddress, ct);
        }

        var result = await MapLicenseAsync(license.Id, cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to load license.");
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        await denyList.ClearLicenseDenyAsync(license.Id, ct);
        return result;
        }, cancellationToken);

    public Task<LicenseDto> ReactivateAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            var license = await db.Licenses
                .Include(l => l.Customer)
                .Include(l => l.ServiceProduct)
                .FirstOrDefaultAsync(l => l.Id == id, ct)
                ?? throw new NotFoundException("License not found.");

            if (license.Customer.IsSuspended)
                throw new InvalidOperationException("Customer is suspended.");

            if (license.Status != LicenseStatus.Suspended)
                throw new InvalidOperationException($"Only suspended licenses can be reactivated (current status {license.Status}).");

            var now = DateTime.UtcNow;
            if (DateTimeNormalizer.IsPastExpiry(license.ExpiresAt, now))
                throw new InvalidOperationException("License expiry is in the past. Renew the license first.");

            license.Status = LicenseStatus.Active;
            license.AutoSuspendedForOverdueInvoiceId = null;
            license.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            await auditLog.WriteAsync(AuditAction.LicenseReactivated, performedBy, license.CustomerId, license.Id,
                null, null, ipAddress, ct);

            var result = await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, ct)
                ?? throw new InvalidOperationException("Failed to load license.");
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            await denyList.ClearLicenseDenyAsync(license.Id, ct);
            return result;
        }, cancellationToken);

    public Task<LicenseDto> RenewAsync(
        string id,
        RenewLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
        // Same lock order as billing (renewal lock, invoice rows, license): a manual renewal of a trial voids
        // its unpaid trial invoice, which would otherwise stay payable for a period that no longer applies.
        var trialInvoiceIds = await LockOpenInvoicesAsync(id, trialOnly: true, ct);
        await LockLicenseRowAsync(id, ct);
        var license = await db.Licenses
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new NotFoundException("License not found.");

        if (license.Customer.IsSuspended)
            throw new InvalidOperationException("Customer is suspended.");

        if (license.Status is not (LicenseStatus.Active or LicenseStatus.Expired or LicenseStatus.Suspended))
            throw new InvalidOperationException($"Cannot renew license in status {license.Status}.");

        var now = DateTime.UtcNow;
        var renewedExpiry = DateTimeNormalizer.ToUtc(request.ExpiresAt);
        if (!renewedExpiry.HasValue || DateTimeNormalizer.IsPastExpiry(renewedExpiry, now))
            throw new InvalidOperationException("Renewal expiry must be in the future.");

        // B8: renewing extends the term; it must not silently lift an admin/overdue suspension.
        // Suspended licenses stay Suspended until explicitly reactivated.
        var stillSuspended = license.Status == LicenseStatus.Suspended;
        if (!stillSuspended)
        {
            license.Status = LicenseStatus.Active;
            license.AutoSuspendedForOverdueInvoiceId = null;
        }
        license.ExpiresAt = renewedExpiry;
        license.UpdatedAt = now;
        SetAnchor(license);
        if (license.IsComplimentary)
        {
            if (request.Billing?.Amount is > 0 || request.CreateInvoice)
                throw new InvalidOperationException("This license is complimentary (free). Use \"Switch to paid\" to start billing it.");
        }
        else
        {
            ApplyBilling(license, request.Billing, _defaultCurrency);
        }
        var trialConverted = license.IsTrial;
        license.IsTrial = false; // a manual renewal ends the trial: the admin has set a paid term
        var (trialInvoicesVoided, trialInvoicesKeptOpen) = trialConverted
            ? await VoidUnpaidInvoicesAsync(trialInvoiceIds, "trial converted by manual renewal", now, performedBy, ipAddress, ct)
            : ([], []);

        if (request.RotateLicenseKey)
        {
            if (!request.EmailLicenseKey)
                throw new InvalidOperationException("Rotated keys must be emailed to the technical contact.");
            await licenseKeyDelivery.DeliverNewKeyAsync(license, isRenewal: true, ct);
        }
        else if (request.EmailLicenseKey && !stillSuspended)
        {
            var renewal = templates.Renewal(license.Customer, license.ServiceProduct, license);
            outbox.Enqueue(
                EmailDeliveryKind.RenewalConfirmation,
                CustomerContactResolver.Technical(license.Customer),
                renewal.Subject,
                renewal.Html,
                license.CustomerId,
                license.Id);
        }
        await db.SaveChangesAsync(ct);

        await auditLog.WriteAsync(AuditAction.LicenseRenewed, performedBy, license.CustomerId, license.Id,
            null, AuditJson.Serialize(new { keyRotated = request.RotateLicenseKey }), ipAddress, ct);
        if (trialConverted)
            await auditLog.WriteAsync(AuditAction.LicenseTrialConverted, performedBy, license.CustomerId, license.Id,
                null, AuditJson.Serialize(new
                {
                    source = "manual-renewal",
                    expiresAt = license.ExpiresAt,
                    invoicesVoided = trialInvoicesVoided,
                    invoicesLeftOpen = trialInvoicesKeptOpen
                }), ipAddress, ct);

        if (request.RotateLicenseKey)
        {
            await auditLog.WriteAsync(AuditAction.LicenseKeyRotated, performedBy, license.CustomerId, license.Id,
                null, null, ipAddress, ct);
        }

        if (request.CreateInvoice)
        {
            await billing.CreateInvoiceForLicenseAsync(
                license, request.Subtotal, request.TaxAmount, request.Currency,
                request.DueDate, request.Description, performedBy,
                request.SendInvoice ? InvoiceStatus.Sent : InvoiceStatus.Draft,
                ipAddress, ct);
        }

        var result = await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, ct)
            ?? throw new InvalidOperationException("Failed to load license.");
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        if (!stillSuspended)
            await denyList.ClearLicenseDenyAsync(license.Id, ct);
        return result;
        }, cancellationToken);

    public async Task<LicenseDto> UpdateAsync(
        string id,
        UpdateLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var license = await db.Licenses
            .IgnoreQueryFilters()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new NotFoundException("License not found.");

        if (license.Status is LicenseStatus.Revoked)
            throw new InvalidOperationException("Cannot update a revoked license.");

        license.PlanName = request.PlanName.Trim();
        var newExpiry = DateTimeNormalizer.ToUtc(request.ExpiresAt);
        if (license.IsTrial && newExpiry != license.ExpiresAt)
        {
            var cap = TrialCap(license.TrialStartedAt, TrialMaxMonths);
            if (newExpiry is null || (cap is not null && newExpiry > cap))
                throw new InvalidOperationException(
                    $"A trial must end by {cap:dd MMM yyyy} (Billing:TrialMaxTotalMonths). Renew it to give a paid term instead.");
            // The trial invoice is tied to the trial end; Extend trial moves it under the renewal lock.
            if (await db.Invoices.IgnoreQueryFilters().AnyAsync(i => i.LicenseId == id && i.IsTrialConversion
                    && OpenInvoiceStatuses.Contains(i.Status), cancellationToken))
                throw new InvalidOperationException(
                    "This trial already has its first invoice. Use \"Extend trial\" to change the end date so the invoice moves with it.");
        }
        if (license.IsComplimentary && request.Billing?.Amount is > 0)
            throw new InvalidOperationException("This license is complimentary (free). Use \"Switch to paid\" to set a price.");
        if (newExpiry != license.ExpiresAt)
        {
            license.ExpiresAt = newExpiry;
            SetAnchor(license);
        }
        ApplyBilling(license, request.Billing, _defaultCurrency);
        license.UpdatedAt = DateTime.UtcNow;
        var becameExpired = license.Status == LicenseStatus.Active
            && DateTimeNormalizer.IsPastExpiry(license.ExpiresAt, license.UpdatedAt);
        if (becameExpired)
            license.Status = LicenseStatus.Expired;

        if (!string.IsNullOrEmpty(license.LicenseKeyLookupHash))
        {
            await denyList.InvalidateValidationCacheAsync(
                license.ServiceProductId,
                license.LicenseKeyLookupHash,
                cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        if (becameExpired)
            await denyList.DenyLicenseAsync(license.Id, cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseUpdated, performedBy, license.CustomerId, license.Id, null,
            AuditJson.Serialize(new { planName = license.PlanName, expiresAt = license.ExpiresAt, status = license.Status.ToString() }),
            ipAddress, cancellationToken);

        return await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
    }

    public async Task<LicenseDto> SuspendAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default,
        string? notificationReason = null,
        string? autoSuspendedForOverdueInvoiceId = null)
    {
        var license = await db.Licenses
            .IgnoreQueryFilters()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new NotFoundException("License not found.");

        if (license.Status is not LicenseStatus.Active)
            throw new InvalidOperationException($"Cannot suspend license in status {license.Status}.");

        license.Status = LicenseStatus.Suspended;
        license.UpdatedAt = DateTime.UtcNow;
        license.AutoSuspendedForOverdueInvoiceId = string.IsNullOrWhiteSpace(autoSuspendedForOverdueInvoiceId)
            ? null
            : autoSuspendedForOverdueInvoiceId;
        // Suspended for an unpaid invoice: include its pay link so paying restores access by itself.
        Invoice? holdInvoice = null;
        string? holdPayUrl = null;
        if (license.AutoSuspendedForOverdueInvoiceId is { } holdId && payLinks is not null)
        {
            // Tracked so an expired or missing link is renewed (saved below): the email must carry a working link.
            holdInvoice = await db.Invoices.IgnoreQueryFilters()
                .Include(i => i.Receipts)
                .FirstOrDefaultAsync(i => i.Id == holdId, cancellationToken);
            holdPayUrl = holdInvoice is null ? null : payLinks.EnsureUrl(holdInvoice, DateTime.UtcNow);
        }
        var suspended = templates.StatusNotice(
            license.Customer, license.ServiceProduct, EmailDeliveryKind.Suspended, notificationReason,
            holdPayUrl, holdPayUrl is null ? null : holdInvoice);
        outbox.Enqueue(
            EmailDeliveryKind.Suspended,
            CustomerContactResolver.Technical(license.Customer),
            suspended.Subject,
            suspended.Html,
            license.CustomerId,
            license.Id);
        await db.SaveChangesAsync(cancellationToken);

        // The deny list (Redis) must follow the committed state: inside a caller's transaction (the overdue worker)
        // the caller denies after its commit, so a rollback can't leave a customer locked out but not suspended.
        if (db.Database.CurrentTransaction is null)
            await denyList.DenyLicenseAsync(license.Id, cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseSuspended, performedBy, license.CustomerId, license.Id,
            null, null, ipAddress, cancellationToken);

        return await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
    }

    public Task<LicenseDto> RevokeAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            // Billing lock order: renewal lock, open invoice rows (id order), then the license row.
            var invoiceIds = await LockOpenInvoicesAsync(id, trialOnly: false, ct);
            await LockLicenseRowAsync(id, ct);
            var license = await db.Licenses
                .IgnoreQueryFilters()
                .Include(l => l.Customer)
                .Include(l => l.ServiceProduct)
                .FirstOrDefaultAsync(l => l.Id == id, ct)
                ?? throw new NotFoundException("License not found.");

            if (license.Status == LicenseStatus.Revoked)
                throw new InvalidOperationException("License is already revoked.");

            var now = DateTime.UtcNow;
            license.Status = LicenseStatus.Revoked;
            license.UpdatedAt = now;
            // Unpaid open invoices die with the license; ones with money on them or a payment in progress stay
            // open for an admin to settle (a payment arriving later is recorded but changes nothing on the license).
            var (voided, keptOpen) = await VoidUnpaidInvoicesAsync(invoiceIds, "license revoked", now, performedBy,
                ipAddress, ct);
            var revoked = templates.StatusNotice(
                license.Customer, license.ServiceProduct, EmailDeliveryKind.Revoked);
            outbox.Enqueue(
                EmailDeliveryKind.Revoked,
                CustomerContactResolver.Technical(license.Customer),
                revoked.Subject,
                revoked.Html,
                license.CustomerId,
                license.Id);
            await db.SaveChangesAsync(ct);

            await auditLog.WriteAsync(AuditAction.LicenseRevoked, performedBy, license.CustomerId, license.Id,
                null, voided.Count + keptOpen.Count == 0
                    ? null
                    : AuditJson.Serialize(new { invoicesVoided = voided, invoicesLeftOpen = keptOpen }),
                ipAddress, ct);

            var result = await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, ct)
                ?? throw new InvalidOperationException("Failed to load license.");
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            await denyList.DenyLicenseAsync(license.Id, ct);
            return result;
        }, cancellationToken);

    public async Task<LicenseDto> ResendKeyAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
        => await RotateKeyAsync(id, performedBy, ipAddress, cancellationToken);

    public async Task<LicenseDto> RotateKeyAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var license = await db.Licenses
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new NotFoundException("License not found.");

        if (license.Customer.IsSuspended)
            throw new InvalidOperationException("Customer is suspended.");

        if (license.Status is LicenseStatus.Revoked)
            throw new InvalidOperationException("Cannot rotate the key for a revoked license.");

        await licenseKeyDelivery.DeliverNewKeyAsync(license, isRenewal: true, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseKeyRotated, performedBy, license.CustomerId, license.Id,
            null, AuditJson.Serialize(new { source = "manual-rotation" }), ipAddress, cancellationToken);

        return await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
    }

    private const int ExpiryBatchSize = 200;
    private const int DenyListBatchSize = 50;

    public async Task<int> ExpireDueLicensesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var horizon = now.Date.AddDays(1);

        // P7: one query for everything that is due (instead of an id query plus a reload per license).
        var due = await db.Licenses
            .IgnoreQueryFilters()
            .Where(l => l.Status == LicenseStatus.Active
                && l.ExpiresAt != null
                && l.ExpiresAt < horizon)
            .ToListAsync(cancellationToken);

        var dueIds = due
            .Where(l => DateTimeNormalizer.IsPastExpiry(l.ExpiresAt, now))
            .Select(l => l.Id)
            .ToList();

        var expired = 0;
        var first = true;
        foreach (var chunk in dueIds.Chunk(ExpiryBatchSize))
        {
            if (!first)
            {
                // Later chunks are reloaded fresh (the tracker is cleared after each chunk).
                due = await db.Licenses
                    .IgnoreQueryFilters()
                    .Where(l => chunk.Contains(l.Id))
                    .ToListAsync(cancellationToken);
            }

            first = false;
            var batch = due.Where(l => chunk.Contains(l.Id)).ToList();
            expired += await ExpireBatchAsync(batch, chunk, now, cancellationToken);
            db.ChangeTracker.Clear();
        }

        return expired;
    }

    /// <summary>
    /// Expires a batch with one save (statuses and audit entries together), then updates the deny list in
    /// bounded concurrent groups. If another writer changed one of the licenses in the meantime (the
    /// <c>Version</c> concurrency token), falls back to the original one-at-a-time re-check for that batch.
    /// </summary>
    private async Task<int> ExpireBatchAsync(
        List<License> batch,
        string[] ids,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var expiring = batch
            .Where(l => l.Status == LicenseStatus.Active && DateTimeNormalizer.IsPastExpiry(l.ExpiresAt, now))
            .ToList();
        if (expiring.Count == 0)
            return 0;

        try
        {
            var audits = new List<AuditEntryRequest>(expiring.Count);
            foreach (var license in expiring)
            {
                license.Status = LicenseStatus.Expired;
                license.UpdatedAt = now;
                audits.Add(new AuditEntryRequest(
                    AuditAction.LicenseExpired,
                    "system:license-expiry",
                    license.CustomerId,
                    license.Id,
                    null,
                    AuditJson.Serialize(new { expiresAt = license.ExpiresAt })));
            }

            await auditLog.WriteBatchAsync(audits, cancellationToken);
            await db.SaveChangesAsync(cancellationToken); // no-op when the audit service already saved
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var count = 0;
            foreach (var id in ids)
                count += await ExpireSingleAsync(id, now, cancellationToken);
            return count;
        }

        await DenyAllAsync(expiring.Select(l => l.Id), cancellationToken);
        return expiring.Count;
    }

    private async Task<int> ExpireSingleAsync(string id, DateTime now, CancellationToken cancellationToken)
    {
        var license = await db.Licenses
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (license is null
            || license.Status != LicenseStatus.Active
            || !DateTimeNormalizer.IsPastExpiry(license.ExpiresAt, now))
        {
            return 0;
        }

        license.Status = LicenseStatus.Expired;
        license.UpdatedAt = now;
        await auditLog.WriteBatchAsync(
        [
            new AuditEntryRequest(
                AuditAction.LicenseExpired,
                "system:license-expiry",
                license.CustomerId,
                license.Id,
                null,
                AuditJson.Serialize(new { expiresAt = license.ExpiresAt }))
        ], cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await denyList.DenyLicenseAsync(license.Id, cancellationToken);
        db.ChangeTracker.Clear();
        return 1;
    }

    private async Task DenyAllAsync(IEnumerable<string> licenseIds, CancellationToken cancellationToken)
    {
        foreach (var group in licenseIds.Chunk(DenyListBatchSize))
            await Task.WhenAll(group.Select(id => denyList.DenyLicenseAsync(id, cancellationToken)));
    }

    private async Task<LicenseDto?> MapLicenseAsync(
        string id,
        bool includeSuspendedCustomers = false,
        CancellationToken cancellationToken = default)
    {
        var query = includeSuspendedCustomers ? db.Licenses.IgnoreQueryFilters() : db.Licenses;

        var license = await query
            .AsNoTracking()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

        if (license is null)
            return null;

        var invoiceQuery = includeSuspendedCustomers
            ? db.Invoices.IgnoreQueryFilters()
            : db.Invoices;
        // One indexed query (Invoices.LicenseId) for both the latest invoice and the live activation invoice.
        var rows = await invoiceQuery
            .AsNoTracking()
            .Where(i => i.LicenseId == id)
            .Select(i => new InvoiceRow(i.LicenseId!, i.Id, i.CreatedAt, i.IsActivationInvoice, i.Status, i.InvoiceNumber))
            .ToListAsync(cancellationToken);

        return MapLicense(license, Summarize(rows), TrialMaxMonths);
    }

    private sealed record InvoiceRow(string LicenseId, string Id, DateTime CreatedAt, bool IsActivation, InvoiceStatus Status, string Number);

    private sealed record InvoiceSummary(string? LatestId, InvoiceRow? Activation);

    private static InvoiceSummary Summarize(IEnumerable<InvoiceRow> rows)
    {
        string? latestId = null;
        var latestAt = DateTime.MinValue;
        InvoiceRow? activation = null;
        foreach (var r in rows)
        {
            if (latestId is null || r.CreatedAt > latestAt)
            {
                latestId = r.Id;
                latestAt = r.CreatedAt;
            }
            if (r.IsActivation && r.Status != InvoiceStatus.Void)
                activation = r;
        }
        return new InvoiceSummary(latestId, activation);
    }

    private static LicenseDto MapLicense(License license, InvoiceSummary? invoices = null, int trialMaxMonths = 3) => new()
    {
        IsTrial = license.IsTrial,
        TrialStartedAt = license.TrialStartedAt,
        TrialMaxEndsAt = license.IsTrial ? TrialCap(license.TrialStartedAt, trialMaxMonths) : null,
        IsComplimentary = license.IsComplimentary,
        Id = license.Id,
        CustomerId = license.CustomerId,
        CustomerName = license.Customer?.Name,
        ServiceProductId = license.ServiceProductId,
        ServiceProductCode = license.ServiceProduct?.Code,
        Status = license.Status,
        ExpiresAt = license.ExpiresAt,
        PlanName = license.PlanName,
        LicenseKeySentAt = license.LicenseKeySentAt,
        CreatedAt = license.CreatedAt,
        UpdatedAt = license.UpdatedAt,
        LatestInvoiceId = invoices?.LatestId,
        ActivationInvoiceId = invoices?.Activation?.Id,
        ActivationInvoiceNumber = invoices?.Activation?.Number,
        ActivationInvoiceStatus = invoices?.Activation?.Status,
        Billing = new LicenseBillingDto
        {
            Amount = license.BillingAmount,
            TaxAmount = license.BillingTaxAmount,
            Currency = license.BillingCurrency,
            PeriodMonths = license.BillingPeriodMonths,
            AutoRenewInvoice = license.AutoRenewInvoice
        }
    };

    /// <summary>Validates and applies a billing price (null = unchanged). Amount null/0 clears the price.</summary>
    internal static void ApplyBilling(License license, LicenseBillingDto? billing,
        string defaultCurrency = Platform.Api.Configuration.BillingSettings.FallbackCurrency)
    {
        if (billing is null)
            return;
        if (billing.PeriodMonths is < 1 or > 36)
            throw new InvalidOperationException("Billing period must be 1 to 36 months.");
        license.BillingPeriodMonths = billing.PeriodMonths;
        license.AutoRenewInvoice = billing.AutoRenewInvoice;
        if (billing.Amount is not > 0)
        {
            license.BillingAmount = null;
            license.BillingTaxAmount = 0;
            license.BillingCurrency = null;
            return;
        }

        BillingService.EnsureValidMoney(billing.Amount.Value, "Billing price");
        BillingService.EnsureValidMoney(billing.TaxAmount, "Billing tax");
        license.BillingAmount = billing.Amount;
        license.BillingTaxAmount = billing.TaxAmount;
        license.BillingCurrency = string.IsNullOrWhiteSpace(billing.Currency)
            ? defaultCurrency
            : BillingService.NormalizeCurrency(billing.Currency);
    }

    /// <summary>Complimentary: no price, never invoiced automatically.</summary>
    private static void MakeFree(License license)
    {
        license.IsComplimentary = true;
        license.IsTrial = false; // TrialStartedAt stays: the one-trial rule still applies
        license.BillingAmount = null;
        license.BillingTaxAmount = 0;
        license.BillingCurrency = null;
        license.AutoRenewInvoice = false;
    }

    private Task<bool> TrialUsedAsync(string customerId, string serviceProductId, CancellationToken ct) =>
        db.Licenses
            .IgnoreQueryFilters()
            .AnyAsync(l => l.CustomerId == customerId
                && l.ServiceProductId == serviceProductId
                && l.TrialStartedAt != null, ct);

    private static string PeriodLabel(int months) => months switch
    {
        1 => "month",
        12 => "year",
        _ when months % 12 == 0 => $"{months / 12} years",
        _ => $"{months} months"
    };

    private async Task LockLicenseRowAsync(string licenseId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
            return;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Licenses" WHERE "Id" = {licenseId} FOR UPDATE""", cancellationToken);
    }

    /// <summary>
    /// An admin-set expiry defines the renewal anniversary day (so Jan 31 renewals return to the 31st). A month-end
    /// expiry that is just the clamped form of the current anchor (28 Feb for anchor 31) keeps that anchor, so it
    /// isn't permanently lowered to 28.
    /// </summary>
    internal static void SetAnchor(License license)
    {
        if (license.ExpiresAt is not { } expiry)
        {
            license.BillingAnchorDay = null;
            return;
        }
        var clampedAnchor = license.BillingAnchorDay is { } anchor
            && anchor > expiry.Day
            && expiry.Day == DateTime.DaysInMonth(expiry.Year, expiry.Month);
        if (!clampedAnchor)
            license.BillingAnchorDay = expiry.Day;
    }
}
