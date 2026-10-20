using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts.Dtos.Auth;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

public class AuthService : IAuthService
{
    // Two browser contexts (a PWA and an open tab) can race a refresh, so the loser presents a
    // token the winner just rotated away. Within this window, with a live successor present, that
    // is the benign race: rotate the successor and mint a fresh pair, preserving the family.
    // Outside it, or with no successor, it is real reuse — the whole family is revoked.
    private static readonly TimeSpan ReuseGracePeriod = TimeSpan.FromSeconds(30);

    private readonly IUserService _userService;
    private readonly ITokenService _tokenService;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly ILogger<AuthService> _logger;

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

    public async Task LogoutAsync(Guid userId, CancellationToken ct = default)
        => await _refreshTokenRepository.RevokeAllForUserAsync(userId, ct);

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

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}