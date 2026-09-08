namespace PlottR0._2.Contracts.Ratings;

/// <summary>
/// Command to record a scoring value for a set of subsamples of ONE plot in ONE trait column.
/// All targeted subsamples receive the SAME value (design.md §6). A command never spans
/// traits/columns: to touch <c>T</c> traits you send <c>T</c> commands.
/// </summary>
public sealed class RecordSubsampleRatingsCommand
{
    public int Plot { get; init; }

    /// <summary>1-indexed trait ordinal (1..9) mapping to a single workbook column (design.md §5).</summary>
    public int TraitOrdinal { get; init; }

    /// <summary>Ordinal rating value, validated 0..10 (design.md §9).</summary>
    public int Value { get; init; }

    /// <summary>The 1-indexed subsample indices within the plot to score (e.g. {1,2,3,4}).</summary>
    public IReadOnlyList<int> SubsampleIndices { get; init; } = [];
}
