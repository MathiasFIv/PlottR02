using PlottR0._2.Contracts.Dtos.Auth;
using PlottR0._2.Contracts.Dtos.User;

namespace PlottR0._2.Services.Abstractions;

/// <summary>
/// Authentication workflow: credential login, refresh-token rotation and
/// logout. Orchestrates <see cref="IUserService"/> for credential validation /
/// profile reads, <see cref="ITokenService"/> for token issuance and
/// <see cref="Contracts.Repositories.IRefreshTokenRepository"/> for persisting
/// the hashed refresh tokens.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Validates credentials and issues a token pair (access + refresh).
    /// </summary>
    /// <param name="request">The login credentials. Must not be <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The authenticated user plus the fresh token pair.</returns>
    /// <exception cref="UnauthorizedAccessException">When the credentials are invalid.</exception>
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);

    /// <summary>
    /// Exchanges a valid refresh token for a new token pair. The presented
    /// token is revoked (rotation) and a new one is issued.
    /// </summary>
    /// <param name="request">The refresh token payload. Must not be <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The refreshed token pair.</returns>
    /// <exception cref="UnauthorizedAccessException">When the refresh token is invalid, revoked,
    /// expired, or its user no longer exists.</exception>
    Task<RefreshResponse> RefreshAsync(RefreshRequest request, CancellationToken ct = default);

    /// <summary>
    /// Revokes every outstanding refresh token issued to the user, terminating
    /// all of their active sessions.
    /// </summary>
    /// <param name="userId">The user whose tokens should be revoked.</param>
    /// <param name="ct">Cancellation token.</param>
    Task LogoutAsync(Guid userId, CancellationToken ct = default);
}