namespace PlottR0._2.Contracts.Ratings;

/// <summary>
/// Result of a single-cell write into a trial's workbook (the inline "edit a sheet cell"
/// feature). Unlike <see cref="PolicyWritePlotColumnResult"/> — which offers a whole
/// utterance's values under the locked-overwrite policy — this addresses exactly ONE
/// <c>(plot, subsample, traitOrdinal)</c> cell and reports whether it was written, or was
/// refused because it already held a value and the caller did not opt into overwriting.
/// A refusal is NOT an error: it is how the UI learns it must ask the user to confirm.
/// </summary>
public sealed class SingleCellWriteResult
{
    /// <summary>
    /// <see cref="SkippedReason"/> value for a <c>(plot, subsample)</c> pair with no row in
    /// the workbook. No write and no overwrite decision was made.
    /// </summary>
    public const string RowNotFound = "row_not_found";

    /// <summary>
    /// <see cref="SkippedReason"/> value reserving the out-of-range trait case. The
    /// repository currently REJECTS <c>traitOrdinal</c> outside 1..9 with an
    /// <see cref="InvalidDataException"/> (defense in depth, mirroring the policy write)
    /// rather than returning this reason; the constant exists so the contract enumerates the
    /// complete set of skip outcomes for consumers.
    /// </summary>
    public const string TraitOutOfRange = "trait_out_of_range";

    /// <summary>
    /// <see cref="SkippedReason"/> value for a target cell that already holds a value
    /// (numeric — 0 included — or non-"missing" text) while the caller did NOT opt into
    /// overwriting. The write is refused so the UI can ask the user to confirm.
    /// </summary>
    public const string CellAlreadyFilled = "cell_already_filled";

    /// <summary>
    /// True when the target cell was written and a fresh output copy was exported. False for
    /// a refused/skipped write.
    /// </summary>
    public bool Written { get; init; }

    /// <summary>
    /// True when the write did not happen for a non-error reason (row missing, or the cell
    /// already filled and overwrite was not requested). Always the inverse of
    /// <see cref="Written"/>.
    /// </summary>
    public bool Skipped { get; init; }

    /// <summary>
    /// Why the write was skipped — exactly one of <see cref="RowNotFound"/>,
    /// <see cref="TraitOutOfRange"/> or <see cref="CellAlreadyFilled"/>, or
    /// <see langword="null"/> when <see cref="Written"/>.
    /// </summary>
    public string? SkippedReason { get; init; }

    /// <summary>
    /// The value that was in the target cell BEFORE the write, read without throwing on text
    /// (<see langword="null"/> for empty or text cells, the numeric value otherwise). Carried
    /// for an overwrite so the caller can record/undo the prior value.
    /// </summary>
    public int? PriorValue { get; init; }

    /// <summary>
    /// The A1 address of the target cell (e.g. "D45"), or an empty string when the
    /// <c>(plot, subsample)</c> row was not found (no cell was addressed).
    /// </summary>
    public string CellAddress { get; init; } = string.Empty;

    /// <summary>The addressed plot.</summary>
    public int Plot { get; init; }

    /// <summary>The addressed subsample.</summary>
    public int Subsample { get; init; }

    /// <summary>The addressed trait ordinal (1..9).</summary>
    public int TraitOrdinal { get; init; }

    /// <summary>The value the caller attempted to write (echoed for convenience).</summary>
    public int? Value { get; init; }
}
