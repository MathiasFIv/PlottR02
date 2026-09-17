using PlottR0._2.Contracts.Dtos.Trial;

namespace PlottR0._2.Services.Abstractions;

public interface ITrialService
{
    Task<TrialSummary> CreateTrialAsync(string fileName, Stream content, CancellationToken ct = default);
    Task<IReadOnlyList<TrialSummary>> ListTrialsAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolves the workbook to serve for a trial export/download — the newest
    /// accumulated <c>*_out_*.xlsx</c> copy if any syncs have happened, otherwise the
    /// uploaded original — and returns its absolute path plus download file name.
    /// </summary>
    /// <param name="trialId">The id of the trial whose workbook is downloaded.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The absolute on-disk file path and the download file name.</returns>
    /// <exception cref="KeyNotFoundException">
    /// The trial with <paramref name="trialId"/> does not exist.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The trial exists but no workbook file is left on disk.
    /// </exception>
    Task<WorkbookExport> GetWorkbookExportAsync(Guid trialId, CancellationToken ct = default);
}
