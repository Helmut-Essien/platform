using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Identity;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class AdminAuthServiceTests
{
    [Fact]
    public async Task RequestPasswordResetAsync_DoesNotThrowForUnknownEmail()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var exception = await Record.ExceptionAsync(() =>
            service.RequestPasswordResetAsync("missing@example.test"));

        Assert.Null(exception);
        Assert.Empty(db.EmailOutboxMessages);
    }

    [Fact]
    public async Task RequestPasswordResetAsync_QueuesEncryptedOutboxWithoutPlaintextToken()
    {
        await using var db = CreateDb();
        var userManager = CreateUserManager(db);
        await userManager.CreateAsync(new ApplicationUser
        {
            UserName = "admin@example.test",
            Email = "admin@example.test",
            EmailConfirmed = true
        }, "Password1!");

        var protector = CreateProtector();
        var service = CreateService(db, protector);
        await service.RequestPasswordResetAsync("admin@example.test");

        var message = Assert.Single(db.EmailOutboxMessages);
        Assert.Equal(EmailDeliveryKind.PasswordReset, message.Kind);
        Assert.Equal("admin@example.test", message.ToEmail);
        Assert.Contains(AdminAuthService.ResetLinkPlaceholder, message.HtmlBody);
        Assert.DoesNotContain("token=", message.HtmlBody);
        Assert.DoesNotContain("reset-password?", message.HtmlBody);
        Assert.NotNull(message.EncryptedPayload);

        var link = protector.Unprotect(message.EncryptedPayload!);
        Assert.Contains("reset-password?", link);
        Assert.Contains("token=", link);
        Assert.Contains("admin%40example.test", link);
    }

    [Fact]
    public async Task ResetPasswordAsync_ThrowsForInvalidToken()
    {
        await using var db = CreateDb();
        var userManager = CreateUserManager(db);
        await userManager.CreateAsync(new ApplicationUser
        {
            UserName = "admin@example.test",
            Email = "admin@example.test",
            EmailConfirmed = true
        }, "Password1!");

        var service = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResetPasswordAsync("admin@example.test", "not-a-valid-token", "NewPassword1!"));
    }

    [Fact]
    public async Task ResetPasswordAsync_UpdatesPasswordWithValidToken()
    {
        await using var db = CreateDb();
        var userManager = CreateUserManager(db);
        var user = new ApplicationUser
        {
            UserName = "admin@example.test",
            Email = "admin@example.test",
            EmailConfirmed = true
        };
        await userManager.CreateAsync(user, "Password1!");
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var encoded = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            System.Text.Encoding.UTF8.GetBytes(token));

        var service = CreateService(db);
        await service.ResetPasswordAsync(user.Email!, encoded, "NewPassword2!");

        Assert.True(await userManager.CheckPasswordAsync(user, "NewPassword2!"));
    }

    private static AdminAuthService CreateService(AppDbContext db, EmailPayloadProtector? protector = null)
    {
        var userManager = CreateUserManager(db);
        return new AdminAuthService(
            userManager,
            db,
            new EmailOutboxService(db),
            protector ?? CreateProtector(),
            new EmailTemplateService(),
            Options.Create(new AuthSettings { ClientBaseUrl = "http://localhost:5154" }),
            NullLogger<AdminAuthService>.Instance);
    }

    private static EmailPayloadProtector CreateProtector()
    {
        var settings = Options.Create(new EmailSettings
        {
            Outbox = new EmailOutboxSettings
            {
                EncryptionKey = "Platform_Test_Outbox_Encryption_Key_Min32!"
            }
        });
        var config = new ConfigurationBuilder().Build();
        return new EmailPayloadProtector(settings, config, new FakeHostEnvironment());
    }

    private static UserManager<ApplicationUser> CreateUserManager(AppDbContext db)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(db);
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders()
            .Services
            .AddScoped<IUserStore<ApplicationUser>>(_ => new UserStore<ApplicationUser>(db));

        return services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "API.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
