using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Repositories;

/// <summary>
/// Persistence contract for <see cref="AuditLog"/> entries.
/// Append-only: write once, read many. Deliberately does NOT extend
/// IRepository&lt;AuditLog,Guid&gt; — mutation-by-id is meaningless for an immutable log.
/// </summary>
public interface IAuditLogRepository
{
    /// <summary>Persists a new audit log entry.</summary>
    Task AddAsync(AuditLog entry, CancellationToken ct = default);

    /// <summary>
    /// Returns audit log entries ordered newest-first, with optional paging.
    /// </summary>
    Task<IReadOnlyList<AuditLog>> GetAllAsync(int? skip = null, int? take = null, CancellationToken ct = default);
}
