using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

/// <summary>
/// Concrete <see cref="IAuditService"/> backed by <see cref="IAuditLogRepository"/>.
/// Writes are best-effort log-and-continue: a failed audit write is logged via
/// <see cref="ILogger"/> and never rethrown, so an audit failure can never block or
/// roll back the primary business operation (design.md auditability).
/// </summary>
public class AuditService : IAuditService
{
    private readonly IAuditLogRepository _repository;
    private readonly ILogger<AuditService> _logger;

    /// <summary>
    /// Creates a new <see cref="AuditService"/>.
    /// </summary>
    /// <param name="repository">The audit log repository contract to persist against.</param>
    /// <param name="logger">Logger used to record failed audit writes (log-and-continue).</param>
    public AuditService(IAuditLogRepository repository, ILogger<AuditService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never throws. Persistence failures are logged at Error and swallowed so the
    /// caller's business operation proceeds regardless of audit state.
    /// </remarks>
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
            // Log-and-continue: an audit write failure must never surface to the
            // caller. The business operation that triggered the audit has already
            // succeeded — losing the audit trail is recorded, not propagated.
            _logger.LogError(
                ex,
                "Failed to persist audit log entry for action {Action} on {EntityType} ({EntityId}); continuing without audit.",
                action, entityType, entityId);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditLog>> GetRecentAsync(int count = 100, CancellationToken ct = default)
        => _repository.GetAllAsync(take: count, ct: ct);
}