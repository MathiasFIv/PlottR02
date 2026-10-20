using System.Security.Claims;
using PlottR0._2.Contracts;
using PlottR0._2.Domain;

namespace PlottR0._2.Api;

public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    public Guid? UserId
    {
        get
        {
            var sub = _accessor.HttpContext?.User.FindFirstValue("sub");
            return sub is not null && Guid.TryParse(sub, out var id) ? id : null;
        }
    }

    public string? Email => _accessor.HttpContext?.User.FindFirstValue("email");

    public string? Name => _accessor.HttpContext?.User.FindFirstValue("name");

    public UserRole? Role
    {
        get
        {
            var raw = _accessor.HttpContext?.User.FindFirstValue(JwtBearerConfiguration.RoleClaimName);
            return raw is not null && Enum.TryParse<UserRole>(raw, ignoreCase: true, out var role) ? role : null;
        }
    }
}
