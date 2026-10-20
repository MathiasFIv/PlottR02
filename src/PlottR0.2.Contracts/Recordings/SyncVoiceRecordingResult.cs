using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Recordings;

public sealed class SyncVoiceRecordingResult
{
    public Guid ClientRecordingId { get; init; }

    public Guid TrialId { get; init; }

    public string Status { get; init; } = VoiceRecordingStatus.Received;

    public int? Plot { get; init; }

    public int? Subsample { get; init; }

    public bool UsedDefaultTarget { get; init; }

    public int? Value { get; init; }

    public int? ParsedPlot { get; init; }

    public int? ParsedSubsample { get; init; }

    public int? ParsedValue { get; init; }

    public bool PlotExists { get; init; }

    public int TraitOrdinal { get; init; }

    public bool NeedsReview { get; init; }

    public bool Replayed { get; init; }

    public bool WasTranscriptOnly { get; init; }

    public DateTimeOffset SubmittedAt { get; init; }

    public string Transcript { get; init; } = string.Empty;

    public string TranscriptionError { get; init; } = string.Empty;

    public double? Confidence { get; init; }

    public IReadOnlyList<RatingCell> WrittenCells { get; init; } = [];

    public IReadOnlyList<RatingCell> SkippedPresentCells { get; init; } = [];

    public IReadOnlyList<int> MissingSubsampleIndices { get; init; } = [];

    public int OutOfTraitRangeValueCount { get; init; } = 0;
}