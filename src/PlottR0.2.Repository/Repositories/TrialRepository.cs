using Microsoft.EntityFrameworkCore;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Persistence;

namespace PlottR0._2.Repository.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ITrialRepository"/> backed by PostgreSQL
/// via <see cref="PlottR0DbContext"/>.
/// </summary>
public sealed class TrialRepository : ITrialRepository
{
    private readonly PlottR0DbContext _db;

    public TrialRepository(PlottR0DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<Trial?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Trials.FindAsync([id], ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Trial>> GetAllAsync(
        int? skip = null,
        int? take = null,
        CancellationToken ct = default)
    {
        IQueryable<Trial> query = _db.Trials.OrderByDescending(t => t.CreatedAt);

        if (skip.HasValue)
            query = query.Skip(skip.Value);

        if (take.HasValue)
            query = query.Take(take.Value);

        return await query.ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Trials.AnyAsync(t => t.Id == id, ct);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        return await _db.Trials.CountAsync(ct);
    }

    /// <inheritdoc />
    public async Task<Trial> CreateAsync(Trial trial, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trial);

        _db.Trials.Add(trial);
        await _db.SaveChangesAsync(ct);
        return trial;
    }
}
