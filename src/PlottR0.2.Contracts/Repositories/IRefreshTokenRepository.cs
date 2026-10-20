using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Repositories;

public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token, CancellationToken ct = default);
    Task<RefreshToken?> GetByHashedTokenAsync(string hashedToken, CancellationToken ct = default);

    Task<RefreshToken?> GetLatestActiveAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Revokes a single token if it is still active. The row count is the concurrency answer:
    /// 1 = this call rotated it, 0 = it was already revoked by a concurrent refresh.
    /// </summary>
    Task<int> RevokeAsync(Guid tokenId, CancellationToken ct = default);

    Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);
}
