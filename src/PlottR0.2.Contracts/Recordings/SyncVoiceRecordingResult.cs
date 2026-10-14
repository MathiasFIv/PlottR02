using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Recordings;

/// <summary>
/// The outcome of one voice-recording sync: which cells were written, which were skipped
/// because they already hold a value (locked-overwrite policy), which requested subsamples
/// had no workbook row, the parsed transcript fields (when a parse ran), and whether a
/// human should review the result.
/// <para>
/// <see cref="Status"/> is one of the <see cref="VoiceRecordingStatus"/> constants
/// (<c>Written</c>, <c>SkippedPresent</c>, <c>Flagged</c>, <c>Replay</c>). A
/// <c>Replay</c> result carries empty write collections and an empty transcript because
/// no transcription or workbook work was performed.
/// </para>
/// </summary>
public sealed class SyncVoiceRecordingResult
{
    /// <summary>The PWA idempotency key this result answers for.</summary>
    public Guid ClientRecordingId { get; init; }

    /// <summary>The trial the recording was synced against.</summary>
    public Guid TrialId { get; init; }

    /// <summary>
    /// Terminal status of the recording: one of <see cref="VoiceRecordingStatus.Written"/>,
    /// <see cref="VoiceRecordingStatus.SkippedPresent"/>, <see cref="VoiceRecordingStatus.Flagged"/>
    /// or <see cref="VoiceRecordingStatus.Replay"/>.
    /// </summary>
    public string Status { get; init; } = VoiceRecordingStatus.Received;

    /// <summary>
    /// The resolved plot number that was (or would be) written: confirmed field from the
    /// command when present and positive, otherwise the parsed transcript value.
    /// <see langword="null"/> when neither source supplied a valid plot.
    /// </summary>
    public int? Plot { get; init; }

    /// <summary>
    /// The resolved subsample index that was (or would be) written: confirmed field from
    /// the command when present and positive, otherwise the parsed transcript value.
    /// <see langword="null"/> when neither source supplied a valid subsample.
    /// </summary>
    public int? Subsample { get; init; }

    /// <summary>
    /// <see langword="true"/> when the resolved plot and/or subsample came from the
    /// command's <see cref="SyncVoiceRecordingCommand.DefaultPlot"/> /
    /// <see cref="SyncVoiceRecordingCommand.DefaultSubsample"/> because the transcript (and
    /// the confirmed fields) omitted it/them. Lets the PWA surface which target parts were
    /// assumed rather than spoken.
    /// </summary>
    public bool UsedDefaultTarget { get; init; }

    /// <summary>
    /// The resolved rating value that was (or would be) written: confirmed field from the
    /// command (0 is a valid confirmed value), otherwise the parsed transcript value.
    /// <see langword="null"/> when neither source supplied a valid value.
    /// </summary>
    public int? Value { get; init; }

    /// <summary>
    /// The parsed plot number from the transcript, or <see langword="null"/> when no parse
    /// ran (replay / value gate) or the transcript did not state one.
    /// </summary>
    public int? ParsedPlot { get; init; }

    /// <summary>
    /// The parsed subsample index from the transcript, or <see langword="null"/> when no
    /// parse ran or the transcript did not state one.
    /// </summary>
    public int? ParsedSubsample { get; init; }

    /// <summary>
    /// The parsed rating value from the transcript, or <see langword="null"/> when no parse
    /// ran or the transcript did not state one.
    /// </summary>
    public int? ParsedValue { get; init; }

    /// <summary>
    /// True when the plot was found in the workbook. Always <see langword="false"/> for a
    /// <c>Replay</c>, under the value gate, or when the workbook was missing.
    /// </summary>
    public bool PlotExists { get; init; }

    /// <summary>
    /// The trait ordinal (workbook column) this outcome was committed under: the command's
    /// confirmed ordinal normalized by the service — absent, 0 and negative all report 1
    /// (never 0), while an explicit out-of-range value is echoed verbatim so the reviewer
    /// sees why the recording was flagged. A replay echoes the anchor recovered from the
    /// stored row, so it re-reports the committed column rather than the caller's.
    /// </summary>
    public int TraitOrdinal { get; init; }

    /// <summary>
    /// True when a human should review the outcome: the transcript was missing, unparsable or
    /// below the confidence threshold, disagreed with the confirmed fields, or the write was
    /// flagged/skipped. Always false for a <c>Replay</c>.
    /// </summary>
    public bool NeedsReview { get; init; }

    /// <summary>True when the idempotency key was already present and the sync short-circuited.</summary>
    public bool Replayed { get; init; }

    /// <summary>
    /// <see langword="true"/> when the incoming command carried no confirmed fields
    /// (plot, subsample, and value were all null/absent) so the write target was
    /// derived entirely from the transcript. <see langword="false"/> when at least
    /// one confirmed field was supplied by the caller.
    /// </summary>
    public bool WasTranscriptOnly { get; init; }

    /// <summary>UTC moment the sync was committed (row update / write decision).</summary>
    public DateTimeOffset SubmittedAt { get; init; }

    /// <summary>
    /// The transcript the sidecar produced, or an empty string when transcription failed,
    /// was skipped (value gate / replay), or carried no understandable speech.
    /// </summary>
    public string Transcript { get; init; } = string.Empty;

    /// <summary>
    /// The parse confidence in 0..1, or <see langword="null"/> when no parse ran
    /// (replay / value gate). 0 when a transcription failed or the transcript was empty
    /// (the parser produced no recognized fields).
    /// </summary>
    public double? Confidence { get; init; }

    /// <summary>The writing cells actually written (empty cells only — locked-overwrite policy).</summary>
    public IReadOnlyList<RatingCell> WrittenCells { get; init; } = [];

    /// <summary>
    /// Cells that already held a value and were therefore left untouched (their
    /// <see cref="RatingCell.Value"/> is the CURRENT content).
    /// </summary>
    public IReadOnlyList<RatingCell> SkippedPresentCells { get; init; } = [];

    /// <summary>The requested subsample indices that had no row in the workbook.</summary>
    public IReadOnlyList<int> MissingSubsampleIndices { get; init; } = [];

    /// <summary>
    /// Number of spoken values of the utterance that could not be written at all
    /// (fixed-column mode: past trait 9; category-block append mode: past the current
    /// Part Rated category's cells or a full region). Only &gt; 0 when the recording is flagged.
    /// </summary>
    public int OutOfTraitRangeValueCount { get; init; } = 0;
}