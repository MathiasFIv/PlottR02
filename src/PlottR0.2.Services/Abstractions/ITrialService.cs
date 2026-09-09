using PlottR0._2.Contracts.Dtos.Trial;

namespace PlottR0._2.Services.Abstractions;

public interface ITrialService
{
    Task<TrialSummary> CreateTrialAsync(string fileName, Stream content, CancellationToken ct = default);
    Task<IReadOnlyList<TrialSummary>> ListTrialsAsync(CancellationToken ct = default);
}
