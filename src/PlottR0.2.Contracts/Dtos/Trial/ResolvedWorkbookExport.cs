namespace PlottR0._2.Contracts.Dtos.Trial;

/// <summary>
/// The workbook resolved for a trial export/download/sheet read: the absolute on-disk
/// <see cref="FilePath"/>, the download <see cref="FileName"/>, and the opaque
/// <see cref="Version"/> token (echoed back as <c>?since=</c>) — computed inside the SAME
/// resolution call from <c>FileInfo</c> only, so path and version can never skew (no TOCTOU).
/// </summary>
public sealed record ResolvedWorkbookExport(
    string FilePath,
    string FileName,
    string Version);
