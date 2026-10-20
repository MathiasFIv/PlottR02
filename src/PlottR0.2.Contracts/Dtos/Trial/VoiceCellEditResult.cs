namespace PlottR0._2.Contracts.Dtos.Trial;

public sealed record VoiceCellEditResult(
    string TrialId,
    int Plot,
    int Subsample,
    int TraitOrdinal,
    int? Value,
    string Status,
    bool Written,
    bool Skipped,
    string? SkippedReason,
    int? PriorValue,
    string CellAddress,
    string Version,
    string? FileName,
    string Transcript,
    DateTime GeneratedAt)
{
    public const string StatusWritten = "written";

    public const string StatusNotUnderstood = "not_understood";

    public const string StatusRowNotFound = "row_not_found";

    public const string StatusCellAlreadyFilled = "cell_already_filled";

    public const string StatusUnexpected = "unexpected";
}
