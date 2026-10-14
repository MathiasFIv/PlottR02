using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Repositories;

public interface ITrialRepository : IRepository<Trial, Guid>
{
    Task<Trial> CreateAsync(Trial trial, CancellationToken ct = default);

    /// <summary>
    /// Replaces the complete set of users assigned to a trial with
    /// <paramref name="userIds"/> in a single round-trip: all existing assignment
    /// rows for the trial are deleted, then one row is inserted per requested user.
    /// </summary>
    /// <param name="trialId">The trial whose assignment set is replaced.</param>
    /// <param name="userIds">The full desired set of assigned user ids (may be empty).</param>
    /// <param name="assignedById">The user id of whoever created the assignments.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetAssignmentsAsync(
        Guid trialId,
        IReadOnlyList<Guid> userIds,
        Guid assignedById,
        CancellationToken ct = default);

    /// <summary>
    /// Gets every assignment for a trial, ordered by <see cref="TrialAssignment.AssignedAt"/>.
    /// </summary>
    /// <param name="trialId">The trial whose assignments are read.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<TrialAssignment>> GetAssignmentsAsync(Guid trialId, CancellationToken ct = default);

    /// <summary>
    /// Gets the ids of every trial a user is assigned to.
    /// </summary>
    /// <param name="userId">The user whose assigned trial ids are read.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Guid>> GetAssignedTrialIdsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Determines whether a specific user is assigned to a specific trial.
    /// </summary>
    /// <param name="trialId">The trial to check.</param>
    /// <param name="userId">The user to check.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> IsAssignedAsync(Guid trialId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Advances ONLY the <see cref="Trial.Status"/> of the trial identified by
    /// <paramref name="trialId"/> (plus its audit timestamp) in a single round-trip —
    /// the dashboard badge lifecycle (<see cref="TrialStatus"/>: Created → InProgress →
    /// Completed) persisted by the sync pipeline after a successful workbook write.
    /// </summary>
    /// <param name="trialId">The trial whose status is updated.</param>
    /// <param name="status">The new status value (see <see cref="TrialStatus"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">No trial with <paramref name="trialId"/> exists.</exception>
    Task UpdateStatusAsync(Guid trialId, string status, CancellationToken ct = default);
}
