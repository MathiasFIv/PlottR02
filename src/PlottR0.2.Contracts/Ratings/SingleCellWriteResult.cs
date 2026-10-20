namespace PlottR0._2.Contracts.Ratings;

public sealed class SingleCellWriteResult
{
    public const string RowNotFound = "row_not_found";

    public const string TraitOutOfRange = "trait_out_of_range";

    public const string CellAlreadyFilled = "cell_already_filled";

    public bool Written { get; init; }

    public bool Skipped { get; init; }

    public string? SkippedReason { get; init; }

    public int? PriorValue { get; init; }

    public string CellAddress { get; init; } = string.Empty;

    public int Plot { get; init; }

    public int Subsample { get; init; }

    public int TraitOrdinal { get; init; }

    public int? Value { get; init; }

    // Absolute path of the copy this write exported; null when the write was skipped or refused.
    public string? OutputPath { get; init; }
}
