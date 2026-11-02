using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Licenses;

public class ExtendTrialRequest
{
    /// <summary>New trial end date (must be later than the current one, within the trial cap).</summary>
    [Required(ErrorMessage = "New trial end date is required")]
    public DateTime? TrialEndsAt { get; set; }
}
