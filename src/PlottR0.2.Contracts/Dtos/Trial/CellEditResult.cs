namespace PlottR0._2.Contracts.Dtos.Trial;

public sealed record CellEditResult(
    string TrialId,
    int Plot,
    int Subsample,
    int TraitOrdinal,
    int? Value,
    bool Written,
    bool Skipped,
    string? SkippedReason,
    int? PriorValue,
    string CellAddress,
    string Version,
    string? FileName,
    DateTime GeneratedAt);
