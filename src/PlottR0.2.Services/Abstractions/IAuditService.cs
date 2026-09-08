using PlottR0._2.Domain;

namespace PlottR0._2.Services.Abstractions;

/// <summary>
/// Business-level audit logging. Translates domain operations into structured
/// <see cref="AuditLog"/> entries and persists them.
/// </summary>
public interface IAuditService
{
    /// <summary>Records an audit event for a critical mutation.</summary>
    Task LogAsync(
        string action,
        string entityType,
        string? entityId,
        Guid? actorUserId,
        string? actorName,
        string? details,
        CancellationToken ct = default);

    /// <summary>Returns the most recent audit entries, newest first.</summary>
    Task<IReadOnlyList<AuditLog>> GetRecentAsync(int count = 100, CancellationToken ct = default);
}