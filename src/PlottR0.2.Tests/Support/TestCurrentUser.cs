using PlottR0._2.Contracts;

namespace PlottR0._2.Tests.Support;

/// <summary>
/// Test double for <see cref="ICurrentUser"/> — exposes a settable identity so
/// tests can simulate an authenticated actor without a real JWT/HttpContext.
/// Defaults to a fixed anonymous identity (null fields) unless a test overrides it.
/// </summary>
public sealed class TestCurrentUser : ICurrentUser
{
    /// <summary>The actor identity to expose. Defaults to a known test user GUID.</summary>
    public Guid? UserId { get; set; } = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>The actor email, or null for an unauthenticated actor.</summary>
    public string? Email { get; set; } = "test@plottr02.local";

    /// <summary>The actor display name, or null for an unauthenticated actor.</summary>
    public string? Name { get; set; } = "Test Actor";
}
