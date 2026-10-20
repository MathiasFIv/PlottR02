namespace PlottR0._2.Contracts.Dtos.Trial;

public class AssignTrialUsersRequest
{
    public IReadOnlyList<Guid> UserIds { get; set; } = [];
}
