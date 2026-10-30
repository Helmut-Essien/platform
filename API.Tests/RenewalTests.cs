using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Api.Services.Renewals;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class PeriodMathTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(2026, 1, 31, 1, 31, 2026, 2, 28)]
    [InlineData(2028, 1, 31, 1, 31, 2028, 2, 29)]
    [InlineData(2026, 2, 28, 1, 31, 2026, 3, 31)] // anchor restores the 31st after February
    [InlineData(2028, 2, 29, 12, 29, 2029, 2, 28)] // leap day, non-leap target
    [InlineData(2028, 2, 29, 48, 29, 2032, 2, 29)] // leap day to leap year
    [InlineData(2026, 12, 15, 1, 15, 2027, 1, 15)] // year rollover
    [InlineData(2026, 4, 30, 12, null, 2027, 4, 30)]
    public void AddMonths_ClampsToMonthEnd_AndHonoursAnchor(int y, int m, int d, int months, int? anchor, int ey, int em, int ed) =>
        Assert.Equal(D(ey, em, ed), PeriodMath.AddMonths(D(y, m, d), months, anchor));

    [Fact]
    public void AddMonths_KeepsTimeOfDay_AndUtc()
    {
        var start = new DateTime(2026, 3, 31, 17, 45, 10, DateTimeKind.Utc);
        var result = PeriodMath.AddMonths(start, 1);
        Assert.Equal(new DateTime(2026, 4, 30, 17, 45, 10, DateTimeKind.Utc), result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void AddMonths_RejectsBadPeriods() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PeriodMath.AddMonths(DateTime.UtcNow, 0));
}

public class RenewalTests
{
    internal static AppDbContext CreateDb(string? name = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name ?? NUlid.Ulid.NewUlid().ToString()).Options);

    internal static PayLinkService CreatePayLinks(bool paystackEnabled = true, int validDays = 60) => new(
        Options.Create(new BillingSettings { PayLinkValidDays = validDays }),
        Options.Create(new JwtSettings { Key = "unit-test-jwt-key-0123456789-abcdefghijklmnop" }),
        Options.Create(new AuthSettings { ClientBaseUrl = "https://helmutcode.runasp.net/" }),
        Options.Create(new PaystackOptions { SecretKey = paystackEnabled ? "sk_test_0123456789abcdef" : null }));

    internal static BillingService CreateBilling(AppDbContext db, RecordingAudit? audit = null, BillingSettings? settings = null,
        PayLinkService? payLinks = null) =>
        new(db, audit ?? new RecordingAudit(), new EmailOutboxService(db), new EmailTemplateService(), new NoopDeny(),
            billingSettings: Options.Create(settings ?? new BillingSettings()), payLinks: payLinks);

    internal static async Task<License> SeedLicenseAsync(AppDbContext db, DateTime? expiresAt, decimal? price = 1200m,
        LicenseStatus status = LicenseStatus.Active, bool suspendedCustomer = false, string currency = "GHS", int months = 12)
    {
        var customer = new Customer { Name = "Acme Ltd", ContactEmail = $"billing+{NUlid.Ulid.NewUlid()}@acme.test", IsSuspended = suspendedCustomer };
        var product = new ServiceProduct { Name = "Hostel", Code = "HOSTEL-" + NUlid.Ulid.NewUlid().ToString()[^10..], IsAvailableForSale = true };
        db.Customers.Add(customer);
        db.ServiceProducts.Add(product);
        var license = new License
        {
            CustomerId = customer.Id,
            ServiceProductId = product.Id,
            PlanName = "Pro",
            Status = status,
            ExpiresAt = expiresAt,
            BillingAmount = price,
            BillingCurrency = price is null ? null : currency,
            BillingPeriodMonths = months,
            BillingAnchorDay = expiresAt?.Day
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return license;
    }

    internal static Task<ReceiptDto> PayInFullAsync(BillingService billing, InvoiceDto invoice, string key = "pay-1") =>
        billing.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = key,
            AmountPaid = invoice.TotalAmount,
            PaymentMethod = PaymentMethod.BankTransfer,
            PaymentReference = "REF-" + key
        }, "admin");

    private static async Task<License> ReloadAsync(AppDbContext db, string id)
    {
        db.ChangeTracker.Clear();
        return await db.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == id);
    }

    private static DateTime Day(int offset) => DateTime.UtcNow.Date.AddDays(offset);

    // ---- invoice creation ----

    [Fact]
    public async Task CreateRenewalInvoice_UsesLicensePrice_DueOnExpiry_AndIsIdempotent()
    {
        await using var db = CreateDb();
        var expiry = Day(10);
        var license = await SeedLicenseAsync(db, expiry);
        var billing = CreateBilling(db);

        var invoice = await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);
        Assert.NotNull(invoice);
        Assert.Equal(1200m, invoice!.TotalAmount);
        Assert.Equal(expiry, invoice.DueDate);
        Assert.Equal(InvoiceStatus.Sent, invoice.Status);
        Assert.Contains("Renewal of Pro", invoice.Description);

        Assert.Null(await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor));
        await Assert.ThrowsAsync<ConflictException>(() =>
            billing.CreateRenewalInvoiceAsync(license.Id, null, "admin", explicitRequest: true));
        Assert.Equal(1, await db.Invoices.CountAsync(i => i.LicenseId == license.Id));
        Assert.Equal(1, await db.EmailOutboxMessages.CountAsync(m => m.Kind == EmailDeliveryKind.Invoice));
    }

    [Fact]
    public async Task CreateRenewalInvoice_LapsedLicense_DueAfterGraceFromToday()
    {
        await using var db = CreateDb();
        var license = await SeedLicenseAsync(db, Day(-5), status: LicenseStatus.Expired);
        var billing = CreateBilling(db, settings: new BillingSettings { RenewalGraceDays = 7 });

        var invoice = await billing.CreateRenewalInvoiceAsync(license.Id, null, BillingService.RenewalActor);
        Assert.Equal(Day(7), invoice!.DueDate);
    }

    [Theory]
    [InlineData("suspendedCustomer")]
    [InlineData("revoked")]
    [InlineData("suspended")]
    [InlineData("noPrice")]
    [InlineData("expiryMoved")]
    public async Task CreateRenewalInvoice_SkipsIneligible(string scenario)
    {
        await using var db = CreateDb();
        var expiry = Day(10);
        var license = await SeedLicenseAsync(db, expiry,
            price: scenario == "noPrice" ? null : 500m,
            status: scenario switch { "revoked" => LicenseStatus.Revoked, "suspended" => LicenseStatus.Suspended, _ => LicenseStatus.Active },
            suspendedCustomer: scenario == "suspendedCustomer");
        var billing = CreateBilling(db);

        Assert.Null(await billing.CreateRenewalInvoiceAsync(license.Id, scenario == "expiryMoved" ? expiry.AddDays(-1) : expiry,
            BillingService.RenewalActor));
        Assert.Equal(0, await db.Invoices.IgnoreQueryFilters().CountAsync());
        if (scenario != "expiryMoved")
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                billing.CreateRenewalInvoiceAsync(license.Id, null, "admin", explicitRequest: true));
    }

    [Fact]
    public async Task VoidedRenewal_NotRecreatedByWorker_ButAdminCanRecreate()
    {
        await using var db = CreateDb();
        var expiry = Day(3);
        var license = await SeedLicenseAsync(db, expiry);
        var billing = CreateBilling(db);
        var first = await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);
        await billing.VoidInvoiceAsync(first!.Id, "admin");

        Assert.Null(await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor));
        var again = await billing.CreateRenewalInvoiceAsync(license.Id, null, "admin", explicitRequest: true);
        Assert.NotNull(again);
        Assert.NotEqual(first.Id, again!.Id);
        Assert.Equal(1, await db.Invoices.CountAsync(i => i.LicenseId == license.Id && i.Status != InvoiceStatus.Void));
    }

    // ---- worker ----

    [Fact]
    public async Task Worker_InvoicesOnlyDueLicenses_AndSecondRunCreatesNothing()
    {
        var name = NUlid.Ulid.NewUlid().ToString();
        await using (var db = CreateDb(name))
        {
            await SeedLicenseAsync(db, Day(5));                                   // due
            await SeedLicenseAsync(db, Day(-3), status: LicenseStatus.Expired);   // lapsed, inside lookback
            await SeedLicenseAsync(db, Day(40));                                  // too early
            await SeedLicenseAsync(db, Day(-90), status: LicenseStatus.Expired);  // beyond lookback
            await SeedLicenseAsync(db, Day(5), price: 0m);                        // free
            await SeedLicenseAsync(db, Day(5), suspendedCustomer: true);
            await SeedLicenseAsync(db, Day(5), status: LicenseStatus.Revoked);
            var optedOut = await SeedLicenseAsync(db, Day(5));
            var tracked = await db.Licenses.SingleAsync(l => l.Id == optedOut.Id);
            tracked.AutoRenewInvoice = false;
            await db.SaveChangesAsync();
        }

        var worker = CreateWorker(name, new BillingSettings());
        Assert.Equal(2, await worker.ProcessAsync(CancellationToken.None));
        Assert.Equal(0, await worker.ProcessAsync(CancellationToken.None));

        await using var check = CreateDb(name);
        Assert.Equal(2, await check.Invoices.IgnoreQueryFilters().CountAsync(i => i.RenewalForExpiresAt != null));
    }

    [Fact]
    public async Task Worker_BatchesBeyondOnePage()
    {
        var name = NUlid.Ulid.NewUlid().ToString();
        await using (var db = CreateDb(name))
        {
            for (var i = 0; i < RenewalInvoiceWorker.BatchSize + 5; i++)
                await SeedLicenseAsync(db, Day(2));
        }

        var worker = CreateWorker(name, new BillingSettings());
        Assert.Equal(RenewalInvoiceWorker.BatchSize + 5, await worker.ProcessAsync(CancellationToken.None));
    }

    private static RenewalInvoiceWorker CreateWorker(string dbName, BillingSettings settings)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateDb(dbName));
        services.AddScoped<IBillingService>(sp => CreateBilling(sp.GetRequiredService<AppDbContext>(), settings: settings));
        var provider = services.BuildServiceProvider();
        return new RenewalInvoiceWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticMonitor<BillingSettings>(settings), NullLogger<RenewalInvoiceWorker>.Instance);
    }

    // ---- renew on payment ----

    [Fact]
    public async Task Payment_ExtendsFromPreviousExpiry_OnePeriod_Once()
    {
        await using var db = CreateDb();
        var expiry = Day(10);
        var license = await SeedLicenseAsync(db, expiry);
        var audit = new RecordingAudit();
        var billing = CreateBilling(db, audit);
        var invoice = await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);

        await PayInFullAsync(billing, invoice!);
        await PayInFullAsync(billing, invoice!); // idempotent replay

        var renewed = await ReloadAsync(db, license.Id);
        Assert.Equal(PeriodMath.AddMonths(expiry, 12), renewed.ExpiresAt);
        Assert.Single(audit.Actions, AuditAction.LicenseAutoRenewedPaid);
        Assert.Equal(1, await db.EmailOutboxMessages.CountAsync(m => m.Kind == EmailDeliveryKind.RenewalConfirmation));
    }

    [Fact]
    public async Task Payment_LongLapsed_ExtendsFromPaymentDate_AndReactivatesExpired()
    {
        await using var db = CreateDb();
        var expiry = Day(-20);
        var license = await SeedLicenseAsync(db, expiry, status: LicenseStatus.Expired, months: 1);
        var billing = CreateBilling(db, settings: new BillingSettings { RenewalGraceDays = 7 });
        var invoice = await billing.CreateRenewalInvoiceAsync(license.Id, null, BillingService.RenewalActor);

        await PayInFullAsync(billing, invoice!);

        var renewed = await ReloadAsync(db, license.Id);
        Assert.Equal(LicenseStatus.Active, renewed.Status);
        Assert.Equal(PeriodMath.AddMonths(Day(0), 1), renewed.ExpiresAt);
    }

    [Fact]
    public async Task Payment_WithinGrace_ExtendsFromOldExpiry()
    {
        await using var db = CreateDb();
        var expiry = Day(-3);
        var license = await SeedLicenseAsync(db, expiry, status: LicenseStatus.Expired);
        var billing = CreateBilling(db, settings: new BillingSettings { RenewalGraceDays = 7 });
        var invoice = await billing.CreateRenewalInvoiceAsync(license.Id, null, BillingService.RenewalActor);

        await PayInFullAsync(billing, invoice!);

        var renewed = await ReloadAsync(db, license.Id);
        Assert.Equal(PeriodMath.AddMonths(expiry, 12), renewed.ExpiresAt);
        Assert.Equal(LicenseStatus.Active, renewed.Status);
    }

    [Fact]
    public async Task Payment_PaymentDateBasis_NeverShortensRunningTerm()
    {
        await using var db = CreateDb();
        var expiry = Day(10);
        var license = await SeedLicenseAsync(db, expiry);
        var billing = CreateBilling(db, settings: new BillingSettings { RenewalExtendFrom = RenewalExtendFrom.PaymentDate });
        var invoice = await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);

        await PayInFullAsync(billing, invoice!);

        Assert.Equal(PeriodMath.AddMonths(expiry, 12), (await ReloadAsync(db, license.Id)).ExpiresAt);
    }

    [Fact]
    public async Task Payment_ManualSuspension_IsKept_ButTermExtended()
    {
        await using var db = CreateDb();
        var expiry = Day(10);
        var license = await SeedLicenseAsync(db, expiry);
        var billing = CreateBilling(db);
        var invoice = await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);
        var tracked = await db.Licenses.SingleAsync(l => l.Id == license.Id);
        tracked.Status = LicenseStatus.Suspended;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await PayInFullAsync(billing, invoice!);

        var after = await ReloadAsync(db, license.Id);
        Assert.Equal(LicenseStatus.Suspended, after.Status);
        Assert.Equal(PeriodMath.AddMonths(expiry, 12), after.ExpiresAt);
    }

    [Fact]
    public async Task ManualRenewalBeforePayment_PaymentDoesNotExtendTwice()
    {
        await using var db = CreateDb();
        var expiry = Day(10);
        var license = await SeedLicenseAsync(db, expiry);
        var audit = new RecordingAudit();
        var billing = CreateBilling(db, audit);
        var invoice = await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);
        var manual = expiry.AddYears(1);
        var tracked = await db.Licenses.SingleAsync(l => l.Id == license.Id);
        tracked.ExpiresAt = manual; // admin renewed by hand
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await PayInFullAsync(billing, invoice!);

        Assert.Equal(manual, (await ReloadAsync(db, license.Id)).ExpiresAt);
        Assert.Contains(AuditAction.LicenseRenewalSkipped, audit.Actions);
        Assert.DoesNotContain(AuditAction.LicenseAutoRenewedPaid, audit.Actions);
    }

    [Fact]
    public async Task ManualRenewal_ThenNextPeriod_VoidsStaleUnpaidRenewalInvoice()
    {
        await using var db = CreateDb();
        var expiry = Day(10);
        var license = await SeedLicenseAsync(db, expiry);
        var billing = CreateBilling(db);
        var stale = await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);
        var manual = Day(12);
        var tracked = await db.Licenses.SingleAsync(l => l.Id == license.Id);
        tracked.ExpiresAt = manual; // admin moved the expiry by hand
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var next = await billing.CreateRenewalInvoiceAsync(license.Id, manual, BillingService.RenewalActor);

        Assert.NotNull(next);
        db.ChangeTracker.Clear();
        var old = await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == stale!.Id);
        Assert.Equal(InvoiceStatus.Void, old.Status);
        Assert.Null(old.PayLinkNonce);
        Assert.Equal(1, await db.Invoices.CountAsync(i => i.LicenseId == license.Id && i.Status != InvoiceStatus.Void));
    }

    [Fact]
    public async Task StaleRenewalInvoiceWithPayment_BlocksNextPeriod()
    {
        await using var db = CreateDb();
        var expiry = Day(10);
        var license = await SeedLicenseAsync(db, expiry);
        var billing = CreateBilling(db);
        var stale = await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);
        await billing.RecordReceiptAsync(stale!.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "part", AmountPaid = 100m, PaymentMethod = PaymentMethod.BankTransfer, PaymentReference = "R"
        }, "admin");
        var manual = Day(12);
        db.ChangeTracker.Clear();
        var tracked = await db.Licenses.SingleAsync(l => l.Id == license.Id);
        tracked.ExpiresAt = manual;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Null(await billing.CreateRenewalInvoiceAsync(license.Id, manual, BillingService.RenewalActor));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            billing.CreateRenewalInvoiceAsync(license.Id, null, "admin", explicitRequest: true));
        Assert.Equal(InvoiceStatus.PartiallyPaid,
            (await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == stale.Id)).Status);
    }

    [Fact]
    public async Task PartialPayment_DoesNotRenew()
    {
        await using var db = CreateDb();
        var expiry = Day(10);
        var license = await SeedLicenseAsync(db, expiry);
        var billing = CreateBilling(db);
        var invoice = await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);

        await billing.RecordReceiptAsync(invoice!.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "part", AmountPaid = 100m, PaymentMethod = PaymentMethod.Cash
        }, "admin");

        Assert.Equal(expiry, (await ReloadAsync(db, license.Id)).ExpiresAt);
    }

    [Fact]
    public async Task Reversal_RevertsUntouchedExtension_AndExpiresIfLapsed()
    {
        await using var db = CreateDb();
        var expiry = Day(-2);
        var license = await SeedLicenseAsync(db, expiry, status: LicenseStatus.Expired);
        var audit = new RecordingAudit();
        var billing = CreateBilling(db, audit);
        var invoice = await billing.CreateRenewalInvoiceAsync(license.Id, null, BillingService.RenewalActor);
        var receipt = await PayInFullAsync(billing, invoice!);
        Assert.Equal(LicenseStatus.Active, (await ReloadAsync(db, license.Id)).Status);

        await billing.ReverseReceiptAsync(invoice!.Id, receipt.Id, new ReverseReceiptRequest { IdempotencyKey = "rev", Reason = "bounced" }, "admin");

        var after = await ReloadAsync(db, license.Id);
        Assert.Equal(expiry, after.ExpiresAt);
        Assert.Equal(LicenseStatus.Expired, after.Status);
        Assert.Contains(AuditAction.LicenseRenewalReverted, audit.Actions);

        // paying again re-applies exactly once
        await PayInFullAsync(billing, invoice, "pay-2");
        Assert.Equal(PeriodMath.AddMonths(expiry, 12), (await ReloadAsync(db, license.Id)).ExpiresAt);
    }

    [Fact]
    public async Task Reversal_AfterOtherChange_KeepsExpiry_AndFlagsConflict()
    {
        await using var db = CreateDb();
        var expiry = Day(10);
        var license = await SeedLicenseAsync(db, expiry);
        var audit = new RecordingAudit();
        var billing = CreateBilling(db, audit);
        var invoice = await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);
        var receipt = await PayInFullAsync(billing, invoice!);
        var tracked = await db.Licenses.SingleAsync(l => l.Id == license.Id);
        var edited = tracked.ExpiresAt!.Value.AddMonths(2);
        tracked.ExpiresAt = edited;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await billing.ReverseReceiptAsync(invoice!.Id, receipt.Id, new ReverseReceiptRequest { IdempotencyKey = "rev", Reason = "chargeback" }, "admin");

        Assert.Equal(edited, (await ReloadAsync(db, license.Id)).ExpiresAt);
        Assert.Contains(AuditAction.LicenseRenewalReversalConflict, audit.Actions);
    }

    [Fact]
    public async Task OrdinaryInvoicePayment_DoesNotTouchExpiry()
    {
        await using var db = CreateDb();
        var expiry = Day(10);
        var license = await SeedLicenseAsync(db, expiry);
        var billing = CreateBilling(db);
        var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = license.CustomerId, LicenseId = license.Id, Status = InvoiceStatus.Sent, Currency = "GHS", Subtotal = 50m
        }, "admin");

        await PayInFullAsync(billing, invoice);

        Assert.Equal(expiry, (await ReloadAsync(db, license.Id)).ExpiresAt);
    }

    // ---- license billing fields ----

    [Fact]
    public void ApplyBilling_ValidatesAndClears()
    {
        var license = new License { CustomerId = "c", ServiceProductId = "p", PlanName = "Pro" };
        LicenseService.ApplyBilling(license, new Platform.Shared.Dtos.Licenses.LicenseBillingDto
        {
            Amount = 99.5m, TaxAmount = 1m, Currency = "ghs", PeriodMonths = 6
        });
        Assert.Equal(99.5m, license.BillingAmount);
        Assert.Equal("GHS", license.BillingCurrency);
        Assert.Equal(6, license.BillingPeriodMonths);

        LicenseService.ApplyBilling(license, new Platform.Shared.Dtos.Licenses.LicenseBillingDto { Amount = 0m });
        Assert.Null(license.BillingAmount);
        Assert.Null(license.BillingCurrency);

        Assert.Throws<InvalidOperationException>(() =>
            LicenseService.ApplyBilling(license, new Platform.Shared.Dtos.Licenses.LicenseBillingDto { Amount = 1m, PeriodMonths = 0 }));
    }

    // ---- helpers ----

    internal sealed class RecordingAudit : IAuditLogService
    {
        public List<AuditAction> Actions { get; } = [];

        public Task<IReadOnlyList<AuditLogDto>> ListAsync(string? customerId = null, string? licenseId = null,
            AuditAction? action = null, int limit = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogDto>>([]);

        public Task WriteAsync(AuditAction action, string performedBy, string? customerId = null, string? licenseId = null,
            string? invoiceId = null, string? detailsJson = null, string? ipAddress = null, CancellationToken cancellationToken = default)
        {
            lock (Actions)
                Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    internal sealed class NoopDeny : ILicenseDenyListService
    {
        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
