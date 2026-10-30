using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Entities;
using Platform.Shared.Enums;

namespace Platform.Api.Services.Renewals;

/// <summary>
/// Signed public pay links: <c>{invoiceId}.{base64url(HMAC-SHA256(key, invoiceId|nonce|expiryTicks))}</c>.
/// Deterministic for a given nonce, so the email, the PDF and the admin "copy link" all show the same URL
/// without storing the token. Clearing or rotating <see cref="Invoice.PayLinkNonce"/> revokes it.
/// </summary>
public sealed class PayLinkService
{
    public const int MaxTokenLength = 80;
    private const int IdLength = 26;
    private const int MacBytes = 32;
    private readonly byte[]? _key;
    private readonly BillingSettings _settings;
    private readonly PaystackOptions _paystack;
    private readonly string _baseUrl;

    public PayLinkService(
        IOptions<BillingSettings> settings,
        IOptions<JwtSettings> jwt,
        IOptions<AuthSettings> auth,
        IOptions<PaystackOptions> paystack)
    {
        _settings = settings.Value;
        _paystack = paystack.Value;
        _baseUrl = auth.Value.ClientBaseUrl.TrimEnd('/');
        var dedicated = _settings.PayLinkSigningKey?.Trim();
        if (!string.IsNullOrEmpty(dedicated) && dedicated.Length >= 32)
            _key = SHA256.HashData(Encoding.UTF8.GetBytes(dedicated));
        else if (!string.IsNullOrWhiteSpace(jwt.Value.Key))
            _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(jwt.Value.Key), 32,
                info: "platform-paylink-v1"u8.ToArray());
    }

    public bool Configured => _key is not null && _settings.PayLinkEnabled;

    public static bool IsPayableStatus(InvoiceStatus status) =>
        status is InvoiceStatus.Sent or InvoiceStatus.Overdue or InvoiceStatus.PartiallyPaid;

    /// <summary>Whether a link should be offered for this invoice (Paystack on, GHS, payable, positive total).</summary>
    public bool IsOfferedFor(Invoice invoice) =>
        Configured
        && _paystack.Enabled
        && IsPayableStatus(invoice.Status)
        && invoice.TotalAmount > 0
        && string.Equals(invoice.Currency, Payments.PaymentCheckoutService.SupportedCurrency, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Issues a nonce when there is none, and pushes the expiry out when it is within a week. The expiry lives only
    /// in the database (not in the token), so extending it keeps every link already emailed working; revoking
    /// (void) clears the nonce, which kills them all. Returns the URL or null.
    /// </summary>
    public string? EnsureUrl(Invoice invoice, DateTime now)
    {
        if (!IsOfferedFor(invoice))
            return null;
        if (invoice.PayLinkNonce is null)
            invoice.PayLinkNonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        if (invoice.PayLinkExpiresAt is null || invoice.PayLinkExpiresAt <= now.AddDays(7))
            invoice.PayLinkExpiresAt = now.AddDays(Math.Clamp(_settings.PayLinkValidDays, 7, 365));
        return BuildUrl(invoice, now);
    }

    /// <summary>Read-only: the current URL if the link is valid now.</summary>
    public string? BuildUrl(Invoice invoice, DateTime now)
    {
        if (!IsOfferedFor(invoice) || invoice.PayLinkNonce is null || invoice.PayLinkExpiresAt is not { } exp || exp <= now)
            return null;
        return $"{_baseUrl}/pay/{CreateToken(invoice.Id, invoice.PayLinkNonce)}";
    }

    /// <summary>Revokes the link (void).</summary>
    public static void Revoke(Invoice invoice)
    {
        invoice.PayLinkNonce = null;
        invoice.PayLinkExpiresAt = null;
    }

    /// <summary>Fully paid: keep the link alive one more day so the payer's return page still loads, then it dies.</summary>
    public static void ShortenAfterPaid(Invoice invoice, DateTime now)
    {
        if (invoice.PayLinkExpiresAt is { } exp && exp > now.AddDays(1))
            invoice.PayLinkExpiresAt = now.AddDays(1);
    }

    public string CreateToken(string invoiceId, string nonce)
    {
        Span<byte> mac = stackalloc byte[MacBytes];
        ComputeMac(invoiceId, nonce, mac);
        return $"{invoiceId}.{Base64Url.EncodeToString(mac)}";
    }

    /// <summary>Cheap syntactic parse (no DB). Rejects anything not shaped like a token.</summary>
    public static bool TryGetInvoiceId(string? token, out string invoiceId)
    {
        invoiceId = string.Empty;
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength || token.Length < IdLength + 2 || token[IdLength] != '.')
            return false;
        foreach (var c in token.AsSpan(0, IdLength))
        {
            if (!char.IsAsciiLetterOrDigit(c))
                return false;
        }
        invoiceId = token[..IdLength].ToUpperInvariant();
        return true;
    }

    /// <summary>
    /// Constant-time signature check. Pass <paramref name="nonce"/> null for an unknown invoice: a MAC is still
    /// computed so a miss costs the same as a hit (no timing oracle for which invoice ids exist).
    /// </summary>
    public bool Verify(string token, string invoiceId, string? nonce)
    {
        if (_key is null || token.Length <= IdLength + 1)
            return false;
        Span<byte> expected = stackalloc byte[MacBytes];
        ComputeMac(invoiceId, nonce ?? "-", expected);
        Span<byte> given = stackalloc byte[MacBytes + 3];
        given.Clear();
        var encoded = token.AsSpan(IdLength + 1);
        var ok = encoded.Length <= 46 && Base64Url.TryDecodeFromChars(encoded, given, out var written) && written == MacBytes;
        var equal = CryptographicOperations.FixedTimeEquals(expected, given[..MacBytes]);
        return ok & equal & nonce is not null;
    }

    private void ComputeMac(string invoiceId, string nonce, Span<byte> destination)
    {
        var payload = Encoding.UTF8.GetBytes($"platform-paylink|{invoiceId}|{nonce}");
        HMACSHA256.HashData(_key ?? new byte[32], payload, destination);
    }
}
