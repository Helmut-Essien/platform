using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Identity;
using Platform.Shared.Constants;

namespace Platform.Api.Data;

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
        }

        var settings = adminSeedOptions.Value;
        if (string.IsNullOrWhiteSpace(settings.Email))
            return;

        var existing = await userManager.FindByEmailAsync(settings.Email);
        if (existing is not null)
            return;

        if (string.IsNullOrWhiteSpace(settings.Password))
        {
            if (environment.IsDevelopment())
            {
                logger.LogWarning(
                    "AdminSeed:Password is not configured. Set AdminSeed:Password in appsettings.Development.json to create the admin user.");
            }

            return;
        }

        var user = new ApplicationUser
        {
            UserName = settings.Email,
            Email = settings.Email,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow
        };

        var createResult = await userManager.CreateAsync(user, settings.Password);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to create admin user: " + string.Join(", ", createResult.Errors.Select(e => e.Description)));
        }

        var roleAssignResult = await userManager.AddToRoleAsync(user, PlatformRoles.Admin);
        if (!roleAssignResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to assign Admin role: " + string.Join(", ", roleAssignResult.Errors.Select(e => e.Description)));
        }

        logger.LogInformation("Seeded admin user {Email}", settings.Email);
    }
}
