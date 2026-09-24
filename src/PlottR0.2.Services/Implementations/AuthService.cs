using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts.Dtos.Auth;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

/// <summary>
/// Concrete <see cref="IAuthService"/>. Talks only to the user service, token
/// service and the refresh-token repository contract — never to EF or concrete
/// persistence classes. Handles credential login, token-pair issuance and
/// refresh-token rotation with single-use semantics.
/// </summary>
public class AuthService : IAuthService
{
    /// <summary>
    /// How long a just-consumed (rotated/revoked) refresh token may be replayed and
    /// still be treated as a benign concurrent refresh. Two browser contexts sharing
    /// an origin (a PWA and a tab) both read the SAME single-use refresh token and
    /// both refresh when the access token expires; the first rotation revokes it, and
    /// the second legitimately re-presents a token that was consumed moments ago.
    /// Without this window that second presentation trips reuse detection and nukes
    /// the entire token family, logging the user out until re-login. Replay beyond
    /// this window — or without a live successor — genuinely indicates a leaked token
    /// and still revokes the family.
    /// </summary>
    private static readonly TimeSpan ReuseGracePeriod = TimeSpan.FromSeconds(30);

    private readonly IUserService _userService;
    private readonly ITokenService _tokenService;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly ILogger<AuthService> _logger;

    /// <summary>
    /// Creates a new <see cref="AuthService"/>.
    /// </summary>
    /// <param name="userService">The user service contract for credential validation and profile reads.</param>
    /// <param name="tokenService">The token service contract for access/refresh token issuance.</param>
    /// <param name="refreshTokenRepository">The refresh-token repository contract for persistence.</param>
    /// <param name="logger">The logger for security-relevant events (reuse detection, grace handling).</param>
    public AuthService(
        IUserService userService,
        ITokenService tokenService,
        IRefreshTokenRepository refreshTokenRepository,
        ILogger<AuthService> logger)
    {
        _userService = userService;
        _tokenService = tokenService;
        _refreshTokenRepository = refreshTokenRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _userService.LoginAsync(request, ct);

        var accessToken = _tokenService.GenerateAccessToken(user);
        var plainRefreshToken = _tokenService.GenerateRefreshToken();

        await _refreshTokenRepository.AddAsync(CreateRefreshTokenEntity(user.Id, plainRefreshToken), ct);

        return new LoginResponse
        {
            User = user,
            AccessToken = accessToken,
            RefreshToken = plainRefreshToken,
        };
    }

    /// <inheritdoc />
    public async Task<RefreshResponse> RefreshAsync(RefreshRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            throw new UnauthorizedAccessException("Invalid refresh token.");
        }

        var stored = await _refreshTokenRepository.GetByHashedTokenAsync(HashToken(request.RefreshToken), ct);
        if (stored is null)
        {
            throw new UnauthorizedAccessException("Invalid refresh token.");
        }

        // Reuse detection. A token that is already revoked should never be presented
        // again — if it was, the plaintext was reused, which means it (or a family
        // member) leaked. Revoke the user's ENTIRE token family, including any
        // freshly-minted successor the attacker may hold, then deny the refresh.
        //
        // EXCEPTION — grace-window tolerance: two browser contexts (a PWA and a tab)
        // sharing one origin can both read the SAME single-use refresh token and both
        // refresh when the access token expires; the first rotation revokes it, and the
        // second legitimately re-presents a token that was consumed moments ago. Reuse of
        // a token revoked within ReuseGracePeriod while a live successor exists is treated
        // as this benign race: the successor is rotated and a fresh pair minted, WITHOUT
        // revoking the family. Reuse beyond the grace window, or with no live successor,
        // remains a theft signal and revokes the family.
        if (stored.IsRevoked)
        {
            if (stored.RevokedAt is DateTime revokedAt
                && DateTime.UtcNow - revokedAt <= ReuseGracePeriod
                && await _refreshTokenRepository.GetLatestActiveAsync(stored.UserId, ct) is { } successor)
            {
                if (await _refreshTokenRepository.RevokeAsync(successor.Id, ct) == 1)
                {
                    var gracedUser = await _userService.GetResponseAsync(stored.UserId, ct);
                    if (gracedUser is not null)
                    {
                        var gracedAccessToken = _tokenService.GenerateAccessToken(gracedUser);
                        var gracedRefreshToken = _tokenService.GenerateRefreshToken();

                        await _refreshTokenRepository.AddAsync(
                            CreateRefreshTokenEntity(stored.UserId, gracedRefreshToken), ct);

                        _logger.LogWarning(
                            "Refresh token {TokenId} for user {UserId} was replayed {ReplayDelaySeconds:F1}s after " +
                            "revocation (within the {GraceSeconds}s grace window) while a live successor existed; " +
                            "treated as a concurrent refresh — successor {SuccessorId} rotated, family preserved.",
                            stored.Id, stored.UserId, (DateTime.UtcNow - revokedAt).TotalSeconds,
                            ReuseGracePeriod.TotalSeconds, successor.Id);

                        return new RefreshResponse
                        {
                            AccessToken = gracedAccessToken,
                            RefreshToken = gracedRefreshToken,
                        };
                    }
                }
            }

            _logger.LogWarning(
                "Refresh token {TokenId} for user {UserId} was replayed outside the reuse grace window " +
                "(revoked {RevokedAt:o}); revoking the entire token family.",
                stored.Id, stored.UserId, stored.RevokedAt);

            await _refreshTokenRepository.RevokeAllForUserAsync(stored.UserId, ct);
            throw new UnauthorizedAccessException("Invalid refresh token.");
        }

        if (stored.ExpiresAt <= DateTime.UtcNow)
        {
            throw new UnauthorizedAccessException("Invalid refresh token.");
        }

        // Rotation: the presented token is single-use. Atomically transition it from
        // active -> revoked ONLY if it is still active. If the affected count is 0 the
        // token was already revoked by a concurrent rotation (a replay race), so refuse
        // to mint a successor.
        var revoked = await _refreshTokenRepository.RevokeAsync(stored.Id, ct);
        if (revoked == 0)
        {
            throw new UnauthorizedAccessException("Invalid refresh token.");
        }

        var user = await _userService.GetResponseAsync(stored.UserId, ct);
        if (user is null)
        {
            throw new UnauthorizedAccessException("Invalid refresh token.");
        }

        var accessToken = _tokenService.GenerateAccessToken(user);
        var plainRefreshToken = _tokenService.GenerateRefreshToken();

        await _refreshTokenRepository.AddAsync(CreateRefreshTokenEntity(user.Id, plainRefreshToken), ct);

        return new RefreshResponse
        {
            AccessToken = accessToken,
            RefreshToken = plainRefreshToken,
        };
    }

    /// <inheritdoc />
    public async Task LogoutAsync(Guid userId, CancellationToken ct = default)
        => await _refreshTokenRepository.RevokeAllForUserAsync(userId, ct);

    /// <summary>
    /// Builds a persisted <see cref="RefreshToken"/> record for the given user.
    /// Only the SHA-256 hash of the plaintext token is ever stored — the client
    /// keeps the plaintext, the database keeps the hash.
    /// </summary>
    /// <param name="userId">The token owner.</param>
    /// <param name="plainToken">The plaintext refresh token issued to the client.</param>
    /// <returns>A ready-to-persist <see cref="RefreshToken"/> entity.</returns>
    private RefreshToken CreateRefreshTokenEntity(Guid userId, string plainToken)
        => new()
        {
            Id = Guid.NewGuid(),
            Token = HashToken(plainToken),
            UserId = userId,
            ExpiresAt = DateTime.UtcNow.AddDays(_tokenService.RefreshTokenLifetimeDays),
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false,
        };

    /// <summary>
    /// Deterministically hashes a plaintext refresh token for storage and lookup.
    /// </summary>
    /// <param name="token">The plaintext refresh token.</param>
    /// <returns>The uppercase hex SHA-256 digest.</returns>
    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}