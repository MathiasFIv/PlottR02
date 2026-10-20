using PlottR0._2.Contracts;
using PlottR0._2.Domain;

namespace PlottR0._2.Tests.Support;

public sealed class TestCurrentUser : ICurrentUser
{
    private static readonly Guid DefaultUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public Guid? UserId { get; set; } = DefaultUserId;

    public string? Email { get; set; } = "test@plottr02.local";

    public string? Name { get; set; } = "Test Actor";

    public UserRole? Role { get; set; } = UserRole.SystemAdmin;

    public void ResetToDefaults()
    {
        UserId = DefaultUserId;
        Email = "test@plottr02.local";
        Name = "Test Actor";
        Role = UserRole.SystemAdmin;
    }
}