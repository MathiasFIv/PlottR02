using Microsoft.EntityFrameworkCore;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Persistence;

namespace PlottR0._2.Repository.Repositories;

public sealed class UserRepository : IUserRepository
{
    private readonly PlottR0DbContext _db;

    public UserRepository(PlottR0DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Users.FindAsync([id], ct);
    }

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

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Users.AnyAsync(u => u.Id == id, ct);
    }

    public async Task<User> AddAsync(User entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        _db.Users.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        return await _db.Users
            .FirstOrDefaultAsync(u => u.Email == email, ct);
    }

    public async Task<User> UpdateAsync(User entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        _db.Users.Update(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var user = await _db.Users.FindAsync([id], ct);

        if (user is null)
            return;

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);
    }
}
