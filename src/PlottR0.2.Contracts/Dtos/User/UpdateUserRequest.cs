using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Dtos.User;

public class UpdateUserRequest
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public UserRole? Role { get; set; }
}