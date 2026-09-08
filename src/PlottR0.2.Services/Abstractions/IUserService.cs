using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Domain;

namespace PlottR0._2.Services.Abstractions;

/// <summary>
/// User-specific service contract extending the generic <see cref="ICrudService{TEntity,TKey}"/>.
/// Inherits the structural entity-level reads (<see cref="ICrudService{TEntity,TKey}.GetByIdAsync"/>
/// / <see cref="ICrudService{TEntity,TKey}.GetAllAsync"/>) and adds the User workflows,
/// which are DTO-oriented. Write and DTO-read methods are named distinctly so they never
/// collide with the inherited entity-returning methods by return type.
/// </summary>
public interface IUserService : ICrudService<User, Guid>
{
    /// <summary>
    /// Creates a new user from a <see cref="CreateUserRequest"/>.
    /// </summary>
    /// <param name="request">The create request payload. Must not be <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created user as a <see cref="UserResponse"/>.</returns>
    Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing user, applying an <see cref="UpdateUserRequest"/> in place
    /// while preserving the user's identity.
    /// </summary>
    /// <param name="id">The identity key of the user to update.</param>
    /// <param name="request">The update request payload. Must not be <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated user as a <see cref="UserResponse"/>, or <see langword="null"/>
    /// if no user with the given <paramref name="id"/> exists.</returns>
    Task<UserResponse?> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default);

    /// <summary>
    /// Deletes a user by its identity key. Deleting a non-existent user is an
    /// idempotent no-op.
    /// </summary>
    /// <param name="id">The identity key of the user to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets a single user by its identity key as a <see cref="UserResponse"/> DTO.
    /// </summary>
    /// <param name="id">The user's identity key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching user DTO, or <see langword="null"/> if none exists.</returns>
    Task<UserResponse?> GetResponseAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets all users as <see cref="UserResponse"/> DTOs.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An immutable list of all users as DTOs.</returns>
    Task<IReadOnlyList<UserResponse>> GetAllResponsesAsync(CancellationToken ct = default);

    /// <summary>
    /// Validates a user's email + password and returns their public profile on
    /// success. Throws <see cref="UnauthorizedAccessException"/> when the email
    /// is unknown or the password does not match. Authentication token issuance
    /// is a separate concern (out of scope for the MVP API contract).
    /// </summary>
    /// <param name="request">The login credentials (email + plaintext password).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The authenticated user as a <see cref="UserResponse"/>.</returns>
    Task<UserResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
}
