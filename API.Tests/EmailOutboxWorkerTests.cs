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
        // 30 s, 1 min, 2 min, 4 min, … capped at 6 hours.
        Assert.Equal(TimeSpan.FromSeconds(30), EmailOutboxWorker.ComputeBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(30), EmailOutboxWorker.ComputeBackoff(0));
        Assert.Equal(TimeSpan.FromMinutes(1), EmailOutboxWorker.ComputeBackoff(2));
        Assert.Equal(TimeSpan.FromMinutes(2), EmailOutboxWorker.ComputeBackoff(3));
        Assert.Equal(TimeSpan.FromMinutes(4), EmailOutboxWorker.ComputeBackoff(4));
        Assert.Equal(TimeSpan.FromMinutes(360), EmailOutboxWorker.ComputeBackoff(12));
        Assert.Equal(TimeSpan.FromMinutes(360), EmailOutboxWorker.ComputeBackoff(500));
        Assert.Equal(TimeSpan.FromMinutes(360), EmailOutboxWorker.ComputeBackoff(int.MaxValue));
        // Configurable base (Email:Outbox:RetryBaseSeconds), clamped to 5–3600 s.
        Assert.Equal(TimeSpan.FromMinutes(2), EmailOutboxWorker.ComputeBackoff(1, 120));
        Assert.Equal(TimeSpan.FromSeconds(5), EmailOutboxWorker.ComputeBackoff(1, 0));
        Assert.Equal(TimeSpan.FromHours(1), EmailOutboxWorker.ComputeBackoff(1, 99999));
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
        // First retry after about 30 seconds (not minutes).
        Assert.InRange(stored.NextAttemptAt!.Value, DateTime.UtcNow.AddSeconds(20), DateTime.UtcNow.AddSeconds(31));
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

    internal sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Lease_IsShortRightAfterStart_AndTheNormalTenMinutesLater()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        EmailOutboxWorker Worker(int timeout = 15, int reclaim = 120) => new(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmailSettings { TimeoutSeconds = timeout, Outbox = new EmailOutboxSettings { StartupReclaimSeconds = reclaim } }),
            NullLogger<EmailOutboxWorker>.Instance, clock: clock);

        var worker = Worker();
        Assert.Equal(TimeSpan.FromSeconds(120), worker.CurrentLease(clock.Now.UtcDateTime));
        // Never shorter than 4x the send timeout (a send in progress elsewhere must finish first), clamped 30-600 s.
        Assert.Equal(TimeSpan.FromSeconds(240), Worker(timeout: 60).CurrentLease(clock.Now.UtcDateTime));
        Assert.Equal(TimeSpan.FromSeconds(60), Worker(reclaim: 1).CurrentLease(clock.Now.UtcDateTime));
        Assert.Equal(TimeSpan.FromSeconds(600), Worker(reclaim: 5000).CurrentLease(clock.Now.UtcDateTime));
        Assert.Equal(TimeSpan.FromSeconds(60), Worker(timeout: 0, reclaim: 0).CurrentLease(clock.Now.UtcDateTime)); // default 15 s timeout x4
        Assert.Equal(EmailOutboxWorker.LeaseTimeout, worker.CurrentLease(clock.Now.UtcDateTime + EmailOutboxWorker.StartupWindow));
    }

    private static async Task<string> SeedSendingAsync(AppDbContext db, DateTime updatedAt, int attempts = 1)
    {
        var message = new EmailOutboxMessage
        {
            Kind = EmailDeliveryKind.RenewalConfirmation, Status = EmailDeliveryStatus.Sending, ToEmail = "owner@acme.test",
            Subject = "Subject", HtmlBody = "<p>Hello</p>", AttemptCount = attempts, UpdatedAt = updatedAt
        };
        db.EmailOutboxMessages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }

    [Fact]
    public async Task Postgres_AfterStart_AnInterruptedSendIsReclaimedWithinMinutes_NotTen()
    {
        await using var pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();
        if (pg is null)
            return;
        var now = DateTime.UtcNow;
        string interrupted, recent, old;
        await using (var seed = pg.NewContext())
        {
            interrupted = await SeedSendingAsync(seed, now.AddMinutes(-3)); // the process stopped mid-send 3 min ago
            recent = await SeedSendingAsync(seed, now.AddSeconds(-20));     // may still be sending elsewhere
        }

        // Fresh process: the short startup lease applies.
        await using (var h = CreateHarness(scopedPostgres: pg.ConnectionString))
            Assert.Equal([interrupted], await h.Worker.ClaimBatchAsync(CancellationToken.None));

        await using (var seed = pg.NewContext())
            old = await SeedSendingAsync(seed, now.AddMinutes(-11));
        await using (var seed = pg.NewContext())
            await seed.EmailOutboxMessages.Where(m => m.Id == interrupted)
                .ExecuteUpdateAsync(x => x.SetProperty(m => m.UpdatedAt, now.AddMinutes(-3)));

        // A process running for more than the startup window only takes rows past the normal 10-minute lease.
        var clock = new MutableClock(DateTimeOffset.UtcNow - EmailOutboxWorker.StartupWindow - TimeSpan.FromSeconds(1));
        await using (var h = CreateHarness(scopedPostgres: pg.ConnectionString, clock: clock))
        {
            clock.Now = DateTimeOffset.UtcNow;
            Assert.Equal([old], await h.Worker.ClaimBatchAsync(CancellationToken.None));
        }
        await using var check = pg.NewContext();
        Assert.Equal(EmailDeliveryStatus.Sending, (await check.EmailOutboxMessages.FirstAsync(m => m.Id == recent)).Status);
    }

    [Fact]
    public async Task Postgres_TwoOverlappingInstances_SendAReclaimedEmailOnce()
    {
        await using var pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();
        if (pg is null)
            return;
        await using (var seed = pg.NewContext())
        {
            seed.EmailOutboxMessages.Add(new EmailOutboxMessage
            {
                Kind = EmailDeliveryKind.RenewalConfirmation, Status = EmailDeliveryStatus.Pending, ToEmail = "owner@acme.test",
                Subject = "Subject", HtmlBody = "<p>Hello</p>", NextAttemptAt = DateTime.UtcNow.AddMinutes(-1)
            });
            await seed.SaveChangesAsync();
        }

        // The old instance claims the email, but it waits in its batch behind slow sends...
        await using var oldInstance = CreateHarness(scopedPostgres: pg.ConnectionString);
        var claim = await oldInstance.Worker.ClaimAsync(CancellationToken.None);
        var id = Assert.Single(claim.Ids);
        await using (var age = pg.NewContext()) // ...long enough for the new instance's startup lease to pass
            await age.EmailOutboxMessages.Where(m => m.Id == id)
                .ExecuteUpdateAsync(x => x.SetProperty(m => m.UpdatedAt, claim.ClaimedAt.AddMinutes(-3)));
        var agedClaim = claim.ClaimedAt.AddMinutes(-3);

        // The new instance reclaims it.
        await using var newInstance = CreateHarness(scopedPostgres: pg.ConnectionString);
        var reclaim = await newInstance.Worker.ClaimAsync(CancellationToken.None);
        Assert.Equal([id], reclaim.Ids);

        // The old instance reaches it in its batch: no longer its claim, so it skips it (even before the new one sends).
        await oldInstance.Worker.SendAsync(id, agedClaim, CancellationToken.None);
        Assert.Empty(oldInstance.Sender.Sent);

        await newInstance.Worker.SendAsync(id, reclaim.ClaimedAt, CancellationToken.None);
        Assert.Single(newInstance.Sender.Sent);
        await oldInstance.Worker.SendAsync(id, agedClaim, CancellationToken.None);
        Assert.Empty(oldInstance.Sender.Sent);
        await using var check = pg.NewContext();
        var stored = await check.EmailOutboxMessages.FirstAsync(m => m.Id == id);
        Assert.Equal((EmailDeliveryStatus.Sent, 1), (stored.Status, stored.AttemptCount));
    }

    [Fact]
    public async Task Signal_CoalescesABurst_IntoOneWake()
    {
        var signal = new EmailOutboxSignal();
        Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        for (var i = 0; i < 50; i++)
            signal.Notify();
        Parallel.For(0, 50, _ => signal.Notify());
        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None)); // one pass claims the whole burst
    }

    private static AppDbContext InMemoryWithWake(EmailOutboxSignal signal) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .AddInterceptors(new EmailOutboxWakeInterceptor(signal))
            .Options);

    private static EmailOutboxMessage NewMessage() => new()
    {
        Kind = EmailDeliveryKind.RenewalConfirmation, ToEmail = "owner@acme.test", Subject = "Subject", HtmlBody = "<p>Hello</p>"
    };

    [Fact]
    public async Task WakeInterceptor_SignalsAfterASaveThatQueuedAnEmail_Only()
    {
        var signal = new EmailOutboxSignal();
        await using var db = InMemoryWithWake(signal);
        db.Customers.Add(new Customer { Name = "Acme", ContactEmail = "a@acme.test" });
        await db.SaveChangesAsync();
        Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None)); // no email queued

        new EmailOutboxService(db).Enqueue(EmailDeliveryKind.Welcome, "a@acme.test", "Hi", "<p>Hi</p>");
        await db.SaveChangesAsync();
        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));

        var sent = await db.EmailOutboxMessages.FirstAsync();
        sent.Status = EmailDeliveryStatus.Sent; // updating an email is not queuing one
        db.SaveChanges();
        Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));

        sent.Status = EmailDeliveryStatus.Failed;
        db.SaveChanges();
        Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        await new EmailOutboxService(db).RetryAsync(sent.Id); // the admin Retry button puts it back to Pending
        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task Postgres_WakeInterceptor_InAnExplicitTransaction_SignalsOnlyAfterCommit()
    {
        await using var pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();
        if (pg is null)
            return;
        var signal = new EmailOutboxSignal();
        AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(pg.ConnectionString)
            .AddInterceptors(new EmailOutboxWakeInterceptor(signal)).Options);

        await using (var db = NewDb())
        {
            db.EmailOutboxMessages.Add(NewMessage());
            await db.SaveChangesAsync();
            Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None)); // no transaction: committed on save
        }
        await using (var db = NewDb())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.EmailOutboxMessages.Add(NewMessage());
            await db.SaveChangesAsync();
            Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None)); // not visible to the worker yet
            await tx.CommitAsync();
            Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        }

        await using (var db = NewDb())
        {
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                db.EmailOutboxMessages.Add(NewMessage());
                await db.SaveChangesAsync();
                await tx.RollbackAsync();
            }
            Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));

            // Disposed without commit, then a later unrelated transaction commits: no stale signal.
            db.ChangeTracker.Clear();
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                db.EmailOutboxMessages.Add(NewMessage());
                await db.SaveChangesAsync();
            }
            db.ChangeTracker.Clear();
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                db.Customers.Add(new Customer { Name = "Acme", ContactEmail = "a@acme.test" });
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        }

        await using var check = pg.NewContext();
        Assert.Equal(2, await check.EmailOutboxMessages.CountAsync()); // only the two committed ones exist
    }

    [Fact]
    public async Task Postgres_Worker_SendsACommittedEmailRightAway_NotAtTheNextPoll()
    {
        await using var pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();
        if (pg is null)
            return;
        var signal = new EmailOutboxSignal();
        await using var h = CreateHarness(scopedPostgres: pg.ConnectionString, signal: signal, pollSeconds: 300);
        await h.Worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(500); // the first (empty) pass has run; the next poll is 5 minutes away
            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(pg.ConnectionString)
                .AddInterceptors(new EmailOutboxWakeInterceptor(signal)).Options))
            {
                db.EmailOutboxMessages.Add(NewMessage());
                await db.SaveChangesAsync();
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (h.Sender.Sent.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.Single(h.Sender.Sent);
        }
        finally
        {
            await h.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Postgres_Worker_KeepsPolling_AfterAnEmptyClaim()
    {
        // Regression: an empty claim left the in-flight batch as an empty list, so every later pass skipped the
        // claim and new emails waited until the process restarted. No wake signal here: the poll alone must find it.
        await using var pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();
        if (pg is null)
            return;
        await using var h = CreateHarness(scopedPostgres: pg.ConnectionString, pollSeconds: 2);
        await h.Worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(500); // first (empty) pass done
            await using (var db = pg.NewContext())
            {
                db.EmailOutboxMessages.Add(NewMessage());
                await db.SaveChangesAsync();
            }

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (h.Sender.Sent.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.Single(h.Sender.Sent);
        }
        finally
        {
            await h.Worker.StopAsync(CancellationToken.None);
        }
    }

    private static Harness CreateHarness(int maxAttempts = 5, AppDbContext? database = null,
        string? scopedPostgres = null, TimeProvider? clock = null, EmailOutboxSignal? signal = null, int pollSeconds = 10)
    {
        var db = database ?? (scopedPostgres is not null
            ? new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(scopedPostgres).Options)
            : null) ?? new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options);
        var settings = Options.Create(new EmailSettings
        {
            Outbox = new EmailOutboxSettings
            {
                MaxAttempts = maxAttempts,
                PollIntervalSeconds = pollSeconds,
                EncryptionKey = Convert.ToBase64String(new byte[32])
            }
        });
        var protector = new EmailPayloadProtector(
            settings,
            new ConfigurationBuilder().Build(),
            new FakeEnv());
        var sender = new RecordingSender();

        var services = new ServiceCollection();
        // scopedPostgres: each worker scope gets its own context, as in production (fresh reads, no shared tracking).
        if (scopedPostgres is not null)
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(scopedPostgres));
        else
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
            NullLogger<EmailOutboxWorker>.Instance,
            signal,
            clock);
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
