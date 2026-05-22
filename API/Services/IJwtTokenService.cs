using Platform.Api.Identity;
using Platform.Shared.Dtos.Auth;

namespace Platform.Api.Services;

public interface IJwtTokenService
{
    LoginResponse CreateToken(ApplicationUser user, IEnumerable<string> roles);
}
