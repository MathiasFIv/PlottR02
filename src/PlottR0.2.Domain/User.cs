namespace PlottR0._2.Domain;

public class User : AuditableEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public UserRole? Role { get; set; }
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// Trials this user is assigned to work on.
    /// </summary>
    public List<TrialAssignment> Assignments { get; set; } = [];
}
