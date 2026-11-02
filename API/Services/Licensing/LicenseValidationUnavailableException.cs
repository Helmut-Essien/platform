using System.Data.Common;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore.Storage;

namespace Platform.Api.Services.Licensing;

/// <summary>The license could not be judged (database/cache down or too slow). Mapped to 503 + Retry-After.</summary>
public sealed class LicenseValidationUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>True for infrastructure failures (DB/Redis/network/timeouts), anywhere in the inner-exception chain.</summary>
    public static bool IsInfrastructureFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbException or TimeoutException or SocketException or RetryLimitExceededException
                || e.GetType().FullName?.StartsWith("StackExchange.Redis.", StringComparison.Ordinal) == true)
                return true;
        }
        return false;
    }
}
