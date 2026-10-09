using PlottR0._2.Contracts;
using PlottR0._2.Domain;

namespace PlottR0._2.Tests.Support;

/// <summary>
/// Test double for <see cref="ICurrentUser"/> — exposes a settable identity so
/// tests can simulate an authenticated actor without a real JWT/HttpContext.
/// Defaults to a fixed authenticated actor holding the <see cref="UserRole.SystemAdmin"/>
/// role, so the default actor can exercise any create/update path. Tests that care
/// about actor identity or role set the properties explicitly, and the owning test
/// class restores <see cref="ResetToDefaults"/> after each test.
/// </summary>
public sealed class TestCurrentUser : ICurrentUser
{
    /// <summary>Default actor user id.</summary>
    private static readonly Guid DefaultUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>The actor identity to expose. Null = unauthenticated actor.</summary>
    public Guid? UserId { get; set; } = DefaultUserId;

    /// <summary>The actor email, or null for an unauthenticated actor.</summary>
    public string? Email { get; set; } = "test@plottr02.local";

    /// <summary>The actor display name, or null for an unauthenticated actor.</summary>
    public string? Name { get; set; } = "Test Actor";

    /// <summary>The actor role, or null for an anonymous/role-less actor. Defaults to
    /// <see cref="UserRole.SystemAdmin"/> so the default actor passes every role gate.</summary>
    public UserRole? Role { get; set; } = UserRole.SystemAdmin;

    /// <summary>Restores the documented default actor identity (used after each test).</summary>
    public void ResetToDefaults()
    {
        UserId = DefaultUserId;
        Email = "test@plottr02.local";
        Name = "Test Actor";
        Role = UserRole.SystemAdmin;
    }
}