using PlottR0._2.Contracts.Dtos.User;

namespace PlottR0._2.Services.Abstractions;

/// <summary>
/// Issues the short-lived signed access token (JWT) and the opaque, single-use
/// refresh token that backs it. The access token carries the calling user's
/// claims; the refresh token is pure entropy and is only ever persisted as a
/// SHA-256 hash by the caller.
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// Generates a signed access token (JWT) for the supplied user profile,
    /// carrying <c>sub</c>, <c>email</c>, <c>name</c>, optional <c>role</c>
    /// and a fresh <c>jti</c>, expiring per the configured access-token lifetime.
    /// </summary>
    /// <param name="user">The authenticated user profile to sign into the token.</param>
    /// <returns>The encoded JWT string.</returns>
    string GenerateAccessToken(UserResponse user);

    /// <summary>
    /// Generates a cryptographically random opaque refresh token (64 bytes,
    /// base64url-encoded). The plaintext is returned to the client only; it
    /// must be hashed before persistence.
    /// </summary>
    /// <returns>The plaintext refresh token.</returns>
    string GenerateRefreshToken();

    /// <summary>
    /// The configured lifetime, in days, of an issued refresh token.
    /// </summary>
    int RefreshTokenLifetimeDays { get; }
}