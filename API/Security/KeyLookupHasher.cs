using System.Security.Cryptography;
using System.Text;

namespace Platform.Api.Security;

public static class KeyLookupHasher
{
    public static string ComputeSha256Hex(string plainKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
