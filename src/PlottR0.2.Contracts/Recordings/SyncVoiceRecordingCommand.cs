namespace PlottR0._2.Contracts.Recordings;

public sealed class SyncVoiceRecordingCommand
{
    public Guid ClientRecordingId { get; init; }

    public Guid TrialId { get; init; }

    public int? Plot { get; init; }

    public int? Subsample { get; init; }

    public int? DefaultPlot { get; init; }

    public int? DefaultSubsample { get; init; }

    public int TraitOrdinal { get; init; }

    public int? Value { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }
}
