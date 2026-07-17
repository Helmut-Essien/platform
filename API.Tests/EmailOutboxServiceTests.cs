using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Services.Email;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class EmailOutboxServiceTests
{
    [Fact]
    public async Task RetryAsync_RequeuesFailedDelivery()
    {
        await using var db = CreateDb();
        var service = new EmailOutboxService(db);
        var message = service.Enqueue(
            EmailDeliveryKind.Welcome,
            "owner@example.test",
            "Welcome",
            "<p>Welcome</p>");
        message.Status = EmailDeliveryStatus.Failed;
        message.AttemptCount = 2;
        message.LastError = "Provider unavailable";
        await db.SaveChangesAsync();

        var result = await service.RetryAsync(message.Id);

        Assert.Equal(EmailDeliveryStatus.Pending, result.Status);
        Assert.Null(result.LastError);
        Assert.NotNull(result.NextAttemptAt);
    }

    [Fact]
    public async Task ListAsync_FiltersByRelatedEntity()
    {
        await using var db = CreateDb();
        var service = new EmailOutboxService(db);
        service.Enqueue(EmailDeliveryKind.Invoice, "a@test", "A", "A", customerId: "customer-a");
        service.Enqueue(EmailDeliveryKind.Invoice, "b@test", "B", "B", customerId: "customer-b");
        await db.SaveChangesAsync();

        var result = await service.ListAsync(customerId: "customer-a");

        var delivery = Assert.Single(result);
        Assert.Equal("a@test", delivery.ToEmail);
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options);
}
