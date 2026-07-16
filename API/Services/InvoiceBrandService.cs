using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Shared.Dtos.Settings;

namespace Platform.Api.Services;

public class InvoiceBrandService(AppDbContext db) : IInvoiceBrandService
{
    public const int MaxLogoBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> AllowedLogoContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/jpg",
        "image/webp"
    };

    public async Task<InvoiceBrandDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var profile = await GetOrCreateProfileAsync(cancellationToken);
        return Map(profile);
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetLogoAsync(CancellationToken cancellationToken = default)
    {
        var profile = await GetOrCreateProfileAsync(cancellationToken);
        if (profile.LogoBytes is not { Length: > 0 })
            return null;

        var contentType = string.IsNullOrWhiteSpace(profile.LogoContentType)
            ? "image/png"
            : profile.LogoContentType!;

        return (profile.LogoBytes, contentType);
    }

    public async Task<InvoiceBrandDto> UpdateAsync(
        UpdateInvoiceBrandRequest request,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetOrCreateProfileAsync(cancellationToken);

        profile.CompanyName = request.CompanyName.Trim();
        profile.AddressLine1 = NormalizeOptional(request.AddressLine1);
        profile.AddressLine2 = NormalizeOptional(request.AddressLine2);
        profile.Phone = NormalizeOptional(request.Phone);
        profile.Website = NormalizeOptional(request.Website);
        profile.UpdatedAt = DateTime.UtcNow;

        if (request.ClearLogo)
        {
            profile.LogoBytes = null;
            profile.LogoContentType = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.LogoBase64))
        {
            ApplyLogo(profile, request.LogoBase64!, request.LogoContentType);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(profile);
    }

    public Task<InvoiceBrandProfile> GetProfileEntityAsync(CancellationToken cancellationToken = default) =>
        GetOrCreateProfileAsync(cancellationToken);

    private async Task<InvoiceBrandProfile> GetOrCreateProfileAsync(CancellationToken cancellationToken)
    {
        var existing = await db.InvoiceBrandProfiles
            .OrderBy(p => p.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
            return existing;

        var profile = new InvoiceBrandProfile
        {
            CompanyName = "Platform License Hub",
            UpdatedAt = DateTime.UtcNow
        };

        db.InvoiceBrandProfiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);
        return profile;
    }

    private static void ApplyLogo(InvoiceBrandProfile profile, string logoBase64, string? contentType)
    {
        var normalizedType = string.IsNullOrWhiteSpace(contentType)
            ? "image/png"
            : contentType.Trim().ToLowerInvariant();

        if (normalizedType == "image/jpg")
            normalizedType = "image/jpeg";

        if (!AllowedLogoContentTypes.Contains(normalizedType))
            throw new InvalidOperationException("Logo must be a PNG, JPEG, or WebP image.");

        byte[] bytes;
        try
        {
            var payload = logoBase64.Contains(',')
                ? logoBase64[(logoBase64.IndexOf(',') + 1)..]
                : logoBase64;
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("LogoBase64 is not valid base64.", ex);
        }

        if (bytes.Length == 0)
            throw new InvalidOperationException("Logo file is empty.");

        if (bytes.Length > MaxLogoBytes)
            throw new InvalidOperationException("Logo must be 2 MB or smaller.");

        profile.LogoBytes = bytes;
        profile.LogoContentType = normalizedType;
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim();
    }

    private static InvoiceBrandDto Map(InvoiceBrandProfile profile) => new()
    {
        Id = profile.Id,
        CompanyName = profile.CompanyName,
        AddressLine1 = profile.AddressLine1,
        AddressLine2 = profile.AddressLine2,
        Phone = profile.Phone,
        Website = profile.Website,
        HasCustomLogo = profile.LogoBytes is { Length: > 0 },
        LogoContentType = profile.LogoContentType,
        UpdatedAt = profile.UpdatedAt
    };
}
