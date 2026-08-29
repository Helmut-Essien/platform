using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Identity;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Xunit;

namespace API.Tests;

public class AdminAuthServiceTests
{
    [Fact]
    public async Task RequestPasswordResetAsync_DoesNotThrowForUnknownEmail()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new CapturingEmailSender());

        var exception = await Record.ExceptionAsync(() =>
            service.RequestPasswordResetAsync("missing@example.test"));

        Assert.Null(exception);
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

        var service = CreateService(db, new CapturingEmailSender());

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

        var service = CreateService(db, new CapturingEmailSender());
        await service.ResetPasswordAsync(user.Email!, encoded, "NewPassword2!");

        Assert.True(await userManager.CheckPasswordAsync(user, "NewPassword2!"));
    }

    private static AdminAuthService CreateService(AppDbContext db, IEmailSender sender)
    {
        var userManager = CreateUserManager(db);
        return new AdminAuthService(
            userManager,
            sender,
            new EmailTemplateService(),
            Options.Create(new AuthSettings { ClientBaseUrl = "http://localhost:5154" }),
            NullLogger<AdminAuthService>.Instance);
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

    private sealed class CapturingEmailSender : IEmailSender
    {
        public Task SendAsync(
            string toEmail,
            string subject,
            string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
