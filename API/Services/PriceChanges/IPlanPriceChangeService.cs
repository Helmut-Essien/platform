using Platform.Shared.Dtos.ServiceProducts;

namespace Platform.Api.Services.PriceChanges;

public interface IPlanPriceChangeService
{
    /// <summary>Every non-revoked license on the plan with its current and new price, and whether it can change.</summary>
    Task<PlanPriceChangePreviewDto> PreviewAsync(string serviceProductId, string planId, PlanPriceChangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stores a pending price change on each chosen license (replacing any earlier pending one) and queues notices.</summary>
    Task<SchedulePlanPriceChangeResultDto> ScheduleAsync(string serviceProductId, string planId, SchedulePlanPriceChangeRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    /// <summary>A license's price changes, newest first (pending, applied and cancelled).</summary>
    Task<IReadOnlyList<LicensePriceChangeDto>> ListForLicenseAsync(string licenseId, CancellationToken cancellationToken = default);

    /// <summary>Cancels a pending change; tells the customer when the notice had already gone out.</summary>
    Task<LicensePriceChangeDto> CancelAsync(string licenseId, string priceChangeId, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    /// <summary>Queues the customer notices whose time has come (worker). Returns how many were queued.</summary>
    Task<int> QueueDueNoticesAsync(DateTime utcNow, CancellationToken cancellationToken = default);
}
