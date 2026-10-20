using PlottR0._2.Contracts.Dtos.Trial;

namespace PlottR0._2.Contracts.Ratings;

public sealed record SheetColumn(
    int TraitOrdinal,
    string Column,
    string? PestCode,
    string? PartRated,
    string Label);

public sealed record SheetRow(
    int Row,
    int Plot,
    int Subsample,
    IReadOnlyList<int?> Values);

public sealed record SheetRegion(
    IReadOnlyList<SheetColumn> Columns,
    IReadOnlyList<SheetRow> Rows)
{
    public SheetMetadata? Metadata { get; init; }
}
