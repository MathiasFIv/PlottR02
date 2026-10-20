using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Repositories;

public interface IAuditLogRepository
{
    Task AddAsync(AuditLog entry, CancellationToken ct = default);

    Task<IReadOnlyList<AuditLog>> GetAllAsync(int? skip = null, int? take = null, CancellationToken ct = default);
}
