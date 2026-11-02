namespace Platform.Shared.Dtos.Licenses;

public class MakeComplimentaryRequest
{
    /// <summary>Optional end of the free access; null = free until you change it.</summary>
    public DateTime? ExpiresAt { get; set; }
}
