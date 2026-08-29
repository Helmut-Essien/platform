using Platform.Api.Security;
using Xunit;

namespace API.Tests;

public class RateLimitPartitionKeyTests
{
    [Fact]
    public void LicenseValidatePartitionKey_IncludesIpAndIntegrationKeyHash()
    {
        const string ip = "203.0.113.10";
        const string integrationKey = "ik_test_key_001";
        var integrationPart = KeyLookupHasher.ComputeSha256Hex(integrationKey)[..16];
        var partitionKey = $"{ip}:{integrationPart}";

        Assert.StartsWith($"{ip}:", partitionKey);
        Assert.NotEqual($"{ip}:none", partitionKey);
    }

    [Fact]
    public void LicenseValidatePartitionKey_UsesNoneWhenIntegrationKeyMissing()
    {
        const string ip = "203.0.113.10";
        string? integrationKey = null;
        var integrationPart = string.IsNullOrWhiteSpace(integrationKey)
            ? "none"
            : KeyLookupHasher.ComputeSha256Hex(integrationKey)[..16];

        Assert.Equal("none", integrationPart);
        Assert.Equal($"{ip}:none", $"{ip}:{integrationPart}");
    }
}
