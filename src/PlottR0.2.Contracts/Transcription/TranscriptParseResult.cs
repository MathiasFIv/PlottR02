namespace PlottR0._2.Contracts.Transcription;

public sealed class TranscriptParseResult
{
    public int? Plot { get; init; }

    public int? Subsample { get; init; }

    public IReadOnlyList<int> Values { get; init; } = Array.Empty<int>();

    public int? FirstValue => Values.Count > 0 ? Values[0] : null;

    public double Confidence { get; init; }

    public bool Repaired { get; init; }

    public bool IsComplete => Plot.HasValue && Subsample.HasValue && Values.Count > 0;
}