using Platform.Api.Entities;

namespace Platform.Api.Services.Billing;

public interface IReceiptPdfGenerator
{
    /// <summary>Receipt PDF for one posted payment against <paramref name="invoice"/>.</summary>
    byte[] GenerateReceipt(Receipt receipt, Invoice invoice, Customer customer, InvoiceLetterhead letterhead);
}
