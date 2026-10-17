using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Dtos.User;

namespace PlottR0._2.Services.Abstractions;

public interface ITrialService
{
    Task<TrialSummary> CreateTrialAsync(string fileName, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Lists the trials visible to the current actor, newest first. Managers (system
    /// administrators, researchers and trial managers) see every trial; all other actors
    /// (field workers, or legacy/role-less users) see ONLY the trials they are assigned to
    /// (an empty list when they have no assignments).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The visible trial summaries.</returns>
    Task<IReadOnlyList<TrialSummary>> ListTrialsAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces the complete set of users assigned to a trial. The caller must be a
    /// system administrator, researcher or trial manager, and every requested user must
    /// exist, carry a role and be assignable (researcher, trial manager or field worker).
    /// Validation is all-or-nothing: nothing is persisted unless every id is valid.
    /// </summary>
    /// <param name="trialId">The trial whose assignment set is replaced.</param>
    /// <param name="request">The full desired set of user ids (may be empty to clear the crew).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ForbiddenAccessException">The caller's role may not assign users.</exception>
    /// <exception cref="KeyNotFoundException">The trial does not exist.</exception>
    /// <exception cref="ArgumentException">
    /// The ids are not distinct, or a user is unknown, role-less or a system administrator.
    /// </exception>
    Task AssignTrialUsersAsync(Guid trialId, AssignTrialUsersRequest request, CancellationToken ct = default);

    /// <summary>
    /// Lists the users currently assigned to a trial, in assignment order (oldest first).
    /// Restricted to system administrators, researchers and trial managers.
    /// </summary>
    /// <param name="trialId">The trial whose assigned users are read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assigned users as response DTOs.</returns>
    /// <exception cref="ForbiddenAccessException">The caller's role may not view assignments.</exception>
    /// <exception cref="KeyNotFoundException">The trial does not exist.</exception>
    Task<IReadOnlyList<UserResponse>> ListTrialUsersAsync(Guid trialId, CancellationToken ct = default);

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
    /// <exception cref="ForbiddenAccessException">
    /// The caller is not a manager and is not assigned to the trial.
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
    /// <exception cref="ForbiddenAccessException">
    /// The caller is not a manager and is not assigned to the trial.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The trial exists but no workbook file is left on disk.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The resolved workbook has no "Subsample Plot Data" sheet (non-ARM upload).
    /// </exception>
    Task<TrialSheet> GetSheetAsync(Guid trialId, string? since, CancellationToken ct = default);

    /// <summary>
    /// Edits ONE ratings cell of a trial's sheet (the inline "edit a sheet cell" feature):
    /// resolves the current workbook, writes the addressed <c>(plot, subsample, traitOrdinal)</c>
    /// cell under the locked-overwrite policy, and — only when a cell was actually written —
    /// mirrors the trial's files to durable storage and records a "Trial.CellEdited" audit
    /// entry. A cell that already holds a value is REFUSED (reported as skipped, not an error)
    /// unless <see cref="EditCellRequest.Overwrite"/> is set. The returned
    /// <see cref="CellEditResult.Version"/> reflects the just-written copy.
    /// <para/>
    /// Orchestration only — the workbook read/write lives behind <c>IRatingRepository</c>;
    /// no ClosedXML/file I/O happens here.
    /// </summary>
    /// <param name="trialId">The trial whose sheet cell is edited.</param>
    /// <param name="request">The addressed cell, the new value and the overwrite opt-in.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome plus the workbook's new opaque version.</returns>
    /// <exception cref="ArgumentException">
    /// A field is out of range: plot/subsample ≤ 0, traitOrdinal outside 1..9, or a missing or
    /// out-of-range (0..10) value.
    /// </exception>
    /// <exception cref="KeyNotFoundException">The trial does not exist.</exception>
    /// <exception cref="ForbiddenAccessException">
    /// The caller is not a manager and is not assigned to the trial.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The trial exists but no workbook file is left on disk.
    /// </exception>
    /// <exception cref="InvalidDataException"><c>traitOrdinal</c> is outside 1..9 (repository defense in depth).</exception>
    Task<CellEditResult> EditCellAsync(Guid trialId, EditCellRequest request, CancellationToken ct = default);

    /// <summary>
    /// Ensures the trial exists and the current actor may access it, using the SAME
    /// existence-check-wins-then-per-trial-access gates <see cref="EditCellAsync"/> runs at
    /// its start. A thin pre-check for callers that want to fail fast on a bad
    /// <paramref name="trialId"/> BEFORE spending money/time downstream (e.g. before a
    /// speech-to-text call) without re-running the full edit afterwards.
    /// </summary>
    /// <param name="trialId">The trial whose existence and access are checked.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">The trial does not exist.</exception>
    /// <exception cref="ForbiddenAccessException">
    /// The caller is not a manager and is not assigned to the trial.
    /// </exception>
    Task EnsureTrialAccessAsync(Guid trialId, CancellationToken ct = default);
}
