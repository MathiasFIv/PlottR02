namespace PlottR0._2.Repository;

/// <summary>
/// Configuration for the workbook-backed rating store (design.md §5).
/// The input file is always the pristine, read-only source; the write target is a
/// freshly exported COPY written into <see cref="OutputDirectory"/> — the input is
/// never mutated.
/// </summary>
public sealed class ExcelWorkbookOptions
{
    public const string SectionName = "Excel";

    /// <summary>
    /// Path to the pristine source workbook (e.g. dropped into <c>Service/Input/</c>).
    /// Opened read-only by the repository.
    /// </summary>
    public string InputPath { get; set; } = string.Empty;

    /// <summary>
    /// Directory where exported output copies are written (e.g. <c>Service/Output/</c>).
    /// </summary>
    public string OutputDirectory { get; set; } = string.Empty;
}
