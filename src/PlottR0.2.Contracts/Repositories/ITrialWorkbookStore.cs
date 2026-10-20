using PlottR0._2.Contracts.Dtos.Trial;

namespace PlottR0._2.Contracts.Repositories;

public interface ITrialWorkbookStore
{
    Task<string> SaveAsync(Guid trialId, string fileName, Stream content, CancellationToken ct = default);

    Task DeleteAsync(string relativePath, CancellationToken ct = default);

    Task<ResolvedWorkbookExport?> ResolveExportAsync(Guid trialId, string sourceRelativePath, CancellationToken ct = default);

    Task PersistTrialFilesAsync(Guid trialId, CancellationToken ct = default);

    string GetWorkingDirectory(Guid trialId);
}
