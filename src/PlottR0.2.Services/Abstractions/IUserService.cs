using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Domain;

namespace PlottR0._2.Services.Abstractions;

public interface IUserService : ICrudService<User, Guid>
{
    Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken ct = default);

    Task<UserResponse?> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default);

    /// <summary>Deleting a user that no longer exists is an idempotent no-op, not an error.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    Task<UserResponse?> GetResponseAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<UserResponse>> GetAllResponsesAsync(CancellationToken ct = default);

    Task<UserResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
}
