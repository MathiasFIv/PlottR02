namespace PlottR0._2.Contracts.Ratings;

/// <summary>
/// A single rating cell that was written by a scoring command. The address is diagnostic
/// only; the authoritative domain key is <c>(plot, subsample, trait)</c> (design.md §6).
/// </summary>
public sealed class WrittenCellDto
{
    public int Subsample { get; init; }
    public string CellAddress { get; init; } = string.Empty;
    public int Value { get; init; }
}
