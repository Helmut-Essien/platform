using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class BillingService(
    AppDbContext db,
    IAuditLogService auditLog,
    IEmailOutboxService outbox,
    EmailTemplateService templates,
    ILicenseDenyListService denyList) : IBillingService
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
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(i => i.Customer)
            .Include(i => i.ServiceProduct)
            .Include(i => i.Receipts)
            .ThenInclude(r => r.PaymentTransaction)
            .Include(i => i.PaymentTransactions)
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
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var ownedTransaction = transaction;

        var invoice = await db.Invoices
            .IgnoreQueryFilters()
            .Include(i => i.Receipts)
            .Include(i => i.PaymentTransactions)
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (invoice.Status == InvoiceStatus.Void)
            throw new InvalidOperationException("Invoice is already void.");

        if (GetNetPaid(invoice) > 0 || invoice.Receipts.Any(r => r.Status == ReceiptStatus.Posted))
            throw new InvalidOperationException("Cannot void an invoice that has posted payments.");

        if (invoice.Status is not (InvoiceStatus.Draft or InvoiceStatus.Sent or InvoiceStatus.Overdue or InvoiceStatus.PartiallyPaid))
            throw new InvalidOperationException("Only draft, sent, overdue, or unpaid invoices can be voided.");

        // PartiallyPaid with net 0 after full reversal is allowed.
        if (invoice.Status == InvoiceStatus.PartiallyPaid && GetNetPaid(invoice) != 0)
            throw new InvalidOperationException("Cannot void an invoice that has posted payments.");

        invoice.Status = InvoiceStatus.Void;
        invoice.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.InvoiceVoided, performedBy, invoice.CustomerId, invoice.LicenseId,
            invoice.Id, $$"""{"invoiceNumber":"{{invoice.InvoiceNumber}}"}""", ipAddress, cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

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

        var idempotencyKey = NormalizeRequired(request.IdempotencyKey, "Idempotency key");
        var paymentReference = NormalizeOptional(request.PaymentReference);
        ValidatePaymentReference(request.PaymentMethod, paymentReference);

        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var ownedTransaction = transaction;

        await LockInvoiceRowAsync(invoiceId, cancellationToken);

        var existingByKey = await db.PaymentTransactions
            .IgnoreQueryFilters()
            .Include(x => x.Receipt)
            .FirstOrDefaultAsync(
                x => x.InvoiceId == invoiceId && x.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existingByKey is not null)
        {
            if (existingByKey.Kind != PaymentTransactionKind.Payment || existingByKey.Receipt is null)
                throw new ConflictException("Idempotency key was already used for a different payment operation.");
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return MapReceipt(existingByKey.Receipt);
        }

        var invoice = await db.Invoices
            .IgnoreQueryFilters()
            .Include(i => i.Customer)
            .Include(i => i.Receipts)
            .Include(i => i.PaymentTransactions)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (invoice.Status is not (InvoiceStatus.Sent or InvoiceStatus.PartiallyPaid or InvoiceStatus.Overdue))
            throw new InvalidOperationException($"Cannot record payment for an invoice in status {invoice.Status}.");

        var amountDue = GetAmountDue(invoice);
        if (request.AmountPaid > amountDue)
            throw new InvalidOperationException($"Amount paid exceeds balance due of {amountDue:0.00}.");

        if (!string.IsNullOrEmpty(paymentReference)
            && invoice.PaymentTransactions.Any(t =>
                t.Kind == PaymentTransactionKind.Payment
                && t.PaymentMethod == request.PaymentMethod
                && string.Equals(t.PaymentReference, paymentReference, StringComparison.OrdinalIgnoreCase)
                && !IsReversed(invoice, t)))
        {
            throw new ConflictException(
                "A payment with the same method and reference already exists for this invoice.");
        }

        var now = DateTime.UtcNow;
        var receipt = new Receipt
        {
            InvoiceId = invoiceId,
            ReceiptNumber = string.Empty,
            AmountPaid = request.AmountPaid,
            PaidAt = DateTimeNormalizer.ToUtc(request.PaidAt) ?? now,
            PaymentMethod = request.PaymentMethod,
            PaymentReference = paymentReference,
            Notes = NormalizeOptional(request.Notes),
            Status = ReceiptStatus.Posted,
            CreatedAt = now
        };

        PaymentTransaction? payment = null;
        for (var attempt = 0; attempt < MaxNumberGenerationAttempts; attempt++)
        {
            receipt.ReceiptNumber = await GenerateReceiptNumberAsync(cancellationToken);
            if (attempt == 0)
            {
                db.Receipts.Add(receipt);
                invoice.Receipts.Add(receipt);
            }

            payment = new PaymentTransaction
            {
                InvoiceId = invoiceId,
                Kind = PaymentTransactionKind.Payment,
                Amount = request.AmountPaid,
                PaymentMethod = request.PaymentMethod,
                PaymentReference = paymentReference,
                Notes = receipt.Notes,
                ReceiptId = receipt.Id,
                IdempotencyKey = idempotencyKey,
                PerformedBy = performedBy,
                CreatedAt = now,
                Receipt = receipt
            };
            db.PaymentTransactions.Add(payment);
            invoice.PaymentTransactions.Add(payment);
            RecalculateInvoiceStatus(invoice);
            invoice.UpdatedAt = now;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException ex) when (PostgresUniqueViolation.IsUniqueViolation(ex)
                && attempt < MaxNumberGenerationAttempts - 1
                && IsReceiptNumberConflict(ex))
            {
                db.Entry(payment).State = EntityState.Detached;
                invoice.PaymentTransactions.Remove(payment);
                // Keep receipt tracked; regenerate number on next attempt.
            }
            catch (DbUpdateException ex) when (PostgresUniqueViolation.IsUniqueViolation(ex))
            {
                // Likely idempotency race — return existing if present.
                var raced = await db.PaymentTransactions
                    .IgnoreQueryFilters()
                    .Include(x => x.Receipt)
                    .FirstOrDefaultAsync(
                        x => x.InvoiceId == invoiceId && x.IdempotencyKey == idempotencyKey,
                        cancellationToken);
                if (raced?.Receipt is not null)
                {
                    if (transaction is not null)
                        await transaction.RollbackAsync(cancellationToken);
                    return MapReceipt(raced.Receipt);
                }

                throw new ConflictException("Payment could not be recorded due to a conflict.", ex);
            }
        }

        if (payment is null)
            throw new InvalidOperationException("Could not generate a unique receipt number.");

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

        await auditLog.WriteAsync(AuditAction.ReceiptRecorded, performedBy, invoice.CustomerId, invoice.LicenseId,
            invoice.Id,
            $$"""{"receiptNumber":"{{receipt.ReceiptNumber}}","amount":{{request.AmountPaid}},"transactionId":"{{payment.Id}}"}""",
            ipAddress, cancellationToken);

        var licenseIdToClearDeny = await TryQueueLicenseReactivationAsync(invoice, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        if (licenseIdToClearDeny is not null)
            await denyList.ClearLicenseDenyAsync(licenseIdToClearDeny, cancellationToken);

        return MapReceipt(receipt);
    }

    public async Task<ReceiptDto> ReverseReceiptAsync(
        string invoiceId,
        string receiptId,
        ReverseReceiptRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var idempotencyKey = NormalizeRequired(request.IdempotencyKey, "Idempotency key");
        var reason = NormalizeRequired(request.Reason, "Reason");

        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var ownedTransaction = transaction;

        await LockInvoiceRowAsync(invoiceId, cancellationToken);

        var existingByKey = await db.PaymentTransactions
            .IgnoreQueryFilters()
            .Include(x => x.ReversesTransaction)
            .ThenInclude(p => p!.Receipt)
            .FirstOrDefaultAsync(
                x => x.InvoiceId == invoiceId && x.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existingByKey is not null)
        {
            if (existingByKey.Kind != PaymentTransactionKind.Reversal
                || existingByKey.ReversesTransaction?.Receipt is null
                || existingByKey.ReversesTransaction.Receipt.Id != receiptId)
            {
                throw new ConflictException("Idempotency key was already used for a different payment operation.");
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return MapReceipt(existingByKey.ReversesTransaction.Receipt);
        }

        var invoice = await db.Invoices
            .IgnoreQueryFilters()
            .Include(i => i.Customer)
            .Include(i => i.Receipts)
            .ThenInclude(r => r.PaymentTransaction)
            .Include(i => i.PaymentTransactions)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (invoice.Status == InvoiceStatus.Void)
            throw new InvalidOperationException("Cannot reverse payment on a void invoice.");

        var receipt = invoice.Receipts.FirstOrDefault(r => r.Id == receiptId)
            ?? throw new InvalidOperationException("Receipt not found.");

        if (receipt.Status == ReceiptStatus.Reversed)
            throw new InvalidOperationException("Receipt is already reversed.");

        var payment = invoice.PaymentTransactions.FirstOrDefault(t =>
            t.Kind == PaymentTransactionKind.Payment && t.ReceiptId == receipt.Id)
            ?? throw new InvalidOperationException("Payment transaction for receipt was not found.");

        if (invoice.PaymentTransactions.Any(t =>
                t.Kind == PaymentTransactionKind.Reversal && t.ReversesTransactionId == payment.Id))
            throw new InvalidOperationException("Payment has already been reversed.");

        var now = DateTime.UtcNow;
        var reversal = new PaymentTransaction
        {
            InvoiceId = invoiceId,
            Kind = PaymentTransactionKind.Reversal,
            Amount = payment.Amount,
            PaymentMethod = payment.PaymentMethod,
            PaymentReference = payment.PaymentReference,
            Notes = reason,
            ReversesTransactionId = payment.Id,
            IdempotencyKey = idempotencyKey,
            PerformedBy = performedBy,
            CreatedAt = now
        };
        db.PaymentTransactions.Add(reversal);
        invoice.PaymentTransactions.Add(reversal);

        receipt.Status = ReceiptStatus.Reversed;
        receipt.ReversedAt = now;
        receipt.ReversalReason = reason;

        RecalculateInvoiceStatus(invoice);
        invoice.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresUniqueViolation.IsUniqueViolation(ex))
        {
            var raced = await db.PaymentTransactions
                .IgnoreQueryFilters()
                .Include(x => x.ReversesTransaction)
                .ThenInclude(p => p!.Receipt)
                .FirstOrDefaultAsync(
                    x => x.InvoiceId == invoiceId && x.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            if (raced?.ReversesTransaction?.Receipt is not null)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(cancellationToken);
                return MapReceipt(raced.ReversesTransaction.Receipt);
            }

            throw new ConflictException("Payment reversal could not be recorded due to a conflict.", ex);
        }

        await auditLog.WriteAsync(AuditAction.ReceiptReversed, performedBy, invoice.CustomerId, invoice.LicenseId,
            invoice.Id,
            $$"""{"receiptNumber":"{{receipt.ReceiptNumber}}","amount":{{payment.Amount}},"transactionId":"{{reversal.Id}}","reason":{{System.Text.Json.JsonSerializer.Serialize(reason)}}}""",
            ipAddress, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return MapReceipt(receipt);
    }

    private async Task LockInvoiceRowAsync(string invoiceId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
            return;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Invoices" WHERE "Id" = {invoiceId} FOR UPDATE""",
            cancellationToken);
    }

    private async Task<string?> TryQueueLicenseReactivationAsync(Invoice invoice, CancellationToken cancellationToken)
    {
        if (invoice.LicenseId is null || invoice.Status != InvoiceStatus.Paid)
            return null;

        var license = await db.Licenses
            .IgnoreQueryFilters()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == invoice.LicenseId, cancellationToken);
        if (license is null)
            return null;

        if (license.Status != LicenseStatus.Suspended
            || string.IsNullOrEmpty(license.AutoSuspendedForOverdueInvoiceId)
            || license.Customer.IsSuspended)
            return null;

        var hasBlockingOverdue = await db.Invoices
            .IgnoreQueryFilters()
            .AnyAsync(
                i => i.LicenseId == license.Id
                    && i.Id != invoice.Id
                    && (i.Status == InvoiceStatus.Overdue
                        || (i.Status == InvoiceStatus.PartiallyPaid
                            && i.DueDate.HasValue
                            && i.DueDate < DateTime.UtcNow)),
                cancellationToken);
        if (hasBlockingOverdue)
            return null;

        license.Status = LicenseStatus.Active;
        license.AutoSuspendedForOverdueInvoiceId = null;
        license.UpdatedAt = DateTime.UtcNow;

        var notice = templates.StatusNotice(
            license.Customer,
            license.ServiceProduct,
            EmailDeliveryKind.LicenseReactivated,
            "Access was restored after the overdue invoice was paid.");
        outbox.Enqueue(
            EmailDeliveryKind.LicenseReactivated,
            CustomerContactResolver.Technical(license.Customer),
            notice.Subject,
            notice.Html,
            license.CustomerId,
            license.Id);

        await auditLog.WriteAsync(
            AuditAction.LicenseAutoReactivatedPaid,
            "system:payment-received",
            license.CustomerId,
            license.Id,
            invoice.Id,
            $$"""{"invoiceId":"{{invoice.Id}}"}""",
            cancellationToken: cancellationToken);

        return license.Id;
    }

    private static void RecalculateInvoiceStatus(Invoice invoice)
    {
        var netPaid = GetNetPaid(invoice);
        if (netPaid >= invoice.TotalAmount)
        {
            invoice.Status = InvoiceStatus.Paid;
            return;
        }

        if (netPaid > 0)
        {
            invoice.Status = InvoiceStatus.PartiallyPaid;
            return;
        }

        if (invoice.DueDate.HasValue && invoice.DueDate.Value < DateTime.UtcNow)
            invoice.Status = InvoiceStatus.Overdue;
        else
            invoice.Status = InvoiceStatus.Sent;
    }

    private static decimal GetNetPaid(Invoice invoice)
    {
        if (invoice.PaymentTransactions.Count > 0)
        {
            var payments = invoice.PaymentTransactions
                .Where(t => t.Kind == PaymentTransactionKind.Payment)
                .Sum(t => t.Amount);
            var reversals = invoice.PaymentTransactions
                .Where(t => t.Kind == PaymentTransactionKind.Reversal)
                .Sum(t => t.Amount);
            return payments - reversals;
        }

        return invoice.Receipts
            .Where(r => r.Status == ReceiptStatus.Posted)
            .Sum(r => r.AmountPaid);
    }

    private static decimal GetAmountDue(Invoice invoice) =>
        Math.Max(0, invoice.TotalAmount - GetNetPaid(invoice));

    private static bool IsReversed(Invoice invoice, PaymentTransaction payment) =>
        invoice.PaymentTransactions.Any(t =>
            t.Kind == PaymentTransactionKind.Reversal && t.ReversesTransactionId == payment.Id);

    private static void ValidatePaymentReference(PaymentMethod method, string? paymentReference)
    {
        if (method == PaymentMethod.Cash)
            return;

        if (string.IsNullOrWhiteSpace(paymentReference))
            throw new InvalidOperationException("Payment reference is required for this payment method.");
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
            throw new InvalidOperationException($"{fieldName} is required.");
        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim();
    }

    private static bool IsReceiptNumberConflict(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("ReceiptNumber", StringComparison.OrdinalIgnoreCase)
            || message.Contains("IX_Receipts_ReceiptNumber", StringComparison.OrdinalIgnoreCase);
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
        var count = await db.Receipts
            .IgnoreQueryFilters()
            .CountAsync(r => r.ReceiptNumber.StartsWith(prefix), cancellationToken);

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
            .IgnoreQueryFilters()
            .Include(i => i.Customer)
            .Include(i => i.ServiceProduct)
            .Include(i => i.Receipts)
            .ThenInclude(r => r.PaymentTransaction)
            .Include(i => i.PaymentTransactions)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        return invoice is null ? null : MapInvoice(invoice);
    }

    private static InvoiceDto MapInvoice(Invoice invoice)
    {
        var paid = GetNetPaid(invoice);

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
            Receipts = invoice.Receipts.OrderByDescending(r => r.PaidAt).Select(MapReceipt).ToList(),
            Transactions = invoice.PaymentTransactions
                .OrderByDescending(t => t.CreatedAt)
                .Select(MapTransaction)
                .ToList()
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
        Status = receipt.Status,
        PaymentTransactionId = receipt.PaymentTransaction?.Id,
        ReversedAt = receipt.ReversedAt,
        ReversalReason = receipt.ReversalReason,
        CreatedAt = receipt.CreatedAt
    };

    private static PaymentTransactionDto MapTransaction(PaymentTransaction transaction) => new()
    {
        Id = transaction.Id,
        InvoiceId = transaction.InvoiceId,
        Kind = transaction.Kind,
        Amount = transaction.Amount,
        PaymentMethod = transaction.PaymentMethod,
        PaymentReference = transaction.PaymentReference,
        Notes = transaction.Notes,
        ReceiptId = transaction.ReceiptId,
        ReversesTransactionId = transaction.ReversesTransactionId,
        IdempotencyKey = transaction.IdempotencyKey,
        PerformedBy = transaction.PerformedBy,
        CreatedAt = transaction.CreatedAt
    };
}
