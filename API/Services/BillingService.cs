using System.Net;
using Microsoft.EntityFrameworkCore;
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
    IEmailSender emailSender,
    IInvoicePdfGenerator invoicePdfGenerator,
    IInvoiceBrandService invoiceBrand,
    ILogger<BillingService> logger) : IBillingService
{
    private const int MaxNumberGenerationAttempts = 3;

    public async Task<InvoiceDto> CreateInvoiceAsync(
        CreateInvoiceRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
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
            Status = request.Status,
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
        {
            await SendInvoiceEmailAsync(customer, invoice, cancellationToken);
        }

        return await MapInvoiceAsync(invoice.Id, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load created invoice.");
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

    public async Task<PagedResult<InvoiceDto>> ListInvoicesAsync(
        string? customerId = null,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
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

    private async Task SendInvoiceEmailAsync(Customer customer, Invoice invoice, CancellationToken cancellationToken)
    {
        try
        {
            if (invoice.ServiceProduct is null && invoice.ServiceProductId is not null)
            {
                invoice.ServiceProduct = await db.ServiceProducts.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == invoice.ServiceProductId, cancellationToken);
            }

            var subject = $"Invoice {invoice.InvoiceNumber} from Platform License Hub";
            var htmlBody = BuildInvoiceEmailBody(customer.Name, invoice);
            var profile = await invoiceBrand.GetProfileEntityAsync(cancellationToken);
            var letterhead = new InvoiceLetterhead(
                profile.CompanyName,
                profile.AddressLine1,
                profile.AddressLine2,
                profile.Phone,
                profile.Website,
                profile.LogoBytes);
            var pdfBytes = invoicePdfGenerator.Generate(invoice, customer, letterhead);
            var attachments = new List<EmailAttachment>
            {
                new($"{invoice.InvoiceNumber}.pdf", "application/pdf", pdfBytes)
            };

            await emailSender.SendAsync(
                customer.ContactEmail,
                subject,
                htmlBody,
                attachments,
                cancellationToken);
            logger.LogInformation(
                "Invoice email sent for {InvoiceNumber} to {Recipient} with PDF attachment ({PdfBytes} bytes)",
                invoice.InvoiceNumber,
                customer.ContactEmail,
                pdfBytes.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send invoice email for {InvoiceNumber} to {Recipient}", invoice.InvoiceNumber, customer.ContactEmail);
        }
    }

    private static string BuildInvoiceEmailBody(string customerName, Invoice invoice)
    {
        var currencySymbol = invoice.Currency switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            "GHS" => "GH₵",
            _ => invoice.Currency + " "
        };

        var dueDate = invoice.DueDate?.ToString("MMMM dd, yyyy") ?? "Upon receipt";

        return $"""
            <html><body style="font-family:sans-serif;color:#1a1a1a;">
            <p>Hello {WebUtility.HtmlEncode(customerName)},</p>
            <p>A new invoice has been issued for your account. Please find your invoice PDF attached.</p>
            <table style="border-collapse:collapse;width:100%;max-width:480px;">
              <tr><td style="padding:6px 12px;border:1px solid #ddd;background:#f5f5f5;"><strong>Invoice</strong></td><td style="padding:6px 12px;border:1px solid #ddd;">{WebUtility.HtmlEncode(invoice.InvoiceNumber)}</td></tr>
              <tr><td style="padding:6px 12px;border:1px solid #ddd;background:#f5f5f5;"><strong>Date</strong></td><td style="padding:6px 12px;border:1px solid #ddd;">{invoice.IssueDate:MMMM dd, yyyy}</td></tr>
              <tr><td style="padding:6px 12px;border:1px solid #ddd;background:#f5f5f5;"><strong>Due Date</strong></td><td style="padding:6px 12px;border:1px solid #ddd;">{dueDate}</td></tr>
              <tr><td style="padding:6px 12px;border:1px solid #ddd;background:#f5f5f5;"><strong>Plan</strong></td><td style="padding:6px 12px;border:1px solid #ddd;">{WebUtility.HtmlEncode(invoice.PlanName ?? "—")}</td></tr>
              <tr><td style="padding:6px 12px;border:1px solid #ddd;background:#f5f5f5;"><strong>Description</strong></td><td style="padding:6px 12px;border:1px solid #ddd;">{WebUtility.HtmlEncode(invoice.Description ?? "—")}</td></tr>
              <tr><td style="padding:6px 12px;border:1px solid #ddd;background:#f5f5f5;"><strong>Subtotal</strong></td><td style="padding:6px 12px;border:1px solid #ddd;">{currencySymbol}{invoice.Subtotal:N2}</td></tr>
              <tr><td style="padding:6px 12px;border:1px solid #ddd;background:#f5f5f5;"><strong>Tax</strong></td><td style="padding:6px 12px;border:1px solid #ddd;">{currencySymbol}{invoice.TaxAmount:N2}</td></tr>
              <tr><td style="padding:6px 12px;border:1px solid #ddd;background:#f5f5f5;"><strong>Total</strong></td><td style="padding:6px 12px;border:1px solid #ddd;font-weight:bold;">{currencySymbol}{invoice.TotalAmount:N2}</td></tr>
            </table>
            <p style="margin-top:16px;color:#666;">Download the attached PDF for your records. Thank you for your business.</p>
            </body></html>
            """;
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
