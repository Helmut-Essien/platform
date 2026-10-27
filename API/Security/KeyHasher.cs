using System.Security.Cryptography;
using System.Text;

namespace Platform.Api.Security;

/// <summary>
/// Verifier hashes for integration keys and license keys.
/// These keys are machine-generated random secrets that are validated on every API call,
/// so a deliberately slow password hash (BCrypt, ~120 ms CPU per verify) only costs latency
/// and throughput. New hashes use a salted SHA-256 ("v2$salt$hash") verified in constant time.
/// Legacy BCrypt hashes ("$2a$...") are still accepted so existing keys keep working;
/// callers should upgrade them after a successful verify (see <see cref="Verify"/>).
/// </summary>
public static class KeyHasher
{
    private const string V2Prefix = "v2$";
    private const int SaltBytes = 16;

    public static string Hash(string plainKey)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(salt, plainKey);
        return $"{V2Prefix}{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool IsLegacy(string? storedHash) =>
        !string.IsNullOrEmpty(storedHash) && !storedHash.StartsWith(V2Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Verifies a plain key against a stored hash. <paramref name="needsUpgrade"/> is true when the
    /// stored value is a legacy BCrypt hash that verified successfully and should be rewritten with <see cref="Hash"/>.
    /// </summary>
    public static bool Verify(string plainKey, string? storedHash, out bool needsUpgrade)
    {
        needsUpgrade = false;
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(plainKey))
            return false;

        if (storedHash.StartsWith(V2Prefix, StringComparison.Ordinal))
        {
            var parts = storedHash.Split('$');
            if (parts.Length != 3)
                return false;

            try
            {
                var salt = Convert.FromBase64String(parts[1]);
                var expected = Convert.FromBase64String(parts[2]);
                var actual = Compute(salt, plainKey);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        try
        {
            var ok = BCrypt.Net.BCrypt.Verify(plainKey, storedHash);
            needsUpgrade = ok;
            return ok;
        }
        catch (Exception ex) when (ex is BCrypt.Net.SaltParseException or ArgumentException)
        {
            return false;
        }
    }

    private static byte[] Compute(byte[] salt, string plainKey)
    {
        var key = Encoding.UTF8.GetBytes(plainKey);
        var input = new byte[salt.Length + key.Length];
        Buffer.BlockCopy(salt, 0, input, 0, salt.Length);
        Buffer.BlockCopy(key, 0, input, salt.Length, key.Length);
        return SHA256.HashData(input);
    }
}
