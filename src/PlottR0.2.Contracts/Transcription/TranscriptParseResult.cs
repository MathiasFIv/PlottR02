namespace PlottR0._2.Contracts.Transcription;

/// <summary>
/// Result of parsing a transcript against the constrained MVP grammar
/// (<c>"plot 101 subsample 3 value 7"</c>, the multi-value form
/// <c>"plot 101 subsample 1 values 3 6 1"</c>, bare <c>"101 3 7"</c>, word forms,
/// partial utterances). Parsing is format-only: fields are NOT range-validated
/// here — the sync service validates against the workbook later (design.md V2E).
/// </summary>
public sealed class TranscriptParseResult
{
    /// <summary>The parsed plot number, or <see langword="null"/> when the transcript did not state one.</summary>
    public int? Plot { get; init; }

    /// <summary>The parsed subsample index, or <see langword="null"/> when the transcript did not state one.</summary>
    public int? Subsample { get; init; }

    /// <summary>
    /// The parsed rating values: the words behind the <c>"value"</c>/<c>"values"</c> keyword
    /// (or the trailing bare numbers) fill the row's trait cells left-to-right starting at the
    /// recording's <c>traitOrdinal</c>. A single <c>"value"</c> yields exactly one entry.
    /// Empty when the transcript stated no value. 0 is valid data.
    /// </summary>
    public IReadOnlyList<int> Values { get; init; } = Array.Empty<int>();

    /// <summary>
    /// The first parsed value (<see cref="Values"/>[0]), or <see langword="null"/> when the
    /// transcript stated none. The single-value cell contract: stored rows and results carry
    /// this one number, which is also the value written to the recording's traitOrdinal column.
    /// </summary>
    public int? FirstValue => Values.Count > 0 ? Values[0] : null;

    /// <summary>
    /// Confidence of the parse in the range 0..1: 0.95 complete + fully labeled,
    /// 0.85 complete + bare, 0.75 complete with any word-form number, 0.5 partial,
    /// 0 when nothing was parsed.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// True when every field was extracted — the transcript addressed plot, subsample and
    /// at least one value.
    /// </summary>
    public bool IsComplete => Plot.HasValue && Subsample.HasValue && Values.Count > 0;
}