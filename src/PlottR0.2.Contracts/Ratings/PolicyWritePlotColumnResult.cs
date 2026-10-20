namespace PlottR0._2.Contracts.Ratings;

public sealed class PolicyWritePlotColumnResult
{
    public bool PlotExists { get; init; }

    public IReadOnlyList<RatingCell> WrittenCells { get; init; } = [];

    public IReadOnlyList<RatingCell> SkippedPresentCells { get; init; } = [];

    public IReadOnlyList<int> MissingSubsampleIndices { get; init; } = [];

    public int OutOfTraitRangeValueCount { get; init; }

    // Absolute path of the copy this write exported; null when no copy was written (0 cells).
    public string? OutputPath { get; init; }
}