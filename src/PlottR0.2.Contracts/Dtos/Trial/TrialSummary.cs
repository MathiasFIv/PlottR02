namespace PlottR0._2.Contracts.Dtos.Trial;

/// <summary>
/// Row shape for the trials list (<c>GET /api/Trials</c>) and the create response
/// (<c>POST /api/Trials</c>). Frozen contract shared with the frontend: every property
/// is always present in the JSON payload (server defaults, never omitted).
/// </summary>
public sealed class TrialSummary
{
    public string TrialId { get; init; } = string.Empty;
    public string ExternalId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public Guid? CreatedBy { get; init; }

    /// <summary>
    /// Display name of the user who created this trial, resolved server-side from the
    /// <c>CreatedBy</c> FK. Null when the creator no longer exists — the FK is configured
    /// SetNull on delete — in which case the UI omits the "by" line. Serializes as
    /// <c>createdByName</c> and is always present in the payload (web JSON defaults emit
    /// <c>null</c> for a nullable reference; there is deliberately no JsonIgnore/omit
    /// condition).
    /// </summary>
    public string? CreatedByName { get; init; }

    /// <summary>
    /// True when the calling actor is personally assigned to this trial. Managers
    /// (system administrators, researchers, trial managers) see every trial and use this
    /// flag to badge the rows they are personally on; field workers only ever receive
    /// their own assigned trials, so the flag is true for every row they see (their UI
    /// hides the badge regardless). Serializes as <c>isAssignedToCurrentUser</c> and is
    /// never omitted.
    /// </summary>
    public bool IsAssignedToCurrentUser { get; init; }
}
