using Microsoft.EntityFrameworkCore;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Persistence;

namespace PlottR0._2.Repository.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUserRepository"/> backed by PostgreSQL
/// via <see cref="PlottR0DbContext"/>.
/// </summary>
public sealed class UserRepository : IUserRepository
{
    private readonly PlottR0DbContext _db;

    /// <summary>
    /// Initialises a new instance of the <see cref="UserRepository"/> class.
    /// </summary>
    /// <param name="db">The application's EF Core <see cref="PlottR0DbContext"/>.</param>
    public UserRepository(PlottR0DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Users.FindAsync([id], ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<User>> GetAllAsync(
        int? skip = null,
        int? take = null,
        CancellationToken ct = default)
    {
        IQueryable<User> query = _db.Users.OrderBy(u => u.Name);

        if (skip.HasValue)
            query = query.Skip(skip.Value);

        if (take.HasValue)
            query = query.Take(take.Value);

        return await query.ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Users.AnyAsync(u => u.Id == id, ct);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        return await _db.Users.CountAsync(ct);
    }

    /// <inheritdoc />
    public async Task<User> AddAsync(User entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        _db.Users.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    /// <inheritdoc />
    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        return await _db.Users
            .FirstOrDefaultAsync(u => u.Email == email, ct);
    }

    /// <inheritdoc />
    public async Task<User> UpdateAsync(User entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        _db.Users.Update(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var user = await _db.Users.FindAsync([id], ct);

        if (user is null)
            return; // Idempotent no-op — deleting a non-existent entity is not an error.

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);
    }
}
