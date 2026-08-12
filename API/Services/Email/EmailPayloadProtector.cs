using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;

namespace Platform.Api.Services.Email;

public class EmailPayloadProtector
{
    private readonly byte[] _key;

    public EmailPayloadProtector(IOptions<EmailSettings> settings, IConfiguration configuration)
    {
        var configured = settings.Value.Outbox.EncryptionKey;
        var keyMaterial = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : configuration["Jwt:Key"];

        if (string.IsNullOrWhiteSpace(keyMaterial))
            throw new InvalidOperationException(
                "Email outbox encryption requires Email:Outbox:EncryptionKey.");

        _key = TryDecodeKey(keyMaterial) ?? SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
    }

    public byte[] Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];

        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, nonce.Length);
        ciphertext.CopyTo(result, nonce.Length + tag.Length);
        CryptographicOperations.ZeroMemory(plaintextBytes);
        return result;
    }

    public string Unprotect(byte[] protectedPayload)
    {
        if (protectedPayload.Length < 29)
            throw new CryptographicException("Invalid encrypted email payload.");

        var nonce = protectedPayload.AsSpan(0, 12);
        var tag = protectedPayload.AsSpan(12, 16);
        var ciphertext = protectedPayload.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[]? TryDecodeKey(string value)
    {
        var trimmed = value.Trim();
        // Avoid Convert.FromBase64String so plain-text secrets (e.g. Jwt:Key) do not
        // raise a first-chance FormatException under the debugger.
        if (trimmed.Length is 0 || trimmed.Length % 4 != 0)
            return null;

        var buffer = new byte[trimmed.Length];
        if (!Convert.TryFromBase64String(trimmed, buffer, out var written))
            return null;

        if (written is not (16 or 24 or 32))
            return null;

        return buffer.AsSpan(0, written).ToArray();
    }
}
