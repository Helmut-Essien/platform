using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Services;
using Platform.Shared.Dtos.Settings;
using Xunit;

namespace API.Tests;

public class InvoiceBrandServiceTests
{
    [Fact]
    public async Task GetAsync_CreatesDefaultProfileWhenMissing()
    {
        await using var db = CreateDbContext();
        var service = new InvoiceBrandService(db);

        var dto = await service.GetAsync();

        Assert.Equal("Platform License Hub", dto.CompanyName);
        Assert.False(dto.HasCustomLogo);
        Assert.Single(db.InvoiceBrandProfiles);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesTextFields()
    {
        await using var db = CreateDbContext();
        var service = new InvoiceBrandService(db);

        var updated = await service.UpdateAsync(new UpdateInvoiceBrandRequest
        {
            CompanyName = "HelmutCode",
            AddressLine1 = "Accra",
            AddressLine2 = "Ghana",
            Phone = "+233 00 000 0000",
            Website = "https://helmutcode.com"
        });

        Assert.Equal("HelmutCode", updated.CompanyName);
        Assert.Equal("Accra", updated.AddressLine1);
        Assert.Equal("Ghana", updated.AddressLine2);
        Assert.Equal("+233 00 000 0000", updated.Phone);
        Assert.Equal("https://helmutcode.com", updated.Website);
        Assert.False(updated.HasCustomLogo);
    }

    [Fact]
    public async Task UpdateAsync_SetsAndClearsLogo()
    {
        await using var db = CreateDbContext();
        var service = new InvoiceBrandService(db);
        var pngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        var withLogo = await service.UpdateAsync(new UpdateInvoiceBrandRequest
        {
            CompanyName = "HelmutCode",
            LogoBase64 = Convert.ToBase64String(pngBytes),
            LogoContentType = "image/png"
        });

        Assert.True(withLogo.HasCustomLogo);
        var logo = await service.GetLogoAsync();
        Assert.NotNull(logo);
        Assert.Equal("image/png", logo!.Value.ContentType);
        Assert.Equal(pngBytes, logo.Value.Bytes);

        var cleared = await service.UpdateAsync(new UpdateInvoiceBrandRequest
        {
            CompanyName = "HelmutCode",
            ClearLogo = true
        });

        Assert.False(cleared.HasCustomLogo);
        Assert.Null(await service.GetLogoAsync());
    }

    [Fact]
    public async Task UpdateAsync_RejectsInvalidContentType()
    {
        await using var db = CreateDbContext();
        var service = new InvoiceBrandService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(new UpdateInvoiceBrandRequest
            {
                CompanyName = "HelmutCode",
                LogoBase64 = Convert.ToBase64String([1, 2, 3]),
                LogoContentType = "application/pdf"
            }));
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;

        return new AppDbContext(options);
    }
}
