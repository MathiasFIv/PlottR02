namespace PlottR0._2.Contracts.Dtos.Trial;

/// <summary>
/// Request payload for replacing the complete set of users assigned to a trial.
/// An empty <see cref="UserIds"/> list is valid and clears every assignment.
/// </summary>
public class AssignTrialUsersRequest
{
    /// <summary>
    /// The full desired set of assigned user ids. Must be distinct; an empty list
    /// clears the trial's crew.
    /// </summary>
    public IReadOnlyList<Guid> UserIds { get; set; } = [];
}
