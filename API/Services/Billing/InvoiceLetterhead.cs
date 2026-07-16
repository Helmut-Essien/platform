namespace Platform.Api.Services.Billing;

public sealed record InvoicePaymentOption(string Method, string? Details);

public sealed record InvoiceLetterhead(
    string CompanyName,
    string? AddressLine1,
    string? AddressLine2,
    string? Phone,
    string? Website,
    byte[]? LogoBytes,
    IReadOnlyList<InvoicePaymentOption> PaymentOptions);
