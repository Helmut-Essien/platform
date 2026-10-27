using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services.Email;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class LicenseExpiryReminderWorkerTests
{
    [Fact]
    public async Task Scan_QueuesOneReminderAndDeduplicatesSubsequentScan()
    {
        var services = new ServiceCollection();
        var database = NUlid.Ulid.NewUlid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddScoped<IEmailOutboxService, EmailOutboxService>();
        services.AddSingleton<EmailTemplateService>();
        await using var provider = services.BuildServiceProvider();

        await using (var seedScope = provider.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = new Customer { Name = "Acme", ContactEmail = "ops@acme.test" };
            var product = new ServiceProduct { Name = "Hostel", Code = "HOSTEL" };
            db.Licenses.Add(new License
            {
                Customer = customer,
                CustomerId = customer.Id,
                ServiceProduct = product,
                ServiceProductId = product.Id,
                PlanName = "Pro",
                Status = LicenseStatus.Active,
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            });
            await db.SaveChangesAsync();
        }

        var worker = new LicenseExpiryReminderWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new LifecycleSettings { ExpiryReminderDays = 30 }),
            NullLogger<LicenseExpiryReminderWorker>.Instance);

        await worker.EnqueueRemindersAsync(CancellationToken.None);
        await worker.EnqueueRemindersAsync(CancellationToken.None);

        await using var assertScope = provider.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var message = Assert.Single(await assertDb.EmailOutboxMessages.ToListAsync());
        Assert.Equal(EmailDeliveryKind.ExpiryReminder, message.Kind);
        Assert.Equal("ops@acme.test", message.ToEmail);
    }

    [Fact]
    public async Task Scan_SendsSteppedRemindersOncePerStep()
    {
        // E5: 30 -> 7 -> 1 day reminders, each at most once.
        await using var provider = BuildProvider();
        string licenseId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = new Customer { Name = "Acme", ContactEmail = "ops@acme.test" };
            var product = new ServiceProduct { Name = "Hostel", Code = "HOSTEL" };
            var license = new License
            {
                Customer = customer, CustomerId = customer.Id,
                ServiceProduct = product, ServiceProductId = product.Id,
                PlanName = "Pro", Status = LicenseStatus.Active,
                ExpiresAt = DateTime.UtcNow.AddDays(25)
            };
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            licenseId = license.Id;
        }

        var worker = CreateWorker(provider);
        await worker.EnqueueRemindersAsync(CancellationToken.None);
        await worker.EnqueueRemindersAsync(CancellationToken.None);
        Assert.Equal(1, await CountRemindersAsync(provider));

        // Time passes: 18 days later the license is 7 days out (existing reminder is that old).
        await ShiftAsync(provider, licenseId, daysUntilExpiry: 6.5, reminderAgeDays: 18);
        await worker.EnqueueRemindersAsync(CancellationToken.None);
        await worker.EnqueueRemindersAsync(CancellationToken.None);
        Assert.Equal(2, await CountRemindersAsync(provider));

        // 5.5 days later: inside the 1-day window.
        await ShiftAsync(provider, licenseId, daysUntilExpiry: 0.9, reminderAgeDays: 5.6);
        await worker.EnqueueRemindersAsync(CancellationToken.None);
        await worker.EnqueueRemindersAsync(CancellationToken.None);
        Assert.Equal(3, await CountRemindersAsync(provider));
    }

    [Fact]
    public async Task Scan_LicenseBeyondTheHorizonGetsNoReminder()
    {
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = new Customer { Name = "Acme", ContactEmail = "ops@acme.test" };
            var product = new ServiceProduct { Name = "Hostel", Code = "HOSTEL" };
            db.Licenses.Add(new License
            {
                Customer = customer, CustomerId = customer.Id,
                ServiceProduct = product, ServiceProductId = product.Id,
                PlanName = "Pro", Status = LicenseStatus.Active,
                ExpiresAt = DateTime.UtcNow.AddDays(90)
            });
            await db.SaveChangesAsync();
        }

        await CreateWorker(provider).EnqueueRemindersAsync(CancellationToken.None);
        Assert.Equal(0, await CountRemindersAsync(provider));
    }

    [Fact]
    public void ReminderSteps_AreDistinctDescendingAndBoundedByTheHorizon()
    {
        var steps = LicenseExpiryReminderWorker.ReminderSteps(new LifecycleSettings
        {
            ExpiryReminderDays = 14,
            ExpiryReminderAdditionalSteps = [30, 7, 7, 1, 0]
        });
        Assert.Equal([14, 7, 1], steps);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var database = NUlid.Ulid.NewUlid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddScoped<IEmailOutboxService, EmailOutboxService>();
        services.AddSingleton<EmailTemplateService>();
        return services.BuildServiceProvider();
    }

    private static LicenseExpiryReminderWorker CreateWorker(ServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new LifecycleSettings { ExpiryReminderDays = 30 }),
            NullLogger<LicenseExpiryReminderWorker>.Instance);

    private static async Task<int> CountRemindersAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .EmailOutboxMessages.CountAsync(x => x.Kind == EmailDeliveryKind.ExpiryReminder);
    }

    private static async Task ShiftAsync(ServiceProvider provider, string licenseId, double daysUntilExpiry, double reminderAgeDays)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var license = await db.Licenses.IgnoreQueryFilters().FirstAsync(x => x.Id == licenseId);
        license.ExpiresAt = DateTime.UtcNow.AddDays(daysUntilExpiry);
        foreach (var m in await db.EmailOutboxMessages.ToListAsync())
            m.CreatedAt = m.CreatedAt.AddDays(-reminderAgeDays);
        await db.SaveChangesAsync();
    }
}
