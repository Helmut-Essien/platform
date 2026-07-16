using Platform.Api.Entities;
using Platform.Shared.Dtos.Settings;

namespace Platform.Api.Services;

public interface IInvoiceBrandService
{
    Task<InvoiceBrandDto> GetAsync(CancellationToken cancellationToken = default);

    Task<(byte[] Bytes, string ContentType)?> GetLogoAsync(CancellationToken cancellationToken = default);

    Task<InvoiceBrandDto> UpdateAsync(UpdateInvoiceBrandRequest request, CancellationToken cancellationToken = default);

    /// <summary>Loads the singleton profile for PDF generation (creates default if missing).</summary>
    Task<InvoiceBrandProfile> GetProfileEntityAsync(CancellationToken cancellationToken = default);
}
