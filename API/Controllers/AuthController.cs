using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Platform.Api.Extensions;
using Platform.Api.Identity;
using Platform.Api.Services;
using Platform.Shared.Dtos.Auth;

namespace Platform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IJwtTokenService jwtTokenService,
    IAdminAuthService adminAuth) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.AuthLoginPolicy)]
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
            return Unauthorized(new { message = "Invalid email or password." });

        var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (!result.Succeeded)
            return Unauthorized(new { message = "Invalid email or password." });

        var roles = await userManager.GetRolesAsync(user);
        var response = jwtTokenService.CreateToken(user, roles);

        return Ok(response);
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.AuthLoginPolicy)]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        await adminAuth.RequestPasswordResetAsync(request.Email, cancellationToken);
        return Ok(new { message = "If an account exists for that email, a reset link has been sent." });
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.AuthLoginPolicy)]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await adminAuth.ResetPasswordAsync(
                request.Email,
                request.Token,
                request.NewPassword,
                cancellationToken);
            return Ok(new { message = "Password has been reset. You can sign in with your new password." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [Authorize(Policy = PlatformAuthorizationPolicies.AdminOnly)]
    [HttpGet("me")]
    public ActionResult<object> Me()
    {
        return Ok(new
        {
            User.Identity?.Name,
            Email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                ?? User.FindFirst("email")?.Value,
            Roles = User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList()
        });
    }
}
