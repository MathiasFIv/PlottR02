namespace PlottR0._2.Contracts.Ratings;

/// <summary>
/// Result of a policy-aware (locked-overwrite) rating write: which cells were written,
/// which cells were skipped because they ALREADY hold a value (0 included), and which
/// requested subsamples have no row in the workbook. Unmentioned cells are preserved
/// as-is (design.md Rule 2 — empty stays empty, 0 stays 0).
/// </summary>
public sealed class PolicyWritePlotColumnResult
{
    /// <summary>
    /// True when the workbook contained at least one "Subsample Plot Data" row for the
    /// requested plot. False when no row in column B carries the plot's composite key.
    /// </summary>
    public bool PlotExists { get; init; }

    /// <summary>
    /// The rating cells this call actually wrote (empty or "missing" cells only). Cells
    /// appear in ascending subsample order, matching the iteration order of the requested
    /// indices.
    /// </summary>
    public IReadOnlyList<RatingCell> WrittenCells { get; init; } = [];

    /// <summary>
    /// Cells skipped because they were already present: a numeric value (0 included) or
    /// non-"missing" text. The reported <see cref="RatingCell.Value"/> holds the cell's
    /// CURRENT content, read without throwing on text — <see langword="null"/> for text
    /// cells, the numeric value otherwise.
    /// </summary>
    public IReadOnlyList<RatingCell> SkippedPresentCells { get; init; } = [];

    /// <summary>
    /// The requested subsample indices (ascending) for which the workbook had no row for
    /// the plot. No write and no skip decision was made for these.
    /// </summary>
    public IReadOnlyList<int> MissingSubsampleIndices { get; init; } = [];

    /// <summary>
    /// Number of values that could NOT be written at all in the chosen mode. Fixed-column
    /// mode: a value whose column would exceed trait 9 (<c>traitOrdinal + i &gt; 9</c>).
    /// Append mode: a value that had no free cell left inside the CURRENT category block — or
    /// a region with no free cell at all. These values were NOT written and should trigger
    /// review (an utterance never spans category blocks; it is dropped and flagged instead).
    /// </summary>
    public int OutOfTraitRangeValueCount { get; init; }
}