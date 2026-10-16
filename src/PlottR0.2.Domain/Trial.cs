namespace PlottR0._2.Domain;

/// <summary>
/// Represents an uploaded trial file.
/// The <see cref="CreatedBy"/> FK (inherited from <see cref="AuditableEntity"/>)
/// establishes ownership per design.md #13.
/// </summary>
public class Trial : AuditableEntity
{
    public Guid Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = "Created";
    public string? SourceWorkbookPath { get; set; }

    /// <summary>
    /// Users assigned to work on this trial. A researcher or trial-manager creator is
    /// included automatically; a system-administrator creator is not (admins sit above
    /// the assignment matrix).
    /// </summary>
    public List<TrialAssignment> Assignments { get; set; } = [];

    /// <summary>
    /// Navigation property to the <see cref="User"/> who created this trial.
    /// Backed by the inherited <see cref="AuditableEntity.CreatedBy"/> FK column.
    /// </summary>
    public User? CreatedByUser { get; set; }
}
