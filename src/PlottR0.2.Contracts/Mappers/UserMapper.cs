using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Mappers;

public static class UserMapper
{
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

    public static void Apply(UpdateUserRequest request, User entity)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entity);

        entity.Name = request.Name;
        entity.Email = request.Email;
        entity.Role = request.Role;
    }

    public static IReadOnlyList<UserResponse> ToResponses(IEnumerable<User> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        return entities.Select(ToResponse).ToList();
    }
}
