using Platform.Api.Entities;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public interface IBillingService
{
    Task<InvoiceDto> CreateInvoiceAsync(CreateInvoiceRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<InvoiceDto> CreateInvoiceForLicenseAsync(License license, decimal subtotal, decimal taxAmount, string currency, DateTime? dueDate, string? description, string performedBy, InvoiceStatus status = InvoiceStatus.Sent, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<InvoiceDto?> GetInvoiceAsync(string id, CancellationToken cancellationToken = default);

    Task<InvoiceDto> SendInvoiceAsync(string id, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<PagedResult<InvoiceDto>> ListInvoicesAsync(
        string? customerId = null,
        int page = 1,
        int pageSize = 25,
        bool unpaidOnly = false,
        CancellationToken cancellationToken = default);

    Task<InvoiceDto> VoidInvoiceAsync(string id, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<ReceiptDto> RecordReceiptAsync(string invoiceId, RecordReceiptRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<ReceiptDto> ReverseReceiptAsync(string invoiceId, string receiptId, ReverseReceiptRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);
}
