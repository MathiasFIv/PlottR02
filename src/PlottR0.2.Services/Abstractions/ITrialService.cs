using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Dtos.User;

namespace PlottR0._2.Services.Abstractions;

public interface ITrialService
{
    Task<TrialSummary> CreateTrialAsync(string fileName, Stream content, CancellationToken ct = default);

    Task<IReadOnlyList<TrialSummary>> ListTrialsAsync(CancellationToken ct = default);

    Task AssignTrialUsersAsync(Guid trialId, AssignTrialUsersRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<UserResponse>> ListTrialUsersAsync(Guid trialId, CancellationToken ct = default);

    Task<WorkbookExport> GetWorkbookExportAsync(Guid trialId, CancellationToken ct = default);

    /// <summary>
    /// A <paramref name="since"/> value that cannot be matched is deliberately ignored (full
    /// payload) rather than rejected, so a poller that hand-edits its query string never
    /// hard-fails. The token is compared as an opaque string, never parsed.
    /// </summary>
    Task<TrialSheet> GetSheetAsync(Guid trialId, string? since, CancellationToken ct = default);

    Task<CellEditResult> EditCellAsync(Guid trialId, EditCellRequest request, CancellationToken ct = default);

    Task EnsureTrialAccessAsync(Guid trialId, CancellationToken ct = default);
}
