using PlottR0._2.Domain;

namespace PlottR0._2.Contracts;

public interface ICurrentUser
{
    Guid? UserId { get; }

    string? Email { get; }

    string? Name { get; }

    UserRole? Role { get; }
}
