using PlottR0._2.Contracts.Dtos.Trial;

namespace PlottR0._2.Contracts.Ratings;

/// <summary>
/// Identity of one trait column (C..K) of the ratings sheet, derived from the workbook's
/// ARM rows 3 (Pest Code) and 8 (Part Rated). <see cref="TraitOrdinal"/> is 1-indexed;
/// the value index in <see cref="SheetRow.Values"/> is <c>TraitOrdinal - 1</c>.
/// </summary>
public sealed record SheetColumn(
    int TraitOrdinal,
    string Column,
    string? PestCode,
    string? PartRated,
    string Label);

/// <summary>
/// One key-present data row of the ratings sheet. <see cref="Row"/> is the Excel row number
/// (keeps the <c>C22</c> ↔ row 22 cross-reference). <see cref="Values"/> is index-aligned
/// with the region's columns: <c>values[i]</c> belongs to <c>columns[i].TraitOrdinal</c>.
/// <see langword="null"/> = empty or non-numeric text cell; <c>0</c> is a real value
/// (empty ≠ 0 — design.md Rule 2).
/// </summary>
public sealed record SheetRow(
    int Row,
    int Plot,
    int Subsample,
    IReadOnlyList<int?> Values);

/// <summary>
/// The parsed ratings sheet region: column identity strip plus every key-present data row
/// in workbook order, with the workbook-level ARM metadata strip read for display.
/// </summary>
public sealed record SheetRegion(
    IReadOnlyList<SheetColumn> Columns,
    IReadOnlyList<SheetRow> Rows)
{
    /// <summary>
    /// ARM metadata strip (rows 5, 6, 10, 11, 12, 20), display-only. It rides as an init
    /// property so the positional signature stays exactly <c>(Columns, Rows)</c> while the
    /// repository seam still has a channel for the strip (read-for-display approved 2026-10-07).
    /// </summary>
    public SheetMetadata? Metadata { get; init; }
}
