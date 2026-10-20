namespace PlottR0._2.Contracts.Ratings;

public readonly record struct RatingCell(
    int Plot,
    int Subsample,
    int TraitOrdinal,
    int? Value,
    string CellAddress);
