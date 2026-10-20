using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Repositories;

public interface ITrialRepository : IRepository<Trial, Guid>
{
    Task<Trial> CreateAsync(Trial trial, CancellationToken ct = default);

    Task SetAssignmentsAsync(
        Guid trialId,
        IReadOnlyList<Guid> userIds,
        Guid assignedById,
        CancellationToken ct = default);

    Task<IReadOnlyList<TrialAssignment>> GetAssignmentsAsync(Guid trialId, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetAssignedTrialIdsAsync(Guid userId, CancellationToken ct = default);

    Task<bool> IsAssignedAsync(Guid trialId, Guid userId, CancellationToken ct = default);

    Task UpdateStatusAsync(Guid trialId, string status, CancellationToken ct = default);
}
