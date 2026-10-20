using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

public class AuditService : IAuditService
{
    private readonly IAuditLogRepository _repository;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IAuditLogRepository repository, ILogger<AuditService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Never throws: a failed audit write is logged and swallowed so the business operation
    /// continues rather than failing on the audit trail.
    /// </summary>
    public async Task LogAsync(
        string action,
        string entityType,
        string? entityId,
        Guid? actorUserId,
        string? actorName,
        string? details,
        CancellationToken ct = default)
    {
        var entry = new AuditLog
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            ActorUserId = actorUserId,
            ActorName = actorName,
            Details = details,
        };

        try
        {
            await _repository.AddAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to persist audit log entry for action {Action} on {EntityType} ({EntityId}); continuing without audit.",
                action, entityType, entityId);
        }
    }

    public Task<IReadOnlyList<AuditLog>> GetRecentAsync(int count = 100, CancellationToken ct = default)
        => _repository.GetAllAsync(take: count, ct: ct);
}