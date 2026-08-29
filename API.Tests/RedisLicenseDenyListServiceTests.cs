using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class RedisLicenseDenyListServiceTests
{
    [Fact]
    public async Task IsDeniedAsync_FallsBackToDatabaseWhenCacheUnavailable()
    {
        await using var db = CreateDb();
        var customer = new Customer { Name = "Co", ContactEmail = "co@test.com", IsSuspended = false };
        db.Customers.Add(customer);
        var license = new License
        {
            CustomerId = customer.Id,
            ServiceProductId = "svc",
            Status = LicenseStatus.Suspended,
            PlanName = "Basic",
            LicenseKeyHash = BCrypt.Net.BCrypt.HashPassword("KEY")
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        var service = new RedisLicenseDenyListService(
            new ThrowingDistributedCache(),
            new FakeScopeFactory(db),
            Options.Create(new RedisSettings()),
            NullLogger<RedisLicenseDenyListService>.Instance);

        Assert.True(await service.IsDeniedAsync(license.Id));
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("Redis down");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            throw new InvalidOperationException("Redis down");
        public void Refresh(string key) => throw new InvalidOperationException("Redis down");
        public Task RefreshAsync(string key, CancellationToken token = default) =>
            throw new InvalidOperationException("Redis down");
        public void Remove(string key) => throw new InvalidOperationException("Redis down");
        public Task RemoveAsync(string key, CancellationToken token = default) =>
            throw new InvalidOperationException("Redis down");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            throw new InvalidOperationException("Redis down");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
            throw new InvalidOperationException("Redis down");
    }

    private sealed class FakeScopeFactory(AppDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FakeScope(db);

        private sealed class FakeScope(AppDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new FakeProvider(db);
            public void Dispose() { }
        }

        private sealed class FakeProvider(AppDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(AppDbContext) ? db : null;
        }
    }
}
