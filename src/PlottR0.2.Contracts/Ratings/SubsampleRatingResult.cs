namespace PlottR0._2.Contracts.Ratings;

/// <summary>
/// Result of a scoring command: which cells were written, which requested subsamples were
/// left untouched (none in the happy path), and the guarantee that unmentioned cells were
/// preserved as-is (empty stays empty — design.md Rule 2).
/// </summary>
public sealed class SubsampleRatingResult
{
    public int Plot { get; init; }
    public int TraitOrdinal { get; init; }
    public int Value { get; init; }
    public IReadOnlyList<WrittenCellDto> WrittenCells { get; init; } = [];
    public IReadOnlyList<int> UntouchedSubsamples { get; init; } = [];

    /// <summary>
    /// True when every cell not explicitly addressed by the command was left exactly as it
    /// was (including empty cells staying empty). With one-trait-per-command semantics and
    /// per-subsample targeting this is always true unless an indexed subsample does not exist.
    /// </summary>
    public bool EmptyPreserved { get; init; } = true;
}
