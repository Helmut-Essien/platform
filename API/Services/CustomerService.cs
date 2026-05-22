using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Shared.Dtos.Customers;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class CustomerService(AppDbContext db, IAuditLogService auditLog) : ICustomerService
{
    public async Task<IReadOnlyList<CustomerDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var customers = await db.Customers
            .AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        var licenseCounts = await db.Licenses
            .IgnoreQueryFilters()
            .AsNoTracking()
            .GroupBy(l => l.CustomerId)
            .Select(g => new { CustomerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Count, cancellationToken);

        return customers.Select(c => MapCustomer(c, licenseCounts.GetValueOrDefault(c.Id))).ToList();
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
            ContactPhone = request.ContactPhone?.Trim(),
            InternalNotes = request.InternalNotes?.Trim(),
            IsSuspended = false,
            CreatedAt = now
        };

        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.CustomerCreated, performedBy, customer.Id, null, null,
            $$"""{"name":"{{customer.Name}}","email":"{{customer.ContactEmail}}"}""", ipAddress, cancellationToken);

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
            ?? throw new InvalidOperationException("Customer not found.");

        var normalizedEmail = request.ContactEmail.Trim().ToLowerInvariant();

        if (await db.Customers.AnyAsync(c => c.Id != id && c.ContactEmail == normalizedEmail, cancellationToken))
            throw new InvalidOperationException("A customer with this email already exists.");

        customer.Name = request.Name.Trim();
        customer.ContactEmail = normalizedEmail;
        customer.ContactPhone = request.ContactPhone?.Trim();
        customer.InternalNotes = request.InternalNotes?.Trim();

        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.CustomerUpdated, performedBy, customer.Id, null, null,
            $$"""{"name":"{{customer.Name}}"}""", ipAddress, cancellationToken);

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
            ?? throw new InvalidOperationException("Customer not found.");

        if (customer.IsSuspended)
            throw new InvalidOperationException("Customer is already suspended.");

        customer.IsSuspended = true;
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.CustomerSuspended, performedBy, customer.Id, null, null,
            null, ipAddress, cancellationToken);

        var licenseCount = await db.Licenses.IgnoreQueryFilters().CountAsync(l => l.CustomerId == id, cancellationToken);
        return MapCustomer(customer, licenseCount);
    }

    public async Task<CustomerDto> ReactivateAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var customer = await db.Customers.FindAsync([id], cancellationToken)
            ?? throw new InvalidOperationException("Customer not found.");

        if (!customer.IsSuspended)
            throw new InvalidOperationException("Customer is not suspended.");

        customer.IsSuspended = false;
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.CustomerReactivated, performedBy, customer.Id, null, null,
            null, ipAddress, cancellationToken);

        var licenseCount = await db.Licenses.IgnoreQueryFilters().CountAsync(l => l.CustomerId == id, cancellationToken);
        return MapCustomer(customer, licenseCount);
    }

    private static CustomerDto MapCustomer(Customer customer, int licenseCount) => new()
    {
        Id = customer.Id,
        Name = customer.Name,
        ContactEmail = customer.ContactEmail,
        ContactPhone = customer.ContactPhone,
        InternalNotes = customer.InternalNotes,
        IsSuspended = customer.IsSuspended,
        CreatedAt = customer.CreatedAt,
        LicenseCount = licenseCount
    };
}
