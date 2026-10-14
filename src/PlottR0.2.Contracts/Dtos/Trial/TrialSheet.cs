using PlottR0._2.Contracts.Ratings;

namespace PlottR0._2.Contracts.Dtos.Trial;

/// <summary>
/// Workbook-level ARM metadata strip shown above the sheet — display-only, parsed straight
/// from the workbook cells. Values are NOT normalized (e.g. the rating date is
/// <c>16Jun2026</c>, not ISO-8601); consumers render them verbatim.
/// </summary>
public sealed record SheetMetadata(
    string? Crop,
    string? RatingDate,
    string? RatingType,
    string? Unit,
    string? SampleSize,
    string? SubsampleCount);

/// <summary>
/// 200 body of <c>GET /api/Trials/{trialId}/sheet</c> — the full payload, or the
/// unchanged fast path. Every field is REQUIRED-but-nullable: the server never omits
/// them, so an unchanged response carries <c>changed: false</c> with
/// <c>fileName/generatedAt/metadata/columns/rows</c> present as JSON nulls (global JSON
/// options stay untouched — plan §3).
/// </summary>
public sealed record TrialSheet(
    string TrialId,
    string Version,
    bool Changed,
    string? FileName,
    DateTime? GeneratedAt,
    IReadOnlyList<SheetColumn>? Columns,
    IReadOnlyList<SheetRow>? Rows,
    SheetMetadata? Metadata);
