using System.Security.Cryptography;
using System.Text;
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
    private readonly IUserService _userService;
    private readonly ITokenService _tokenService;
    private readonly IRefreshTokenRepository _refreshTokenRepository;

    /// <summary>
    /// Creates a new <see cref="AuthService"/>.
    /// </summary>
    /// <param name="userService">The user service contract for credential validation and profile reads.</param>
    /// <param name="tokenService">The token service contract for access/refresh token issuance.</param>
    /// <param name="refreshTokenRepository">The refresh-token repository contract for persistence.</param>
    public AuthService(
        IUserService userService,
        ITokenService tokenService,
        IRefreshTokenRepository refreshTokenRepository)
    {
        _userService = userService;
        _tokenService = tokenService;
        _refreshTokenRepository = refreshTokenRepository;
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
        if (stored.IsRevoked)
        {
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