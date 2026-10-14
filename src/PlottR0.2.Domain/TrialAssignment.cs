namespace PlottR0._2.Domain;

/// <summary>
/// Join entity linking a <see cref="User"/> to a <see cref="Trial"/> they are allowed
/// to work on. A user may be assigned to a given trial at most once (enforced by a
/// composite unique index over <see cref="TrialId"/> + <see cref="UserId"/>).
/// </summary>
public class TrialAssignment
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>FK to the assigned <see cref="Trial"/>.</summary>
    public Guid TrialId { get; set; }

    /// <summary>FK to the assigned <see cref="User"/>.</summary>
    public Guid UserId { get; set; }

    /// <summary>User ID of whoever created the assignment.</summary>
    public Guid AssignedBy { get; set; }

    /// <summary>UTC timestamp when the assignment was created.</summary>
    public DateTime AssignedAt { get; set; }
}
