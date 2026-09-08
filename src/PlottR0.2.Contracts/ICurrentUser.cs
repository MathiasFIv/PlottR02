namespace PlottR0._2.Contracts;

/// <summary>
/// Provides the identity of the currently authenticated user.
/// Implemented in the Api layer by reading the JWT principal; consumed by
/// Services to attribute audit fields and actor identity. Decoupled from
/// HttpContext so Services remain persistence- and web-agnostic.
/// </summary>
public interface ICurrentUser
{
    /// <summary>The authenticated user's ID from the JWT "sub" claim, or null if unauthenticated.</summary>
    Guid? UserId { get; }

    /// <summary>The authenticated user's email from the JWT "email" claim, or null.</summary>
    string? Email { get; }

    /// <summary>The authenticated user's display name from the JWT "name" claim, or null.</summary>
    string? Name { get; }
}
