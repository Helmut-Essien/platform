using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Dtos.Customers;
using Platform.Shared.Enums;
using Platform.Api.Services.Email;

namespace Platform.Api.Services;

public class CustomerService(
    AppDbContext db,
    IAuditLogService auditLog,
    ILicenseDenyListService denyList,
    IEmailOutboxService outbox,
    EmailTemplateService templates) : ICustomerService
{
    public async Task<PagedResult<CustomerDto>> ListAsync(
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        string? search = null,
        bool? isSuspended = null,
        DateTime? createdAfter = null,
        string? sortBy = null,
        bool sortDesc = false,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedPageSize, skip) = PagingHelper.Normalize(page, pageSize);

        var query = db.Customers.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = SearchHelper.ContainsPattern(search); // E9: wildcards in user input match literally
            query = query.Where(c =>
                EF.Functions.ILike(c.Name, term) ||
                EF.Functions.ILike(c.ContactEmail, term) ||
                EF.Functions.ILike(c.Id, term));
        }

        if (isSuspended is not null)
            query = query.Where(c => c.IsSuspended == isSuspended.Value);

        if (createdAfter is not null)
            query = query.Where(c => c.CreatedAt >= createdAfter.Value);

        // Whitelisted sort (unknown keys fall back to newest first); Id tiebreaker keeps paging stable.
        var ordered = SortHelper.Apply(query, SortHelper.CustomerSorts, sortBy, sortDesc,
                q => q.OrderByDescending(c => c.CreatedAt))
            .ThenByDescending(c => c.Id);

        var totalCount = await ordered.CountAsync(cancellationToken);

        var customers = await ordered
            .Skip(skip)
            .Take(normalizedPageSize)
            .ToListAsync(cancellationToken);

        var customerIds = customers.Select(c => c.Id).ToList();
        var licenseCounts = customerIds.Count == 0
            ? new Dictionary<string, int>()
            : await db.Licenses
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(l => customerIds.Contains(l.CustomerId))
                .GroupBy(l => l.CustomerId)
                .Select(g => new { CustomerId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.CustomerId, x => x.Count, cancellationToken);

        return new PagedResult<CustomerDto>
        {
            Items = customers.Select(c => MapCustomer(c, licenseCounts.GetValueOrDefault(c.Id))).ToList(),
            TotalCount = totalCount,
            Page = normalizedPage,
            PageSize = normalizedPageSize
        };
    }

    public async Task<CustomerDto?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (customer is null)
            return null;

        var licenseCount = await db.Licenses.IgnoreQueryFilters().CountAsync(l => l.CustomerId == id, cancellationToken);
        return MapCustomer(customer, licenseCount);
    }

    public async Task<CustomerDto> CreateAsync(
        CreateCustomerRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = request.ContactEmail.Trim().ToLowerInvariant();

        if (await db.Customers.AnyAsync(c => c.ContactEmail == normalizedEmail, cancellationToken))
            throw new InvalidOperationException("A customer with this email already exists.");

        var now = DateTime.UtcNow;
        var customer = new Customer
        {
            Name = request.Name.Trim(),
            ContactEmail = normalizedEmail,
            BillingEmail = NormalizeEmail(request.BillingEmail),
            TechnicalEmail = NormalizeEmail(request.TechnicalEmail),
            ContactPhone = request.ContactPhone?.Trim(),
            InternalNotes = request.InternalNotes?.Trim(),
            IsSuspended = false,
            CreatedAt = now
        };

        db.Customers.Add(customer);
        if (request.SendWelcomeEmail)
        {
            var template = templates.Welcome(customer);
            outbox.Enqueue(EmailDeliveryKind.Welcome, customer.ContactEmail, template.Subject, template.Html, customer.Id);
        }
        try
        {
            // P11: WriteAsync persists the pending changes together with the audit row in a single
            // SaveChanges, so the change and its audit entry are atomic and cost one round trip.
            await auditLog.WriteAsync(AuditAction.CustomerCreated, performedBy, customer.Id, null, null,
                AuditJson.Serialize(new { name = customer.Name, email = customer.ContactEmail }), ipAddress, cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresUniqueViolation.IsUniqueViolation(ex))
        {
            throw new InvalidOperationException("A customer with this email already exists.", ex);
        }

        return MapCustomer(customer, 0);
    }

    public async Task<CustomerDto> UpdateAsync(
        string id,
        UpdateCustomerRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var customer = await db.Customers.FindAsync([id], cancellationToken)
            ?? throw new NotFoundException("Customer not found.");

        var normalizedEmail = request.ContactEmail.Trim().ToLowerInvariant();

        if (await db.Customers.AnyAsync(c => c.Id != id && c.ContactEmail == normalizedEmail, cancellationToken))
            throw new InvalidOperationException("A customer with this email already exists.");

        customer.Name = request.Name.Trim();
        customer.ContactEmail = normalizedEmail;
        customer.BillingEmail = NormalizeEmail(request.BillingEmail);
        customer.TechnicalEmail = NormalizeEmail(request.TechnicalEmail);
        customer.ContactPhone = request.ContactPhone?.Trim();
        customer.InternalNotes = request.InternalNotes?.Trim();

        try
        {
            // P11: single SaveChanges for the change and its audit entry.
            await auditLog.WriteAsync(AuditAction.CustomerUpdated, performedBy, customer.Id, null, null,
                AuditJson.Serialize(new { name = customer.Name }), ipAddress, cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresUniqueViolation.IsUniqueViolation(ex))
        {
            throw new InvalidOperationException("A customer with this email already exists.", ex);
        }

        var licenseCount = await db.Licenses.IgnoreQueryFilters().CountAsync(l => l.CustomerId == id, cancellationToken);
        return MapCustomer(customer, licenseCount);
    }

    public async Task<CustomerDto> SuspendAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var customer = await db.Customers.FindAsync([id], cancellationToken)
            ?? throw new NotFoundException("Customer not found.");

        if (customer.IsSuspended)
            throw new InvalidOperationException("Customer is already suspended.");

        customer.IsSuspended = true;
        var notice = templates.StatusNotice(customer, null, EmailDeliveryKind.Suspended);
        foreach (var recipient in CustomerContactResolver.Operational(customer))
            outbox.Enqueue(EmailDeliveryKind.Suspended, recipient, notice.Subject, notice.Html, customer.Id);

        // P13: one license query (ids double as the count) instead of two.
        var licenseIds = await db.Licenses.IgnoreQueryFilters()
            .Where(l => l.CustomerId == customer.Id)
            .Select(l => l.Id)
            .ToListAsync(cancellationToken);

        // P11: single SaveChanges for the suspension, queued emails and audit entry.
        await auditLog.WriteAsync(AuditAction.CustomerSuspended, performedBy, customer.Id, null, null,
            null, ipAddress, cancellationToken);

        await denyList.DenyCustomerLicensesAsync(customer.Id, cancellationToken);
        // P13: deny per license concurrently (bounded batches) instead of one Redis round trip at a time.
        foreach (var batch in licenseIds.Chunk(DenyListBatchSize))
            await Task.WhenAll(batch.Select(licenseId => denyList.DenyLicenseAsync(licenseId, cancellationToken)));

        return MapCustomer(customer, licenseIds.Count);
    }

    public async Task<CustomerDto> ReactivateAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var customer = await db.Customers.FindAsync([id], cancellationToken)
            ?? throw new NotFoundException("Customer not found.");

        if (!customer.IsSuspended)
            throw new InvalidOperationException("Customer is not suspended.");

        customer.IsSuspended = false;

        var licenses = await db.Licenses
            .IgnoreQueryFilters()
            .Where(l => l.CustomerId == customer.Id)
            .Select(l => new { l.Id, l.Status })
            .ToListAsync(cancellationToken);

        // P11: single SaveChanges for the reactivation and its audit entry.
        await auditLog.WriteAsync(AuditAction.CustomerReactivated, performedBy, customer.Id, null, null,
            null, ipAddress, cancellationToken);

        await denyList.ClearCustomerDenyAsync(customer.Id, cancellationToken);
        var activeLicenseIds = licenses.Where(l => l.Status == LicenseStatus.Active).Select(l => l.Id);
        foreach (var batch in activeLicenseIds.Chunk(DenyListBatchSize))
            await Task.WhenAll(batch.Select(licenseId => denyList.ClearLicenseDenyAsync(licenseId, cancellationToken)));

        // P13: the license count comes from the same single query.
        return MapCustomer(customer, licenses.Count);
    }

    private const int DenyListBatchSize = 50;

    private static CustomerDto MapCustomer(Customer customer, int licenseCount) => new()
    {
        Id = customer.Id,
        Name = customer.Name,
        ContactEmail = customer.ContactEmail,
        BillingEmail = customer.BillingEmail,
        TechnicalEmail = customer.TechnicalEmail,
        ContactPhone = customer.ContactPhone,
        InternalNotes = customer.InternalNotes,
        IsSuspended = customer.IsSuspended,
        CreatedAt = customer.CreatedAt,
        LicenseCount = licenseCount
    };

    private static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
}
