using PlottR0._2.Contracts.Ratings;

namespace PlottR0._2.Contracts.Dtos.Trial;

public sealed record SheetMetadata(
    string? Crop,
    string? RatingDate,
    string? RatingType,
    string? Unit,
    string? SampleSize,
    string? SubsampleCount);

public sealed record TrialSheet(
    string TrialId,
    string Version,
    bool Changed,
    string? FileName,
    DateTime? GeneratedAt,
    IReadOnlyList<SheetColumn>? Columns,
    IReadOnlyList<SheetRow>? Rows,
    SheetMetadata? Metadata);
