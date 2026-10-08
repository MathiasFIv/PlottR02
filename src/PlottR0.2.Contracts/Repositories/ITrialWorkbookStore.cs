using PlottR0._2.Contracts.Dtos.Trial;

namespace PlottR0._2.Contracts.Repositories;

/// <summary>
/// Stores uploaded trial workbooks on disk. Resides in the Repository layer so
/// Services/Api stay file-system-agnostic. Relative paths are relative to the
/// repo-root Service/ directory.
/// </summary>
public interface ITrialWorkbookStore
{
    /// <summary>
    /// Saves workbook bytes for a trial under Service/Working/trialId/ and
    /// returns the RELATIVE path (e.g. "Working/trialId/doc.xlsx").
    /// Throws InvalidDataException if the content is not a valid xlsx workbook.
    /// </summary>
    Task<string> SaveAsync(Guid trialId, string fileName, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Best-effort deletes a previously saved workbook file.
    /// Used for cleanup when a downstream operation (e.g. DB insert) fails
    /// after the file was already written.
    /// </summary>
    /// <param name="relativePath">The relative path returned by <see cref="SaveAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Resolves the workbook to serve for a trial export/download/sheet read: the newest
    /// "*_out_*.xlsx" output copy in the trial's working directory if one exists
    /// (accumulated sync state), otherwise the trial's pristine source workbook — together
    /// with its file name and an OPAQUE version token computed from <c>FileInfo</c> only
    /// (never opening the file) INSIDE THE SAME CALL, so the path and its version can
    /// never skew (no TOCTOU).
    /// Returns null when the working directory exists but contains no xlsx and the
    /// source workbook no longer exists on disk, or when the resolved source path
    /// escapes the Service/ directory.
    /// </summary>
    ResolvedWorkbookExport? ResolveExport(Guid trialId, string sourceRelativePath);
}
