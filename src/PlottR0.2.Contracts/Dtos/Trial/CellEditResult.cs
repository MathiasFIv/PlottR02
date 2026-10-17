namespace PlottR0._2.Contracts.Dtos.Trial;

/// <summary>
/// Outcome of a single-cell sheet edit (<c>POST /api/Trials/{trialId}/cells</c>). Carries the
/// addressed cell, whether it was written or refused, the prior value, and the NEW opaque
/// <see cref="Version"/> of the workbook AFTER the write (a fresh <c>_out_</c> copy changes the
/// version, so the client can re-poll the sheet with it).
/// </summary>
/// <param name="TrialId">The edited trial's id.</param>
/// <param name="Plot">The addressed plot.</param>
/// <param name="Subsample">The addressed subsample.</param>
/// <param name="TraitOrdinal">The addressed trait ordinal (1..9).</param>
/// <param name="Value">The value the client requested (echoed).</param>
/// <param name="Written">True when the cell was written and a new output copy exported.</param>
/// <param name="Skipped">
/// True when the write did not happen for a non-error reason — either the cell already held a
/// value and <c>overwrite</c> was not requested, or the addressed row was not found.
/// </param>
/// <param name="SkippedReason">
/// Why the write was skipped — <c>"cell_already_filled"</c> or <c>"row_not_found"</c> — or
/// <see langword="null"/> when the cell was written.
/// </param>
/// <param name="PriorValue">
/// The value the cell held before the write (<see langword="null"/> when empty/text). Set on an
/// overwrite and on a filled-cell refusal so the UI can show/confirm the prior value.
/// </param>
/// <param name="CellAddress">The A1 address of the target cell (e.g. "D45"), or empty when the row was not found.</param>
/// <param name="Version">The workbook's opaque version AFTER the write.</param>
/// <param name="FileName">The workbook file name now served, or null when it could not be re-resolved.</param>
/// <param name="GeneratedAt">UTC timestamp of the response.</param>
public sealed record CellEditResult(
    string TrialId,
    int Plot,
    int Subsample,
    int TraitOrdinal,
    int? Value,
    bool Written,
    bool Skipped,
    string? SkippedReason,
    int? PriorValue,
    string CellAddress,
    string Version,
    string? FileName,
    DateTime GeneratedAt);
