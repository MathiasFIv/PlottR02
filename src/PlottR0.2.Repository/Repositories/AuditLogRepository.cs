using Microsoft.EntityFrameworkCore;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Persistence;

namespace PlottR0._2.Repository.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAuditLogRepository"/> backed by
/// PostgreSQL via <see cref="PlottR0DbContext"/>.
/// </summary>
public sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly PlottR0DbContext _db;

    /// <summary>
    /// Initialises a new instance of the <see cref="AuditLogRepository"/> class.
    /// </summary>
    /// <param name="db">The application's EF Core <see cref="PlottR0DbContext"/>.</param>
    public AuditLogRepository(PlottR0DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task AddAsync(AuditLog entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _db.AuditLogs.Add(entry);
        await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLog>> GetAllAsync(
        int? skip = null,
        int? take = null,
        CancellationToken ct = default)
    {
        IQueryable<AuditLog> query = _db.AuditLogs
            .OrderByDescending(a => a.Timestamp);

        if (skip.HasValue)
            query = query.Skip(skip.Value);

        if (take.HasValue)
            query = query.Take(take.Value);

        return await query.ToListAsync(ct);
    }
}
