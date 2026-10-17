namespace PlottR0._2.Contracts.Dtos.Trial;

/// <summary>
/// Request payload for editing ONE ratings cell of a trial's sheet
/// (<c>POST /api/Trials/{trialId}/cells</c>): the addressed
/// <see cref="Plot"/>/<see cref="Subsample"/>/<see cref="TraitOrdinal"/> cell and the new
/// value. <see cref="Value"/> is nullable so a MISSING value is detected by the service and
/// rejected — it must never be silently defaulted to 0.
/// </summary>
public class EditCellRequest
{
    /// <summary>The plot number of the target cell (must be &gt; 0).</summary>
    public int Plot { get; set; }

    /// <summary>The 1-indexed subsample of the target cell (must be &gt; 0).</summary>
    public int Subsample { get; set; }

    /// <summary>The 1-indexed trait ordinal of the target cell (1..9; 1 → column C, 9 → column K).</summary>
    public int TraitOrdinal { get; set; }

    /// <summary>
    /// The new rating value (0..10). Nullable ON PURPOSE: a missing value is a client error,
    /// not a write of 0.
    /// </summary>
    public int? Value { get; set; }

    /// <summary>
    /// Opt-in to overwriting a cell that already holds a value. Default <see langword="false"/>:
    /// a filled cell is then REFUSED (reported back, not an error) so the UI can ask the user
    /// to confirm; a retry with <see langword="true"/> proceeds and records the prior value.
    /// </summary>
    public bool Overwrite { get; set; }
}
