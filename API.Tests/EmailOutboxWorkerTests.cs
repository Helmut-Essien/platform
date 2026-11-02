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
    public async Task SendAsync_ReclaimedPoisonMessage_IsDeadLetteredWithoutSending()
    {
        // A message whose attempts crashed the worker is reclaimed with its attempts already counted.
        await using var h = CreateHarness(maxAttempts: 3);
        var message = await h.QueueAsync(EmailDeliveryKind.RenewalConfirmation, payload: false);
        message.AttemptCount = 3;
        await h.Db.SaveChangesAsync();

        await h.Worker.SendAsync(message.Id, CancellationToken.None);

        h.Db.ChangeTracker.Clear();
        Assert.Equal(EmailDeliveryStatus.DeadLetter, (await h.Db.EmailOutboxMessages.FirstAsync(m => m.Id == message.Id)).Status);
        Assert.Empty(h.Sender.Sent);
    }

    [Fact]
    public async Task SendAsync_CountsAttemptBeforeSending()
    {
        await using var h = CreateHarness();
        h.Sender.Throw = new OperationCanceledException(); // e.g. shutdown mid-send: nothing else is recorded
        var message = await h.QueueAsync(EmailDeliveryKind.RenewalConfirmation, payload: false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Worker.SendAsync(message.Id, CancellationToken.None));

        h.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await h.Db.EmailOutboxMessages.FirstAsync(m => m.Id == message.Id)).AttemptCount);
    }

    [Fact]
    public async Task Postgres_ClaimThenSend_SendsOnceAndCountsOneAttempt()
    {
        await using var pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();
        if (pg is null)
            return;

        await using var h = CreateHarness(database: pg.NewContext());
        var message = await h.QueueAsync(EmailDeliveryKind.RenewalConfirmation, payload: false, status: EmailDeliveryStatus.Pending);

        var claimed = await h.Worker.ClaimBatchAsync(CancellationToken.None);
        Assert.Contains(message.Id, claimed);
        await h.Worker.SendAsync(message.Id, CancellationToken.None);
        await h.Worker.SendAsync(message.Id, CancellationToken.None); // a duplicate in-batch call is a no-op

        Assert.Single(h.Sender.Sent);
        await using var check = pg.NewContext();
        var stored = await check.EmailOutboxMessages.FirstAsync(m => m.Id == message.Id);
        Assert.Equal(EmailDeliveryStatus.Sent, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
    }

    [Fact]
    public async Task Postgres_SendAsync_SkipsMessageReclaimedByAnotherWorker()
    {
        await using var pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();
        if (pg is null)
            return;

        await using var h = CreateHarness(database: pg.NewContext());
        var message = await h.QueueAsync(EmailDeliveryKind.RenewalConfirmation, payload: false);

        // Another worker reclaims the row as stale while this worker still holds it in its batch.
        await using (var other = pg.NewContext())
            await other.EmailOutboxMessages.Where(m => m.Id == message.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(m => m.UpdatedAt, DateTime.UtcNow.AddMinutes(1)));

        await h.Worker.SendAsync(message.Id, CancellationToken.None);

        Assert.Empty(h.Sender.Sent);
        await using var check = pg.NewContext();
        Assert.Equal(0, (await check.EmailOutboxMessages.FirstAsync(m => m.Id == message.Id)).AttemptCount);
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
    public async Task SendAsync_PermanentFailure_IsDeadLetteredWithoutRetries()
    {
        await using var h = CreateHarness(maxAttempts: 10);
        h.Sender.FailPermanently = true;
        var message = await h.QueueAsync(EmailDeliveryKind.RenewalConfirmation, payload: false);

        await h.Worker.SendAsync(message.Id, CancellationToken.None);

        h.Db.ChangeTracker.Clear();
        var stored = await h.Db.EmailOutboxMessages.FirstAsync(m => m.Id == message.Id);
        Assert.Equal(EmailDeliveryStatus.DeadLetter, stored.Status);
        Assert.Null(stored.NextAttemptAt);
        Assert.Equal(1, stored.AttemptCount);
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
    public async Task SendAsync_PaymentReceipt_AttachesReceiptPdf()
    {
        await using var h = CreateHarness();
        var customer = new Customer { Name = "Acme", ContactEmail = "a@acme.test" };
        var invoice = new Invoice
        {
            Customer = customer,
            CustomerId = customer.Id,
            InvoiceNumber = "INV-2026-00100",
            Status = InvoiceStatus.Paid,
            Currency = "GHS",
            Subtotal = 10m,
            TotalAmount = 10m
        };
        var receipt = new Receipt
        {
            InvoiceId = invoice.Id,
            ReceiptNumber = "RCP-2026-00100",
            AmountPaid = 10m,
            Status = ReceiptStatus.Posted,
            PaidAt = DateTime.UtcNow
        };
        h.Db.AddRange(customer, invoice, receipt);
        await h.Db.SaveChangesAsync();
        var message = await h.QueueAsync(EmailDeliveryKind.PaymentReceipt, payload: false, invoiceId: invoice.Id, customerId: customer.Id);
        message.ReceiptId = receipt.Id;
        await h.Db.SaveChangesAsync();

        await h.Worker.SendAsync(message.Id, CancellationToken.None);

        Assert.Single(h.Sender.Sent);
        var attachment = Assert.Single(h.Sender.Attachments[0]!);
        Assert.Equal("RCP-2026-00100.pdf", attachment.FileName);
        Assert.Equal(new byte[] { 4, 5, 6 }, attachment.Content);
    }

    [Fact]
    public async Task SendAsync_ReceiptReversedBeforeSending_IsCancelledAndNotSent()
    {
        await using var h = CreateHarness();
        var customer = new Customer { Name = "Acme", ContactEmail = "a@acme.test" };
        var invoice = new Invoice
        {
            Customer = customer,
            CustomerId = customer.Id,
            InvoiceNumber = "INV-2026-00101",
            Status = InvoiceStatus.Sent,
            Currency = "GHS",
            Subtotal = 10m,
            TotalAmount = 10m
        };
        var receipt = new Receipt
        {
            InvoiceId = invoice.Id,
            ReceiptNumber = "RCP-2026-00101",
            AmountPaid = 10m,
            Status = ReceiptStatus.Reversed,
            PaidAt = DateTime.UtcNow
        };
        h.Db.AddRange(customer, invoice, receipt);
        await h.Db.SaveChangesAsync();
        var message = await h.QueueAsync(EmailDeliveryKind.PaymentReceipt, payload: false, invoiceId: invoice.Id, customerId: customer.Id);
        message.ReceiptId = receipt.Id;
        await h.Db.SaveChangesAsync();

        await h.Worker.SendAsync(message.Id, CancellationToken.None);

        Assert.Empty(h.Sender.Sent);
        h.Db.ChangeTracker.Clear();
        var stored = await h.Db.EmailOutboxMessages.SingleAsync(m => m.Id == message.Id);
        Assert.Equal(EmailDeliveryStatus.Cancelled, stored.Status);
        Assert.Contains("reversed", stored.LastError);
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

    private static Harness CreateHarness(int maxAttempts = 5, AppDbContext? database = null)
    {
        var db = database ?? new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options);
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
        services.AddSingleton<StubPdf>();
        services.AddSingleton<IInvoicePdfGenerator>(sp => sp.GetRequiredService<StubPdf>());
        services.AddSingleton<IReceiptPdfGenerator>(sp => sp.GetRequiredService<StubPdf>());
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
        public bool FailPermanently { get; set; }
        public Exception? Throw { get; set; }
        public List<(string To, string Subject, string Body)> Sent { get; } = [];
        public List<IReadOnlyList<EmailAttachment>?> Attachments { get; } = [];

        public Task SendAsync(
            string toEmail,
            string subject,
            string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null,
            CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
                throw Throw;
            if (FailPermanently)
                throw new PermanentEmailFailureException("recipient rejected");
            if (Fail)
                throw new InvalidOperationException("provider down");
            Sent.Add((toEmail, subject, htmlBody));
            Attachments.Add(attachments);
            return Task.CompletedTask;
        }
    }

    private sealed class StubPdf : IInvoicePdfGenerator, IReceiptPdfGenerator
    {
        public byte[] Generate(Invoice invoice, Customer customer, InvoiceLetterhead letterhead, string? payUrl = null) => [1, 2, 3];
        public byte[] GenerateReceipt(Receipt receipt, Invoice invoice, Customer customer, InvoiceLetterhead letterhead) => [4, 5, 6];
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
