using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Identity;
using Platform.Shared.Constants;

namespace Platform.Api.Data;

/// <summary>
/// Seeds the Admin role and a single bootstrap account for the current environment.
/// Development uses the demo credentials from appsettings.Development.json;
/// Production uses live credentials supplied only via secrets / environment variables.
/// </summary>
public static class IdentitySeedData
{
    public static async Task SeedAdminAsync(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<AdminSeedSettings> adminSeedOptions,
        IHostEnvironment environment,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!await roleManager.RoleExistsAsync(PlatformRoles.Admin))
        {
            var roleResult = await roleManager.CreateAsync(new IdentityRole(PlatformRoles.Admin));
            if (!roleResult.Succeeded)
            {
                throw new InvalidOperationException(
                    "Failed to create Admin role: " + string.Join(", ", roleResult.Errors.Select(e => e.Description)));
            }

            logger.LogInformation("Created Identity role {Role}", PlatformRoles.Admin);
        }

        var settings = adminSeedOptions.Value;
        var email = settings.Email?.Trim() ?? string.Empty;
        var accountKind = environment.IsDevelopment() ? "demo" : "live";

        if (string.IsNullOrWhiteSpace(email))
        {
            if (environment.IsDevelopment())
            {
                logger.LogWarning(
                    "AdminSeed:Email is empty. Set AdminSeed in appsettings.Development.json to create the demo Admin account.");
            }
            else
            {
                logger.LogInformation(
                    "AdminSeed:Email is empty. No live Admin account will be seeded. " +
                    "Set AdminSeed__Email and AdminSeed__Password via production secrets to bootstrap the first login.");
            }

            return;
        }

        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            logger.LogDebug(
                "Bootstrap {AccountKind} Admin account {Email} already exists; leaving password unchanged.",
                accountKind,
                email);
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.Password))
        {
            if (environment.IsDevelopment())
            {
                logger.LogWarning(
                    "AdminSeed:Password is empty. Set AdminSeed:Password in appsettings.Development.json " +
                    "to create the demo Admin account ({Email}).",
                    email);
            }
            else
            {
                logger.LogWarning(
                    "AdminSeed:Password is empty for {Email}. " +
                    "Set AdminSeed__Password via production secrets to create the live Admin account. " +
                    "No password was committed in app configuration.",
                    email);
            }

            return;
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow
        };

        var createResult = await userManager.CreateAsync(user, settings.Password);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to create {accountKind} Admin user: " +
                string.Join(", ", createResult.Errors.Select(e => e.Description)));
        }

        var roleAssignResult = await userManager.AddToRoleAsync(user, PlatformRoles.Admin);
        if (!roleAssignResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to assign Admin role to {accountKind} user: " +
                string.Join(", ", roleAssignResult.Errors.Select(e => e.Description)));
        }

        logger.LogInformation(
            "Seeded {AccountKind} Admin account {Email} (password is not logged).",
            accountKind,
            email);
    }
}
