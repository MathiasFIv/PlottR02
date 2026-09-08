namespace PlottR0._2.Contracts.Ratings;

/// <summary>
/// A single resolved rating cell in the workbook. This is a persistence-neutral,
/// transport value type: it expresses <c>(plot, subsample, trait, value, cellAddress)</c>
/// without leaking any ClosedXML/EF types across the repository seam (design.md §7).
/// <c>Value</c> is <see langword="null"/> when the cell is currently empty
/// (the "empty ≠ 0" honesty rule, design.md Rule 2).
/// </summary>
public readonly record struct RatingCell(
    int Plot,
    int Subsample,
    int TraitOrdinal,
    int? Value,
    string CellAddress);
