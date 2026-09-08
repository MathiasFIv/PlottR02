namespace PlottR0._2.Domain;

/// <summary>
/// Base class for entities that track creation and modification metadata.
/// Provides consistent audit timestamps and actor attribution.
/// Pure POCO — no persistence attributes, no interfaces (design.md §3).
/// </summary>
public abstract class AuditableEntity
{
    /// <summary>UTC timestamp when this entity was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp when this entity was last modified.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>User ID of the creator, or <c>null</c> for system-seeded data.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>User ID of the last modifier, or <c>null</c> if unchanged since creation.</summary>
    public Guid? UpdatedBy { get; set; }
}
