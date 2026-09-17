namespace PlottR0._2.Contracts.Transcription;

/// <summary>
/// Result of parsing a transcript against the constrained MVP grammar
/// (<c>"plot 101 subsample 3 value 7"</c>, bare <c>"101 3 7"</c>, word forms,
/// partial utterances). Parsing is format-only: fields are NOT range-validated
/// here — the sync service validates against the workbook later (design.md V2E).
/// </summary>
public sealed class TranscriptParseResult
{
    /// <summary>The parsed plot number, or <see langword="null"/> when the transcript did not state one.</summary>
    public int? Plot { get; init; }

    /// <summary>The parsed subsample index, or <see langword="null"/> when the transcript did not state one.</summary>
    public int? Subsample { get; init; }

    /// <summary>The parsed rating value (0 is valid data), or <see langword="null"/> when not stated.</summary>
    public int? Value { get; init; }

    /// <summary>
    /// Confidence of the parse in the range 0..1: 0.95 complete + fully labeled,
    /// 0.85 complete + bare, 0.75 complete with any word-form number, 0.5 partial,
    /// 0 when nothing was parsed.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// True when every field was extracted — the transcript addressed plot, subsample and value.
    /// </summary>
    public bool IsComplete => Plot.HasValue && Subsample.HasValue && Value.HasValue;
}