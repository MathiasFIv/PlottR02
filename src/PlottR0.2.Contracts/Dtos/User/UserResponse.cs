using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Dtos.User;

public class UserResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public UserRole? Role { get; set; }
}