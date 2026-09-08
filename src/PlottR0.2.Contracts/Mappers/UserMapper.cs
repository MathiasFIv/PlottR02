using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Mappers;

/// <summary>
/// Stateless static mapper between <see cref="User"/> and its DTOs.
/// No DI registration needed — call directly from services.
/// </summary>
public static class UserMapper
{
    /// <summary>
    /// Maps a domain <see cref="User"/> to a <see cref="UserResponse"/> DTO.
    /// </summary>
    public static UserResponse ToResponse(User entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new UserResponse
        {
            Id = entity.Id,
            Name = entity.Name,
            Email = entity.Email,
            Role = entity.Role
        };
    }

    /// <summary>
    /// Creates a new domain <see cref="User"/> from a <see cref="CreateUserRequest"/>
    /// with a freshly generated <see cref="Guid"/>.
    /// </summary>
    public static User ToEntity(CreateUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new User
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Email = request.Email,
            Role = request.Role,
            PasswordHash = string.Empty
        };
    }

    /// <summary>
    /// Mutates an existing <see cref="User"/> in place with values from
    /// an <see cref="UpdateUserRequest"/>. The entity's <see cref="User.Id"/> is preserved.
    /// </summary>
    public static void Apply(UpdateUserRequest request, User entity)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entity);

        entity.Name = request.Name;
        entity.Email = request.Email;
        entity.Role = request.Role;
    }

    /// <summary>
    /// Batch-maps a collection of domain entities to response DTOs.
    /// </summary>
    public static IReadOnlyList<UserResponse> ToResponses(IEnumerable<User> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        return entities.Select(ToResponse).ToList();
    }
}
