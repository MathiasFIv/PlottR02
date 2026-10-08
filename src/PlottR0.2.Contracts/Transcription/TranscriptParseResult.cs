namespace PlottR0._2.Contracts.Transcription;

/// <summary>
/// Result of parsing a transcript against the constrained MVP grammar
/// (<c>"plot 101 subsample 3 value 7"</c>, the multi-value form
/// <c>"plot 101 subsample 1 values 3 6 1"</c>, a keyword-free values-only utterance
/// <c>"3, 6, 1"</c> where every number lands in <see cref="Values"/> while
/// <see cref="Plot"/>/<see cref="Subsample"/> stay <see langword="null"/>, word forms,
/// partial utterances). Parsing is format-only: fields are NOT range-validated
/// here — the sync service validates against the workbook later (design.md V2E), and the
/// ceiling-bounded glued-digit repair (<see cref="Repaired"/>) re-splits digits without
/// deciding legality either.
/// </summary>
public sealed class TranscriptParseResult
{
    /// <summary>The parsed plot number, or <see langword="null"/> when the transcript did not state one.</summary>
    public int? Plot { get; init; }

    /// <summary>The parsed subsample index, or <see langword="null"/> when the transcript did not state one.</summary>
    public int? Subsample { get; init; }

    /// <summary>
    /// The parsed rating values: the numbers behind the <c>"value"</c>/<c>"values"</c>
    /// keyword fill the row's trait cells left-to-right starting at the recording's
    /// <c>traitOrdinal</c>. A KEYWORD-FREE transcript puts ALL of its numbers here too —
    /// <c>"3, 6, 1"</c> ⇒ [3, 6, 1], a single <c>"7"</c> ⇒ [7] — which is how an operator
    /// continues a run without repeating the word "values" (plot/subsample then come from
    /// the client's default target, not from the parse). A single <c>"value"</c> yields
    /// exactly one entry. Empty when the transcript stated no value. 0 is valid data.
    /// A glued token in value position arrives here already REPAIRED into its digits
    /// (<see cref="Repaired"/>), so this list always holds what would be written.
    /// </summary>
    public IReadOnlyList<int> Values { get; init; } = Array.Empty<int>();

    /// <summary>
    /// The first parsed value (<see cref="Values"/>[0]), or <see langword="null"/> when the
    /// transcript stated none. The single-value cell contract: stored rows and results carry
    /// this one number, which is also the value written to the recording's traitOrdinal
    /// column. After a glued-digit repair this is the REPAIRED first digit (3 for
    /// <c>"Values 398"</c>), so it agrees with what the cells actually received.
    /// </summary>
    public int? FirstValue => Values.Count > 0 ? Values[0] : null;

    /// <summary>
    /// Confidence of the parse in the range 0..1: 0.95 complete + fully labeled,
    /// 0.85 complete + bare (band retained in the parser but unreachable — a keyword-free
    /// transcript is values-only, so it never states plot/subsample and can never be
    /// complete), 0.75 complete with any word-form number, 0.5 partial (EVERY
    /// keyword-free values-only utterance lands here), 0 when nothing was parsed.
    /// A REPAIRED parse (<see cref="Repaired"/>) is capped at 0.70 — below the sync
    /// service's <c>ReviewConfidenceThreshold</c> (0.75) — so it always needs review while
    /// the write still proceeds.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// True when the parser repaired a glued digit run in VALUE position: Whisper dropped
    /// the separators between spoken digits, so one numeric token carried several ratings
    /// (<c>"plot 101 subsamble 1.Values 398"</c> ⇒ <see cref="Values"/> [3, 9, 8] instead of
    /// the out-of-range [398]). The repair is bounded (3..<c>MaxSplitDigits</c> digits,
    /// above <c>TranscriptParser.ValueCeiling</c> or starting with a zero, values only —
    /// never plot/subsample) and is flagged by capping <see cref="Confidence"/> at 0.70.
    /// Deliberately NOT persisted to the database and NOT echoed on
    /// <c>SyncVoiceRecordingResult</c>: the raw transcript is stored verbatim on the
    /// recording row and echoed, so the owner always sees the true
    /// <c>"... Values 398"</c> Whisper returned next to the cells that were written.
    /// </summary>
    public bool Repaired { get; init; }

    /// <summary>
    /// True when every field was extracted — the transcript addressed plot, subsample and
    /// at least one value.
    /// </summary>
    public bool IsComplete => Plot.HasValue && Subsample.HasValue && Values.Count > 0;
}