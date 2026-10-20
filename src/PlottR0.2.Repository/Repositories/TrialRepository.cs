using Microsoft.EntityFrameworkCore;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Persistence;

namespace PlottR0._2.Repository.Repositories;

public sealed class TrialRepository : ITrialRepository
{
    private readonly PlottR0DbContext _db;

    public TrialRepository(PlottR0DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<Trial?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Trials.FindAsync([id], ct);
    }

    public async Task<IReadOnlyList<Trial>> GetAllAsync(
        int? skip = null,
        int? take = null,
        CancellationToken ct = default)
    {
        IQueryable<Trial> query = _db.Trials
            .Include(t => t.CreatedByUser)
            .OrderByDescending(t => t.CreatedAt);

        if (skip.HasValue)
            query = query.Skip(skip.Value);

        if (take.HasValue)
            query = query.Take(take.Value);

        return await query.ToListAsync(ct);
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Trials.AnyAsync(t => t.Id == id, ct);
    }

    public async Task<Trial> CreateAsync(Trial trial, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trial);

        _db.Trials.Add(trial);
        await _db.SaveChangesAsync(ct);
        return trial;
    }

    public async Task SetAssignmentsAsync(
        Guid trialId,
        IReadOnlyList<Guid> userIds,
        Guid assignedById,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var existing = await _db.Assignments
            .Where(a => a.TrialId == trialId)
            .ToListAsync(ct);

        if (existing.Count > 0)
            _db.Assignments.RemoveRange(existing);

        var assignedAt = DateTime.UtcNow;
        foreach (var userId in userIds)
        {
            _db.Assignments.Add(new TrialAssignment
            {
                Id = Guid.NewGuid(),
                TrialId = trialId,
                UserId = userId,
                AssignedBy = assignedById,
                AssignedAt = assignedAt,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TrialAssignment>> GetAssignmentsAsync(
        Guid trialId,
        CancellationToken ct = default)
    {
        return await _db.Assignments
            .Where(a => a.TrialId == trialId)
            .OrderBy(a => a.AssignedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetAssignedTrialIdsAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        return await _db.Assignments
            .Where(a => a.UserId == userId)
            .Select(a => a.TrialId)
            .ToListAsync(ct);
    }

    public async Task<bool> IsAssignedAsync(
        Guid trialId,
        Guid userId,
        CancellationToken ct = default)
    {
        return await _db.Assignments
            .AnyAsync(a => a.TrialId == trialId && a.UserId == userId, ct);
    }

    public async Task UpdateStatusAsync(
        Guid trialId,
        string status,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        var updated = await _db.Trials
            .Where(t => t.Id == trialId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.Status, status)
                    .SetProperty(t => t.UpdatedAt, DateTime.UtcNow),
                ct);

        if (updated == 0)
            throw new KeyNotFoundException($"Trial '{trialId}' was not found.");
    }
}
