using System.Text;
using Microsoft.IdentityModel.Tokens;
using PlottR0._2.Services.Configuration;

namespace PlottR0._2.Api;

public static class JwtBearerConfiguration
{
    /// <summary>
    /// Literal claim name used for roles: tokens carry "role" directly and inbound claim mapping is
    /// disabled, so role checks must resolve this exact name rather than a legacy WS-* URI.
    /// </summary>
    public const string RoleClaimName = "role";

    public static TokenValidationParameters CreateTokenValidationParameters(TokenOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SecretKey)),
        ClockSkew = TimeSpan.FromMinutes(1),
        RoleClaimType = RoleClaimName,
    };
}
