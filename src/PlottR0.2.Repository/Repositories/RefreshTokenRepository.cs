using Microsoft.EntityFrameworkCore;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Persistence;

namespace PlottR0._2.Repository.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IRefreshTokenRepository"/> backed by
/// PostgreSQL via <see cref="PlottR0DbContext"/>.
/// </summary>
public sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly PlottR0DbContext _db;

    /// <summary>
    /// Initialises a new instance of the <see cref="RefreshTokenRepository"/> class.
    /// </summary>
    /// <param name="db">The application's EF Core <see cref="PlottR0DbContext"/>.</param>
    public RefreshTokenRepository(PlottR0DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task AddAsync(RefreshToken token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        _db.RefreshTokens.Add(token);
        await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<RefreshToken?> GetByHashedTokenAsync(string hashedToken, CancellationToken ct = default)
    {
        return await _db.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.Token == hashedToken, ct);
    }

    /// <inheritdoc />
    public async Task<RefreshToken?> GetLatestActiveAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && !rt.IsRevoked && rt.ExpiresAt > now)
            .OrderByDescending(rt => rt.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Atomically revokes the token identified by <paramref name="tokenId"/>,
    /// but ONLY if it is not already revoked. Executed as a single database
    /// UPDATE so concurrent rotations of the same token cannot both succeed
    /// (prevents the single-use/TOCTOU replay race). The affected count is
    /// returned so callers can distinguish "revoked now" (1) from "already
    /// revoked / already used" (0, i.e. a replay).
    /// </summary>
    public async Task<int> RevokeAsync(Guid tokenId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return await _db.RefreshTokens
            .Where(rt => rt.Id == tokenId && !rt.IsRevoked)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(rt => rt.IsRevoked, true)
                    .SetProperty(rt => rt.RevokedAt, now),
                ct);
    }

    /// <summary>
    /// Atomically revokes every active token belonging to <paramref name="userId"/>.
    /// Uses a single database UPDATE restricted to non-revoked rows, so the call
    /// is idempotent — a repeated invocation simply affects zero rows. Used to
    /// kill an entire token family on a detected replay.
    /// </summary>
    public async Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && !rt.IsRevoked)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(rt => rt.IsRevoked, true)
                    .SetProperty(rt => rt.RevokedAt, now),
                ct);
    }
}
