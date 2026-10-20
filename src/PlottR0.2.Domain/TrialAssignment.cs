namespace PlottR0._2.Domain;

public class TrialAssignment
{
    public Guid Id { get; set; }

    public Guid TrialId { get; set; }

    public Guid UserId { get; set; }

    public Guid AssignedBy { get; set; }

    public DateTime AssignedAt { get; set; }
}
