using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Xunit;

namespace API.Tests;

public class SortHelperTests
{
    [Theory]
    [InlineData("name", true)]
    [InlineData("NAME", true)]
    [InlineData(" created ", true)]
    [InlineData("InternalNotes", false)]
    [InlineData("Name; DROP TABLE", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CustomerWhitelist_OnlyAcceptsKnownKeys(string? key, bool expected) =>
        Assert.Equal(expected, SortHelper.TryGet(SortHelper.CustomerSorts, key, out _));

    [Fact]
    public void Whitelists_DoNotExposeSecretColumns()
    {
        Assert.DoesNotContain(SortHelper.LicenseSorts.Keys, k => k.Contains("hash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(SortHelper.CustomerSorts.Keys, k => k.Contains("notes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(SortHelper.InvoiceSorts.Keys, k => k.Contains("notes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Apply_UnknownKey_UsesDefaultOrder()
    {
        var data = new[] { new Customer { Name = "b", ContactEmail = "b@x" }, new Customer { Name = "a", ContactEmail = "a@x" } }
            .AsQueryable();

        var result = SortHelper.Apply(data, SortHelper.CustomerSorts, "bogus", false, q => q.OrderByDescending(c => c.Name)).ToList();

        Assert.Equal(["b", "a"], result.Select(c => c.Name));
    }

    [Fact]
    public async Task CustomerList_SortsByNameAscendingAndDescending()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString()).Options);
        db.Customers.AddRange(
            new Customer { Name = "Charlie", ContactEmail = "c@x.test" },
            new Customer { Name = "Alpha", ContactEmail = "a@x.test" },
            new Customer { Name = "Bravo", ContactEmail = "b@x.test" });
        await db.SaveChangesAsync();
        var service = new CustomerService(db, new AuditLogService(db), new NoopDenyList(), new EmailOutboxService(db), new EmailTemplateService());

        var asc = await service.ListAsync(sortBy: "name");
        var desc = await service.ListAsync(sortBy: "name", sortDesc: true);

        Assert.Equal(["Alpha", "Bravo", "Charlie"], asc.Items.Select(c => c.Name));
        Assert.Equal(["Charlie", "Bravo", "Alpha"], desc.Items.Select(c => c.Name));
    }

    // Every whitelisted key must translate to SQL on Npgsql (ToQueryString needs no live connection).
    [Fact]
    public void AllWhitelistedSorts_TranslateOnNpgsql()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused").Options);

        foreach (var key in SortHelper.CustomerSorts.Keys)
            Assert.Contains("ORDER BY", SortHelper.Apply(db.Customers, SortHelper.CustomerSorts, key, true, q => q.OrderBy(c => c.Id)).ThenBy(c => c.Id).ToQueryString());
        foreach (var key in SortHelper.LicenseSorts.Keys)
            Assert.Contains("ORDER BY", SortHelper.Apply(db.Licenses, SortHelper.LicenseSorts, key, false, q => q.OrderBy(c => c.Id)).ThenBy(c => c.Id).ToQueryString());
        foreach (var key in SortHelper.InvoiceSorts.Keys)
            Assert.Contains("ORDER BY", SortHelper.Apply(db.Invoices, SortHelper.InvoiceSorts, key, false, q => q.OrderBy(c => c.Id)).ThenBy(c => c.Id).ToQueryString());
    }

    private sealed class NoopDenyList : ILicenseDenyListService
    {
        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
