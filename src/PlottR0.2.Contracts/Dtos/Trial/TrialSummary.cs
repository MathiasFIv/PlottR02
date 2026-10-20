namespace PlottR0._2.Contracts.Dtos.Trial;

public sealed class TrialSummary
{
    public string TrialId { get; init; } = string.Empty;
    public string ExternalId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public Guid? CreatedBy { get; init; }

    public string? CreatedByName { get; init; }

    public bool IsAssignedToCurrentUser { get; init; }
}
