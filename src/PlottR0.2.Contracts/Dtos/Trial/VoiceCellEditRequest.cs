namespace PlottR0._2.Contracts.Dtos.Trial;

/// <summary>
/// Service-facing primitives for the voice cell edit
/// (<c>POST /api/Trials/{trialId}/cells/voice</c>): the addressed
/// <see cref="Plot"/>/<see cref="Subsample"/>/<see cref="TraitOrdinal"/> cell the caller
/// wants a spoken rating written into. The VALUE is not part of this request — it comes
/// from transcribing the uploaded audio and parsing exactly one number out of it.
/// Mirrors <see cref="EditCellRequest"/> minus <c>Value</c>.
/// </summary>
public class VoiceCellEditRequest
{
    /// <summary>The plot number of the target cell (must be &gt; 0).</summary>
    public int Plot { get; set; }

    /// <summary>The 1-indexed subsample of the target cell (must be &gt; 0).</summary>
    public int Subsample { get; set; }

    /// <summary>The 1-indexed trait ordinal of the target cell (1..9; 1 → column C, 9 → column K).</summary>
    public int TraitOrdinal { get; set; }

    /// <summary>
    /// Opt-in to overwriting a cell that already holds a value. Default <see langword="true"/>:
    /// the voice flow is a deliberate "double-tap the cell, speak the number" action, so a
    /// filled cell is overwritten without a second confirmation round-trip. Set
    /// <see langword="false"/> to get the locked-overwrite refusal
    /// (<c>"cell_already_filled"</c>) instead.
    /// </summary>
    public bool Overwrite { get; set; } = true;
}
