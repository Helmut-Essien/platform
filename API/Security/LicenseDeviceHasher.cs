using System.Security.Cryptography;
using System.Text;

namespace Platform.Api.Security;

public static class LicenseDeviceHasher
{
    /// <summary>Stable SHA-256 hex of a client device id. Independent of the license key, so key rotation keeps devices.</summary>
    public static string Hash(string deviceId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("license-device:v1:" + deviceId)));
}
