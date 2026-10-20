using PlottR0._2.Domain;

namespace PlottR0._2.Services.Abstractions;

public interface IAuditService
{
    Task LogAsync(
        string action,
        string entityType,
        string? entityId,
        Guid? actorUserId,
        string? actorName,
        string? details,
        CancellationToken ct = default);

    Task<IReadOnlyList<AuditLog>> GetRecentAsync(int count = 100, CancellationToken ct = default);
}