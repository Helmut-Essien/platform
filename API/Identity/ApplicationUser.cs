using Microsoft.AspNetCore.Identity;

namespace Platform.Api.Identity;

public class ApplicationUser : IdentityUser
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
