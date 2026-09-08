using System.Security.Claims;
using PlottR0._2.Contracts;

namespace PlottR0._2.Api;

/// <summary>
/// Reads the current user's identity from the JWT claims in HttpContext.
/// Scoped — one instance per HTTP request. Decouples Services from ASP.NET.
/// </summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    /// <summary>Creates a new <see cref="CurrentUser"/>.</summary>
    /// <param name="accessor">The HTTP context accessor used to read the current principal.</param>
    public CurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    /// <inheritdoc />
    public Guid? UserId
    {
        get
        {
            var sub = _accessor.HttpContext?.User.FindFirstValue("sub");
            return sub is not null && Guid.TryParse(sub, out var id) ? id : null;
        }
    }

    /// <inheritdoc />
    public string? Email => _accessor.HttpContext?.User.FindFirstValue("email");

    /// <inheritdoc />
    public string? Name => _accessor.HttpContext?.User.FindFirstValue("name");
}
