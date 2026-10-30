using System.Security.Cryptography;
using System.Text;

namespace Platform.Api.Services.Payments;

/// <summary>Paystack webhook signature: hex HMAC-SHA512 of the raw body keyed with the secret key (x-paystack-signature).</summary>
public static class PaystackSignature
{
    public const string HeaderName = "x-paystack-signature";

    public static string Compute(string secretKey, ReadOnlySpan<byte> body) =>
        Convert.ToHexStringLower(HMACSHA512.HashData(Encoding.UTF8.GetBytes(secretKey.Trim()), body));

    public static bool IsValid(string? secretKey, ReadOnlySpan<byte> body, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(signatureHeader))
            return false;

        var header = signatureHeader.Trim();
        if (header.Length != HMACSHA512.HashSizeInBytes * 2)
            return false;

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(header);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA512.HashData(Encoding.UTF8.GetBytes(secretKey.Trim()), body);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
