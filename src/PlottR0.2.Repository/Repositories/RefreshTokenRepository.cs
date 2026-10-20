using Microsoft.EntityFrameworkCore;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Persistence;

namespace PlottR0._2.Repository.Repositories;

public sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly PlottR0DbContext _db;

    public RefreshTokenRepository(PlottR0DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task AddAsync(RefreshToken token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        _db.RefreshTokens.Add(token);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<RefreshToken?> GetByHashedTokenAsync(string hashedToken, CancellationToken ct = default)
    {
        return await _db.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.Token == hashedToken, ct);
    }

    public async Task<RefreshToken?> GetLatestActiveAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && !rt.IsRevoked && rt.ExpiresAt > now)
            .OrderByDescending(rt => rt.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    // Atomic single-use revoke: only an active row is updated, so 1 means this call won and 0 means
    // the token was already rotated away (a replay) — no read-then-write race.
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
