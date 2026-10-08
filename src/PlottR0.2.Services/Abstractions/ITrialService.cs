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

    /// <summary>
    /// Reads the trial's ratings sheet for the read-only sheet endpoint
    /// (<c>GET /api/Trials/{trialId}/sheet?since={version}</c>): resolves the current
    /// workbook and, when <paramref name="since"/> matches its opaque version, returns the
    /// unchanged fast-path payload WITHOUT opening the workbook. Orchestration only — the
    /// region read itself is delegated to <c>IRatingRepository.ReadSheetRegion</c>.
    /// </summary>
    /// <param name="trialId">The id of the trial whose sheet is read.</param>
    /// <param name="since">
    /// The last-seen version echoed back by the client. A non-matching value — including
    /// malformed input, which is deliberately IGNORED rather than rejected — returns the
    /// full payload.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The sheet payload: full (<c>Changed = true</c>) or fast path (unchanged).</returns>
    /// <exception cref="KeyNotFoundException">The trial with <paramref name="trialId"/> does not exist.</exception>
    /// <exception cref="FileNotFoundException">
    /// The trial exists but no workbook file is left on disk.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The resolved workbook has no "Subsample Plot Data" sheet (non-ARM upload).
    /// </exception>
    Task<TrialSheet> GetSheetAsync(Guid trialId, string? since, CancellationToken ct = default);
}
