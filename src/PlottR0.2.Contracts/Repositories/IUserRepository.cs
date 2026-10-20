using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Repositories;

public interface IUserRepository : IRepository<User, Guid>
{
    Task<User> AddAsync(User entity, CancellationToken ct = default);
    Task<User> UpdateAsync(User entity, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);
}