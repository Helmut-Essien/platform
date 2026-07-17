using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
using Platform.Api.Services.Billing;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class BillingService(
    AppDbContext db,
    IAuditLogService auditLog,
    IEmailOutboxService outbox,
    EmailTemplateService templates) : IBillingService
{
    private const int MaxNumberGenerationAttempts = 3;

    public async Task<InvoiceDto> CreateInvoiceAsync(
        CreateInvoiceRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var ownedTransaction = transaction;

        var customer = await db.Customers.FindAsync([request.CustomerId], cancellationToken)
            ?? throw new InvalidOperationException("Customer not found.");

        if (customer.IsSuspended)
            throw new InvalidOperationException("Cannot create invoice for a suspended customer.");

        if (request.LicenseId is not null)
        {
            var license = await db.Licenses
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(l => l.Id == request.LicenseId, cancellationToken)
                ?? throw new InvalidOperationException("License not found.");

            if (license.CustomerId != request.CustomerId)
                throw new InvalidOperationException("License does not belong to the customer.");
        }

        var now = DateTime.UtcNow;
        var total = request.Subtotal + request.TaxAmount;

        var invoice = new Invoice
        {
            CustomerId = request.CustomerId,
            LicenseId = request.LicenseId,
            ServiceProductId = request.ServiceProductId,
            InvoiceNumber = string.Empty,
            Status = request.SendImmediately ? InvoiceStatus.Sent : request.Status,
            IssueDate = now,
            DueDate = DateTimeNormalizer.ToUtc(request.DueDate),
            Currency = request.Currency.ToUpperInvariant(),
            Subtotal = request.Subtotal,
            TaxAmount = request.TaxAmount,
            TotalAmount = total,
            PlanName = request.PlanName,
            Description = request.Description,
            InternalNotes = request.InternalNotes,
            CreatedAt = now,
            UpdatedAt = now
        };

        await SaveNewInvoiceAsync(invoice, cancellationToken);

        var action = request.Status == InvoiceStatus.Sent ? AuditAction.InvoiceSent : AuditAction.InvoiceCreated;
        await auditLog.WriteAsync(action, performedBy, request.CustomerId, request.LicenseId, invoice.Id,
            $$"""{"invoiceNumber":"{{invoice.InvoiceNumber}}","total":{{total}}}""", ipAddress, cancellationToken);

        if (request.LicenseId is not null)
        {
            await auditLog.WriteAsync(AuditAction.InvoiceLinkedToLicense, performedBy, request.CustomerId,
                request.LicenseId, invoice.Id, null, ipAddress, cancellationToken);
        }

        if (invoice.Status == InvoiceStatus.Sent)
            QueueInvoiceEmail(customer, invoice);

        await db.SaveChangesAsync(cancellationToken);
        var result = await MapInvoiceAsync(invoice.Id, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load created invoice.");
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<InvoiceDto> CreateInvoiceForLicenseAsync(
        License license,
        decimal subtotal,
        decimal taxAmount,
        string currency,
        DateTime? dueDate,
        string? description,
        string performedBy,
        InvoiceStatus status = InvoiceStatus.Sent,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        return await CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = license.CustomerId,
            LicenseId = license.Id,
            ServiceProductId = license.ServiceProductId,
            Status = status,
            SendImmediately = status == InvoiceStatus.Sent,
            DueDate = dueDate,
            Currency = currency,
            Subtotal = subtotal,
            TaxAmount = taxAmount,
            PlanName = license.PlanName,
            Description = description ?? $"License {license.PlanName}"
        }, performedBy, ipAddress, cancellationToken);
    }

    public async Task<InvoiceDto?> GetInvoiceAsync(string id, CancellationToken cancellationToken = default)
    {
        return await MapInvoiceAsync(id, cancellationToken);
    }

    public async Task<InvoiceDto> SendInvoiceAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices
            .IgnoreQueryFilters()
            .Include(x => x.Customer)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (invoice.Status is InvoiceStatus.Void or InvoiceStatus.Paid)
            throw new InvalidOperationException($"Cannot send an invoice in status {invoice.Status}.");

        if (invoice.Status == InvoiceStatus.Draft)
            invoice.Status = InvoiceStatus.Sent;
        invoice.UpdatedAt = DateTime.UtcNow;
        QueueInvoiceEmail(invoice.Customer, invoice);
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.InvoiceSent, performedBy, invoice.CustomerId, invoice.LicenseId,
            invoice.Id, $$"""{"invoiceNumber":"{{invoice.InvoiceNumber}}"}""", ipAddress, cancellationToken);
        return await MapInvoiceAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load invoice.");
    }

    public async Task<PagedResult<InvoiceDto>> ListInvoicesAsync(
        string? customerId = null,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        bool unpaidOnly = false,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedPageSize, skip) = PagingHelper.Normalize(page, pageSize);

        var query = db.Invoices
            .AsNoTracking()
            .Include(i => i.Customer)
            .Include(i => i.ServiceProduct)
            .Include(i => i.Receipts)
            .AsQueryable();

        if (customerId is not null)
            query = query.Where(i => i.CustomerId == customerId);

        if (unpaidOnly)
        {
            query = query.Where(i =>
                i.Status == InvoiceStatus.Sent
                || i.Status == InvoiceStatus.PartiallyPaid
                || i.Status == InvoiceStatus.Overdue);
        }

        var ordered = query.OrderByDescending(i => i.IssueDate);

        var totalCount = await ordered.CountAsync(cancellationToken);

        var invoices = await ordered
            .Skip(skip)
            .Take(normalizedPageSize)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return new PagedResult<InvoiceDto>
        {
            Items = invoices.Select(MapInvoice).ToList(),
            TotalCount = totalCount,
            Page = normalizedPage,
            PageSize = normalizedPageSize
        };
    }

    public async Task<InvoiceDto> VoidInvoiceAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices
            .Include(i => i.Receipts)
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (invoice.Status == InvoiceStatus.Void)
            throw new InvalidOperationException("Invoice is already void.");

        if (invoice.Receipts.Count > 0)
            throw new InvalidOperationException("Cannot void an invoice that has receipts.");

        if (invoice.Status is not (InvoiceStatus.Draft or InvoiceStatus.Sent or InvoiceStatus.Overdue))
            throw new InvalidOperationException("Only draft, sent, or overdue invoices can be voided.");

        invoice.Status = InvoiceStatus.Void;
        invoice.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.InvoiceVoided, performedBy, invoice.CustomerId, invoice.LicenseId,
            invoice.Id, $$"""{"invoiceNumber":"{{invoice.InvoiceNumber}}"}""", ipAddress, cancellationToken);

        return MapInvoice(invoice);
    }

    public async Task<ReceiptDto> RecordReceiptAsync(
        string invoiceId,
        RecordReceiptRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.AmountPaid <= 0)
            throw new InvalidOperationException("Amount paid must be greater than zero.");

        var invoice = await db.Invoices
            .Include(i => i.Receipts)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (invoice.Status == InvoiceStatus.Void)
            throw new InvalidOperationException("Cannot record payment for a void invoice.");

        if (invoice.Status == InvoiceStatus.Paid)
            throw new InvalidOperationException("Invoice is already fully paid.");

        var now = DateTime.UtcNow;
        var receipt = new Receipt
        {
            InvoiceId = invoiceId,
            ReceiptNumber = string.Empty,
            AmountPaid = request.AmountPaid,
            PaidAt = DateTimeNormalizer.ToUtc(request.PaidAt) ?? now,
            PaymentMethod = request.PaymentMethod,
            PaymentReference = request.PaymentReference,
            Notes = request.Notes,
            CreatedAt = now
        };

        await SaveNewReceiptAsync(invoice, receipt, cancellationToken);

        var receiptTemplate = templates.PaymentReceipt(invoice.Customer, invoice, receipt);
        outbox.Enqueue(
            EmailDeliveryKind.PaymentReceipt,
            CustomerContactResolver.Billing(invoice.Customer),
            receiptTemplate.Subject,
            receiptTemplate.Html,
            invoice.CustomerId,
            invoice.LicenseId,
            invoice.Id,
            receipt.Id);
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.ReceiptRecorded, performedBy, invoice.CustomerId, invoice.LicenseId,
            invoice.Id,
            $$"""{"receiptNumber":"{{receipt.ReceiptNumber}}","amount":{{request.AmountPaid}}}""",
            ipAddress, cancellationToken);

        return MapReceipt(receipt);
    }

    private async Task UpdateInvoicePaymentStatusAsync(Invoice invoice, CancellationToken cancellationToken)
    {
        var paid = invoice.Receipts.Sum(r => r.AmountPaid);

        if (paid >= invoice.TotalAmount)
            invoice.Status = InvoiceStatus.Paid;
        else if (paid > 0)
            invoice.Status = InvoiceStatus.PartiallyPaid;
        else if (invoice.DueDate.HasValue && invoice.DueDate < DateTime.UtcNow && invoice.Status == InvoiceStatus.Sent)
            invoice.Status = InvoiceStatus.Overdue;

        await Task.CompletedTask;
    }

    private async Task SaveNewInvoiceAsync(Invoice invoice, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxNumberGenerationAttempts; attempt++)
        {
            invoice.InvoiceNumber = await GenerateInvoiceNumberAsync(cancellationToken);
            db.Invoices.Add(invoice);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException ex) when (PostgresUniqueViolation.IsUniqueViolation(ex))
            {
                db.Entry(invoice).State = EntityState.Detached;
                if (attempt == MaxNumberGenerationAttempts - 1)
                    throw new InvalidOperationException("Could not generate a unique invoice number.", ex);
            }
        }
    }

    private async Task SaveNewReceiptAsync(
        Invoice invoice,
        Receipt receipt,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxNumberGenerationAttempts; attempt++)
        {
            receipt.ReceiptNumber = await GenerateReceiptNumberAsync(cancellationToken);
            db.Receipts.Add(receipt);
            invoice.Receipts.Add(receipt);
            await UpdateInvoicePaymentStatusAsync(invoice, cancellationToken);
            invoice.UpdatedAt = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException ex) when (PostgresUniqueViolation.IsUniqueViolation(ex))
            {
                db.Entry(receipt).State = EntityState.Detached;
                invoice.Receipts.Remove(receipt);
                await UpdateInvoicePaymentStatusAsync(invoice, cancellationToken);
                if (attempt == MaxNumberGenerationAttempts - 1)
                    throw new InvalidOperationException("Could not generate a unique receipt number.", ex);
            }
        }
    }

    private async Task<string> GenerateInvoiceNumberAsync(CancellationToken cancellationToken)
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"INV-{year}-";
        var count = await db.Invoices
            .IgnoreQueryFilters()
            .CountAsync(i => i.InvoiceNumber.StartsWith(prefix), cancellationToken);

        return $"{prefix}{(count + 1):D5}";
    }

    private async Task<string> GenerateReceiptNumberAsync(CancellationToken cancellationToken)
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"RCP-{year}-";
        var count = await db.Receipts.CountAsync(r => r.ReceiptNumber.StartsWith(prefix), cancellationToken);

        return $"{prefix}{(count + 1):D5}";
    }

    private void QueueInvoiceEmail(Customer customer, Invoice invoice)
    {
        var template = templates.Invoice(customer, invoice);
        outbox.Enqueue(
            EmailDeliveryKind.Invoice,
            CustomerContactResolver.Billing(customer),
            template.Subject,
            template.Html,
            customer.Id,
            invoice.LicenseId,
            invoice.Id);
    }

    private async Task<InvoiceDto?> MapInvoiceAsync(string id, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices
            .AsNoTracking()
            .Include(i => i.Customer)
            .Include(i => i.ServiceProduct)
            .Include(i => i.Receipts)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        return invoice is null ? null : MapInvoice(invoice);
    }

    private static InvoiceDto MapInvoice(Invoice invoice)
    {
        var paid = invoice.Receipts.Sum(r => r.AmountPaid);

        return new InvoiceDto
        {
            Id = invoice.Id,
            CustomerId = invoice.CustomerId,
            CustomerName = invoice.Customer?.Name,
            LicenseId = invoice.LicenseId,
            ServiceProductId = invoice.ServiceProductId,
            ServiceProductCode = invoice.ServiceProduct?.Code,
            InvoiceNumber = invoice.InvoiceNumber,
            Status = invoice.Status,
            IssueDate = invoice.IssueDate,
            DueDate = invoice.DueDate,
            Currency = invoice.Currency,
            Subtotal = invoice.Subtotal,
            TaxAmount = invoice.TaxAmount,
            TotalAmount = invoice.TotalAmount,
            AmountPaid = paid,
            AmountDue = Math.Max(0, invoice.TotalAmount - paid),
            PlanName = invoice.PlanName,
            Description = invoice.Description,
            InternalNotes = invoice.InternalNotes,
            CreatedAt = invoice.CreatedAt,
            UpdatedAt = invoice.UpdatedAt,
            Receipts = invoice.Receipts.OrderByDescending(r => r.PaidAt).Select(MapReceipt).ToList()
        };
    }

    private static ReceiptDto MapReceipt(Receipt receipt) => new()
    {
        Id = receipt.Id,
        InvoiceId = receipt.InvoiceId,
        ReceiptNumber = receipt.ReceiptNumber,
        AmountPaid = receipt.AmountPaid,
        PaidAt = receipt.PaidAt,
        PaymentMethod = receipt.PaymentMethod,
        PaymentReference = receipt.PaymentReference,
        Notes = receipt.Notes,
        CreatedAt = receipt.CreatedAt
    };
}
