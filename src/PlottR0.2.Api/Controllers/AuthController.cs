using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Contracts.Dtos.Auth;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Api.Controllers;

/// <summary>
/// Authentication endpoints. Thin controller — all credential verification and
/// token management is delegated to <see cref="IAuthService"/>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    /// <summary>
    /// Creates a new <see cref="AuthController"/>.
    /// </summary>
    /// <param name="authService">The authentication service contract to operate against.</param>
    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>
    /// Logs a user in by validating their email + password against the stored
    /// BCrypt hash and issuing a JWT access + refresh token pair.
    /// </summary>
    /// <param name="request">The login credentials.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the authenticated user's profile and tokens, or 401 on bad credentials.</returns>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _authService.LoginAsync(request, ct);
            return Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    /// <summary>
    /// Exchanges a valid refresh token for a new access + refresh token pair.
    /// The presented refresh token is revoked (rotation).
    /// </summary>
    /// <param name="request">The refresh token payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the new token pair, or 401 if the refresh token is invalid/expired/revoked.</returns>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(RefreshResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RefreshResponse>> Refresh(
        [FromBody] RefreshRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _authService.RefreshAsync(request, ct);
            return Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    /// <summary>
    /// Revokes all outstanding refresh tokens for the authenticated user,
    /// terminating all active sessions.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>204 No Content.</returns>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout(CancellationToken ct = default)
    {
        var sub = User.FindFirstValue("sub");
        if (sub is null || !Guid.TryParse(sub, out var userId))
            return Unauthorized();

        await _authService.LogoutAsync(userId, ct);
        return NoContent();
    }
}
