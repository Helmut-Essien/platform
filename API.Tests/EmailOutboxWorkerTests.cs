using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Billing;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.Settings;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class EmailOutboxWorkerTests
{
    [Fact]
    public void ComputeBackoff_DoublesAndIsCapped()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), EmailOutboxWorker.ComputeBackoff(1));
        Assert.Equal(TimeSpan.FromMinutes(8), EmailOutboxWorker.ComputeBackoff(3));
        Assert.Equal(TimeSpan.FromMinutes(360), EmailOutboxWorker.ComputeBackoff(12));
        Assert.Equal(TimeSpan.FromMinutes(360), EmailOutboxWorker.ComputeBackoff(500));
    }

    [Fact]
    public async Task SendAsync_Success_MarksSentAndClearsPayload()
    {
        await using var h = CreateHarness();
        var message = await h.QueueAsync(EmailDeliveryKind.LicenseKey, payload: true);

        await h.Worker.SendAsync(message.Id, CancellationToken.None);

        var stored = await h.Db.EmailOutboxMessages.AsNoTracking().FirstAsync(m => m.Id == message.Id);
        Assert.Equal(EmailDeliveryStatus.Sent, stored.Status);
        Assert.Null(stored.EncryptedPayload);
        Assert.DoesNotContain("{{LICENSE_KEY}}", stored.HtmlBody);
        Assert.Single(h.Sender.Sent);
        Assert.Contains("HOSTEL-AAAA-BBBB", h.Sender.Sent[0].Body);
    }

    [Fact]
    public async Task SendAsync_DeadLetter_KeepsPayloadSoLicenseKeyEmailCanBeRetried()
    {
        // B3 + E3: a provider outage must not destroy the key payload; retry works and resets attempts.
        await using var h = CreateHarness(maxAttempts: 1);
        h.Sender.Fail = true;
        var message = await h.QueueAsync(EmailDeliveryKind.LicenseKey, payload: true);

        await h.Worker.SendAsync(message.Id, CancellationToken.None);

        h.Db.ChangeTracker.Clear();
        var dead = await h.Db.EmailOutboxMessages.FirstAsync(m => m.Id == message.Id);
        Assert.Equal(EmailDeliveryStatus.DeadLetter, dead.Status);
        Assert.NotNull(dead.EncryptedPayload);
        Assert.Contains("{{LICENSE_KEY}}", dead.HtmlBody);

        var retried = await new EmailOutboxService(h.Db).RetryAsync(message.Id);
        Assert.Equal(EmailDeliveryStatus.Pending, retried.Status);
        Assert.Equal(0, retried.AttemptCount);

        // Simulate the worker claiming it again, then the provider recovering.
        var tracked = await h.Db.EmailOutboxMessages.FirstAsync(m => m.Id == message.Id);
        tracked.Status = EmailDeliveryStatus.Sending;
        await h.Db.SaveChangesAsync();
        h.Sender.Fail = false;
        await h.Worker.SendAsync(message.Id, CancellationToken.None);

        h.Db.ChangeTracker.Clear();
        Assert.Equal(EmailDeliveryStatus.Sent, (await h.Db.EmailOutboxMessages.FirstAsync(m => m.Id == message.Id)).Status);
        Assert.Contains("HOSTEL-AAAA-BBBB", h.Sender.Sent[0].Body);
    }

    [Fact]
    public async Task SendAsync_Failure_SchedulesBackoffAndKeepsMessageRetryable()
    {
        await using var h = CreateHarness(maxAttempts: 5);
        h.Sender.Fail = true;
        var message = await h.QueueAsync(EmailDeliveryKind.RenewalConfirmation, payload: false);

        await h.Worker.SendAsync(message.Id, CancellationToken.None);

        h.Db.ChangeTracker.Clear();
        var stored = await h.Db.EmailOutboxMessages.FirstAsync(m => m.Id == message.Id);
        Assert.Equal(EmailDeliveryStatus.Failed, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
        Assert.True(stored.NextAttemptAt > DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task SendAsync_VoidedInvoice_IsCancelledAndNotSent()
    {
        // E4
        await using var h = CreateHarness();
        var customer = new Customer { Name = "Acme", ContactEmail = "a@acme.test" };
        var invoice = new Invoice
        {
            Customer = customer,
            CustomerId = customer.Id,
            InvoiceNumber = "INV-2026-00099",
            Status = InvoiceStatus.Void,
            Currency = "USD",
            Subtotal = 10m,
            TotalAmount = 10m
        };
        h.Db.AddRange(customer, invoice);
        await h.Db.SaveChangesAsync();
        var message = await h.QueueAsync(EmailDeliveryKind.Invoice, payload: false, invoiceId: invoice.Id, customerId: customer.Id);

        await h.Worker.SendAsync(message.Id, CancellationToken.None);

        Assert.Empty(h.Sender.Sent);
        h.Db.ChangeTracker.Clear();
        var stored = await h.Db.EmailOutboxMessages.FirstAsync(m => m.Id == message.Id);
        Assert.Equal(EmailDeliveryStatus.Cancelled, stored.Status);
        Assert.Contains("voided", stored.LastError);
    }

    [Fact]
    public async Task SendAsync_IgnoresMessagesThatAreNotClaimed()
    {
        await using var h = CreateHarness();
        var message = await h.QueueAsync(EmailDeliveryKind.RenewalConfirmation, payload: false, status: EmailDeliveryStatus.Pending);

        await h.Worker.SendAsync(message.Id, CancellationToken.None);

        Assert.Empty(h.Sender.Sent);
    }

    [Fact]
    public async Task PurgeAsync_RedactsOldDeadLettersAndDeletesOldSentRows()
    {
        await using var h = CreateHarness();
        var oldDead = await h.QueueAsync(EmailDeliveryKind.LicenseKey, payload: true, status: EmailDeliveryStatus.DeadLetter);
        var freshDead = await h.QueueAsync(EmailDeliveryKind.LicenseKey, payload: true, status: EmailDeliveryStatus.DeadLetter);
        var oldSent = await h.QueueAsync(EmailDeliveryKind.RenewalConfirmation, payload: false, status: EmailDeliveryStatus.Sent);
        var freshSent = await h.QueueAsync(EmailDeliveryKind.RenewalConfirmation, payload: false, status: EmailDeliveryStatus.Sent);
        (await h.Db.EmailOutboxMessages.FirstAsync(m => m.Id == oldDead.Id)).UpdatedAt = DateTime.UtcNow.AddDays(-30);
        (await h.Db.EmailOutboxMessages.FirstAsync(m => m.Id == oldSent.Id)).UpdatedAt = DateTime.UtcNow.AddDays(-200);
        await h.Db.SaveChangesAsync();

        await h.Worker.PurgeAsync(CancellationToken.None);

        h.Db.ChangeTracker.Clear();
        var oldDeadStored = await h.Db.EmailOutboxMessages.FirstAsync(m => m.Id == oldDead.Id);
        Assert.Null(oldDeadStored.EncryptedPayload);
        Assert.Contains("[redacted]", oldDeadStored.HtmlBody);
        Assert.NotNull((await h.Db.EmailOutboxMessages.FirstAsync(m => m.Id == freshDead.Id)).EncryptedPayload);
        Assert.False(await h.Db.EmailOutboxMessages.AnyAsync(m => m.Id == oldSent.Id));
        Assert.True(await h.Db.EmailOutboxMessages.AnyAsync(m => m.Id == freshSent.Id));
    }

    private static Harness CreateHarness(int maxAttempts = 5)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;
        var db = new AppDbContext(options);
        var settings = Options.Create(new EmailSettings
        {
            Outbox = new EmailOutboxSettings
            {
                MaxAttempts = maxAttempts,
                EncryptionKey = Convert.ToBase64String(new byte[32])
            }
        });
        var protector = new EmailPayloadProtector(
            settings,
            new ConfigurationBuilder().Build(),
            new FakeEnv());
        var sender = new RecordingSender();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IEmailSender>(sender);
        services.AddSingleton(protector);
        services.AddSingleton<IInvoicePdfGenerator, StubPdf>();
        services.AddSingleton<IInvoiceBrandService, StubBrand>();
        var provider = services.BuildServiceProvider();

        var worker = new EmailOutboxWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            settings,
            NullLogger<EmailOutboxWorker>.Instance);
        return new Harness(db, worker, sender, protector, provider);
    }

    private sealed class Harness(
        AppDbContext db,
        EmailOutboxWorker worker,
        RecordingSender sender,
        EmailPayloadProtector protector,
        ServiceProvider provider) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public EmailOutboxWorker Worker { get; } = worker;
        public RecordingSender Sender { get; } = sender;

        public async Task<EmailOutboxMessage> QueueAsync(
            EmailDeliveryKind kind,
            bool payload,
            EmailDeliveryStatus status = EmailDeliveryStatus.Sending,
            string? invoiceId = null,
            string? customerId = null)
        {
            var message = new EmailOutboxMessage
            {
                Kind = kind,
                Status = status,
                ToEmail = "owner@acme.test",
                Subject = "Subject",
                HtmlBody = payload ? "<p>Your key: {{LICENSE_KEY}}</p>" : "<p>Hello</p>",
                EncryptedPayload = payload ? protector.Protect("HOSTEL-AAAA-BBBB") : null,
                InvoiceId = invoiceId,
                CustomerId = customerId
            };
            Db.EmailOutboxMessages.Add(message);
            await Db.SaveChangesAsync();
            return message;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class RecordingSender : IEmailSender
    {
        public bool Fail { get; set; }
        public List<(string To, string Subject, string Body)> Sent { get; } = [];

        public Task SendAsync(
            string toEmail,
            string subject,
            string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null,
            CancellationToken cancellationToken = default)
        {
            if (Fail)
                throw new InvalidOperationException("provider down");
            Sent.Add((toEmail, subject, htmlBody));
            return Task.CompletedTask;
        }
    }

    private sealed class StubPdf : IInvoicePdfGenerator
    {
        public byte[] Generate(Invoice invoice, Customer customer, InvoiceLetterhead letterhead) => [1, 2, 3];
    }

    private sealed class StubBrand : IInvoiceBrandService
    {
        public Task<InvoiceBrandDto> GetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(byte[] Bytes, string ContentType)?> GetLogoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InvoiceBrandDto> UpdateAsync(UpdateInvoiceBrandRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InvoiceBrandProfile> GetProfileEntityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new InvoiceBrandProfile { CompanyName = "Test Co" });
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "API.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
