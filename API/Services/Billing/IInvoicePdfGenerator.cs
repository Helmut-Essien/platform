using Platform.Api.Entities;

namespace Platform.Api.Services.Billing;

public interface IInvoicePdfGenerator
{
    byte[] Generate(Invoice invoice, Customer customer, InvoiceLetterhead letterhead);
}
