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
}
