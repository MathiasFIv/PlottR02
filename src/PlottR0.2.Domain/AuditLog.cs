namespace PlottR0._2.Domain;

/// <summary>
/// Immutable, append-only audit log entry recording a critical mutation.
/// Entries are created but never updated or deleted. Not itself audited,
/// so it does not inherit <see cref="AuditableEntity"/> (uses its own Timestamp).
/// </summary>
public class AuditLog
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>UTC timestamp when the audited action occurred.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Short label for the action performed (e.g. <c>Trial.Create</c>).</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Type name of the entity that was acted upon.</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>String representation of the acted-upon entity's key, or <c>null</c> for global actions.</summary>
    public string? EntityId { get; set; }

    /// <summary>User ID of the actor, or <c>null</c> for system-initiated actions.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Display name of the actor at the time of the action, or <c>null</c>.</summary>
    public string? ActorName { get; set; }

    /// <summary>Optional free-text payload describing the change in detail.</summary>
    public string? Details { get; set; }
}
