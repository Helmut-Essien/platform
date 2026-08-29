using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;

namespace Platform.Api.Extensions;

public static class DatabaseStartup
{
    public static async Task MigrateWithRetryAsync(
        AppDbContext db,
        ILogger logger,
        int maxAttempts = 10,
        CancellationToken cancellationToken = default)
    {
        var delay = TimeSpan.FromSeconds(3);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientStartupFailure(ex))
            {
                logger.LogWarning(
                    ex,
                    "Database migration attempt {Attempt}/{MaxAttempts} failed; retrying in {DelaySeconds}s.",
                    attempt,
                    maxAttempts,
                    delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 30));
            }
        }
    }

    private static bool IsTransientStartupFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.NpgsqlException or TimeoutException or IOException)
                return true;
        }

        return false;
    }
}
