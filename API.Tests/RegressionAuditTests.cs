using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
using Platform.Api.Services;
using Platform.Api.Services.Billing;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

/// <summary>Regression-audit fixes: numbering, subjects, templates, logos, search bounds, counts.</summary>
public class RegressionAuditTests
{
    [Theory]
    [InlineData(null, "INV-2026-00001")]
    [InlineData("INV-2026-00041", "INV-2026-00042")]
    [InlineData("INV-2026-99999", "INV-2026-100000")]
    [InlineData("INV-2026--5", "INV-2026-00001")]        // negative suffix is ignored
    [InlineData("INV-2026- 7", "INV-2026-00001")]        // whitespace is not a number
    [InlineData("INV-2026-ABCDE", "INV-2026-00001")]     // non-numeric
    [InlineData("INV-2026-", "INV-2026-00001")]
    [InlineData("INV-2026-9999999999999999", "INV-2026-00001")] // too long: no overflow
    [InlineData("INV-2025-00007", "INV-2026-00001")]     // other year prefix
    public void NextDocumentNumber_HandlesMalformedAndBoundaryInput(string? last, string expected) =>
        Assert.Equal(expected, BillingService.NextDocumentNumber("INV-2026-", last));

    [Fact]
    public void NextDocumentNumber_SkipMovesPastUnseenCollisions() =>
        Assert.Equal("INV-2026-00003", BillingService.NextDocumentNumber("INV-2026-", null, skip: 2));

    [Fact]
    public void SanitizeSubject_FlattensControlCharsAndCapsLength()
    {
        Assert.Equal("Your A B license", EmailOutboxService.SanitizeSubject("Your A\r\nB   license"));
        Assert.Equal("(no subject)", EmailOutboxService.SanitizeSubject("\r\n"));
        var capped = EmailOutboxService.SanitizeSubject(new string('x', 900));
        Assert.Equal(500, capped.Length);
    }

    [Fact]
    public void LicenseKeyTemplate_EncodesProductNameInPreheader()
    {
        var customer = new Customer { Name = "Acme", ContactEmail = "a@acme.test" };
        var product = new ServiceProduct { Name = "A <b>&</b> B", Code = "AB" };
        var license = new License { CustomerId = customer.Id, ServiceProductId = product.Id, PlanName = "Pro" };

        var (_, html) = new EmailTemplateService().LicenseKey(customer, product, license, "KEY-1", rotated: false);

        Assert.DoesNotContain("A <b>&</b> B", html);
        Assert.Contains("A &lt;b&gt;&amp;&lt;/b&gt; B", html);
    }

    [Fact]
    public void SearchHelper_Normalize_TrimsAndCaps()
    {
        Assert.Equal("abc", SearchHelper.Normalize("  abc  "));
        Assert.Equal(SearchHelper.MaxSearchLength, SearchHelper.Normalize(new string('a', 1000)).Length);
    }

    [Fact]
    public void LogoSignatureCheck_RejectsMislabeledOrCorruptFiles()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        Assert.True(InvoiceBrandService.LooksLikeImage(png, "image/png"));
        Assert.False(InvoiceBrandService.LooksLikeImage(png, "image/jpeg"));
        Assert.False(InvoiceBrandService.LooksLikeImage("not an image at all"u8.ToArray(), "image/png"));
        Assert.False(InvoiceBrandService.LooksLikeImage(png, "image/gif"));
    }

    [Fact]
    public async Task UpdateBrand_RejectsCorruptLogo()
    {
        await using var db = NewDb();
        var service = new InvoiceBrandService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(new Platform.Shared.Dtos.Settings.UpdateInvoiceBrandRequest
        {
            CompanyName = "X",
            LogoBase64 = Convert.ToBase64String("garbage-bytes-garbage"u8.ToArray()),
            LogoContentType = "image/png"
        }));
    }

    [Fact]
    public void Pdf_FallsBackToNoLogoWhenLogoCannotBeDecoded()
    {
        var customer = new Customer { Name = "Acme", ContactEmail = "a@acme.test" };
        var invoice = new Invoice
        {
            CustomerId = customer.Id,
            InvoiceNumber = "INV-2026-00001",
            Status = InvoiceStatus.Sent,
            IssueDate = DateTime.UtcNow,
            Currency = "USD",
            Subtotal = 10m,
            TotalAmount = 10m
        };
        var letterhead = new InvoiceLetterhead("Co", null, null, null, null, "definitely not an image"u8.ToArray(), []);

        var pdf = new InvoicePdfGenerator().Generate(invoice, customer, letterhead);

        Assert.True(pdf.Length > 100);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public async Task ServiceProductList_CountsLicensesOfSuspendedCustomers()
    {
        await using var db = NewDb();
        var product = new ServiceProduct { Name = "P", Code = "P1" };
        var customer = new Customer { Name = "Susp", ContactEmail = "s@x.test", IsSuspended = true };
        db.AddRange(product, customer);
        db.Licenses.Add(new License
        {
            CustomerId = customer.Id,
            ServiceProductId = product.Id,
            PlanName = "Pro",
            Status = LicenseStatus.Suspended
        });
        await db.SaveChangesAsync();

        var service = new ServiceProductService(db, new AuditLogService(db));
        Assert.Equal(1, (await service.ListAsync()).Single().LicenseCount);
        Assert.Equal(1, (await service.GetAsync(product.Id))!.LicenseCount);
    }

    [Fact]
    public async Task IntegrationKeyList_IsBounded()
    {
        await using var db = NewDb();
        var product = new ServiceProduct { Name = "P", Code = "P1" };
        db.ServiceProducts.Add(product);
        for (var i = 0; i < 520; i++)
        {
            db.IntegrationKeys.Add(new IntegrationKey
            {
                ServiceProductId = product.Id,
                KeyHash = "x",
                KeyLookupHash = $"h{i}",
                IsActive = false,
                CreatedAt = DateTime.UtcNow.AddMinutes(-i)
            });
        }

        await db.SaveChangesAsync();
        var keys = await new IntegrationKeyService(db, new AuditLogService(db)).ListAsync();
        Assert.Equal(500, keys.Count);
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
        .Options);
}
