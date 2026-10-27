using Platform.Api.Entities;

namespace Platform.Api.Services;

public interface ILicenseKeyDeliveryService
{
    /// <summary>Generates a new key, stores a salted SHA-256 verifier hash, emails plain key to customer. Never logs the plain key.</summary>
    Task DeliverNewKeyAsync(License license, bool isRenewal, CancellationToken cancellationToken = default);
}
