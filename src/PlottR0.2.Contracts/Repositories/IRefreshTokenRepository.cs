using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Repositories;

/// <summary>
/// Persistence contract for <see cref="RefreshToken"/> records.
/// Refresh tokens are looked up by their hashed value, so the repository
/// receives the SHA-256 hash of the client-supplied token.
/// </summary>
public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token, CancellationToken ct = default);
    Task<RefreshToken?> GetByHashedTokenAsync(string hashedToken, CancellationToken ct = default);

    /// <summary>
    /// Returns the newest ACTIVE (non-revoked, non-expired) refresh token for the
    /// given user, or <c>null</c> when none exists. Used by the reuse-grace path to
    /// locate the live successor of a just-consumed token so a concurrent refresh
    /// race can be serviced without revoking the whole family.
    /// </summary>
    Task<RefreshToken?> GetLatestActiveAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Atomically revokes the active token with the given <paramref name="tokenId"/>,
    /// returning the number of rows actually revoked (1 = revoked now, 0 = the token
    /// was already revoked/used, i.e. a replay). The transition only happens when the
    /// row is currently NOT revoked, making rotation single-use without a TOCTOU race.
    /// </summary>
    Task<int> RevokeAsync(Guid tokenId, CancellationToken ct = default);

    /// <summary>
    /// Atomically revokes every active (non-revoked) token for the given
    /// <paramref name="userId"/> and returns how many were revoked. Idempotent: a
    /// repeat call simply affects zero rows. Used to kill an entire token family on
    /// a detected replay.
    /// </summary>
    Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);
}
