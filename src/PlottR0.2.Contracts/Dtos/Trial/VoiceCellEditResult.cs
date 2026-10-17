namespace PlottR0._2.Contracts.Dtos.Trial;

/// <summary>
/// Outcome of a voice cell edit (<c>POST /api/Trials/{trialId}/cells/voice</c>): the audio
/// was transcribed server-side, ONE number was parsed out of the transcript, and — when a
/// number was understood — it was written into the addressed cell through the same
/// single-cell write path as <c>POST /api/Trials/{trialId}/cells</c>. Mirrors
/// <see cref="CellEditResult"/> plus the transcript and a flat <see cref="Status"/> string
/// so a "double-tap a cell → record → number is saved" client can react in one round-trip.
/// <para/>
/// A transcript with no parseable number is NOT an error: it is reported as
/// <see cref="StatusNotUnderstood"/> with <see cref="Written"/> false, so the UI can simply
/// ask the user to try again.
/// </summary>
/// <param name="TrialId">The edited trial's id.</param>
/// <param name="Plot">The addressed plot.</param>
/// <param name="Subsample">The addressed subsample.</param>
/// <param name="TraitOrdinal">The addressed trait ordinal (1..9).</param>
/// <param name="Value">
/// The parsed rating value that was written, or <see langword="null"/> when the transcript
/// carried no single understood number (<see cref="StatusNotUnderstood"/>).
/// </param>
/// <param name="Status">The outcome — exactly one of the <c>Status*</c> constants below.</param>
/// <param name="Written">True when the cell was written and a new output copy exported.</param>
/// <param name="Skipped">
/// True when the write did not happen for a non-error reason — the transcript was not
/// understood, the addressed row was not found, or the cell already held a value and
/// overwrite was not requested.
/// </param>
/// <param name="SkippedReason">
/// Why the underlying single-cell write was skipped — <c>"cell_already_filled"</c> or
/// <c>"row_not_found"</c> — or <see langword="null"/> when the cell was written or the
/// transcript was not understood.
/// </param>
/// <param name="PriorValue">
/// The value the cell held before the write (<see langword="null"/> when empty/text or when
/// no write was attempted).
/// </param>
/// <param name="CellAddress">The A1 address of the target cell (e.g. "D45"), or empty when
/// the row was not found or no write was attempted.</param>
/// <param name="Version">
/// The workbook's opaque version AFTER the write, or an empty string when no write was
/// attempted (nothing on disk changed, so there is no new version to poll with).
/// </param>
/// <param name="FileName">The workbook file name now served, or null when no write happened
/// or it could not be re-resolved.</param>
/// <param name="Transcript">The raw transcript the value was parsed from (verbatim, also on a
/// <see cref="StatusNotUnderstood"/> so the UI can show what was heard).</param>
/// <param name="GeneratedAt">UTC timestamp of the response.</param>
public sealed record VoiceCellEditResult(
    string TrialId,
    int Plot,
    int Subsample,
    int TraitOrdinal,
    int? Value,
    string Status,
    bool Written,
    bool Skipped,
    string? SkippedReason,
    int? PriorValue,
    string CellAddress,
    string Version,
    string? FileName,
    string Transcript,
    DateTime GeneratedAt)
{
    /// <summary><see cref="Status"/> value: the parsed value landed in the cell.</summary>
    public const string StatusWritten = "written";

    /// <summary><see cref="Status"/> value: the transcript carried no single understood
    /// number (nothing was parsed, or only a glued multi-digit repair — flagged for a retry,
    /// never written).</summary>
    public const string StatusNotUnderstood = "not_understood";

    /// <summary><see cref="Status"/> value: the addressed <c>(plot, subsample)</c> pair has
    /// no row in the workbook.</summary>
    public const string StatusRowNotFound = "row_not_found";

    /// <summary><see cref="Status"/> value: the target cell already held a value and
    /// overwrite was not requested.</summary>
    public const string StatusCellAlreadyFilled = "cell_already_filled";

    /// <summary><see cref="Status"/> value: the underlying write reported a skip for a
    /// reason outside the enumerated contract — defensive fallback, never expected.</summary>
    public const string StatusUnexpected = "unexpected";
}
