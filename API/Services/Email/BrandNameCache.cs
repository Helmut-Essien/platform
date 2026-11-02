using Platform.Api.Entities;

namespace Platform.Api.Services.Email;

/// <summary>
/// The brand profile's company name for email subjects and bodies, cached in memory (singleton). Refreshed whenever
/// the profile is read or saved (Settings, every outbox send) and once at startup; the built-in default until then.
/// </summary>
public sealed class BrandNameCache
{
    private volatile string _current = EmailBrandContact.Defaults.CompanyName;

    public string Current => _current;

    public void Set(InvoiceBrandProfile? profile) => _current = EmailBrandContact.From(profile).CompanyName;
}
