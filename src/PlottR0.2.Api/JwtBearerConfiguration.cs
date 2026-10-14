using System.Text;
using Microsoft.IdentityModel.Tokens;
using PlottR0._2.Services.Configuration;

namespace PlottR0._2.Api;

/// <summary>
/// Builds the <see cref="TokenValidationParameters"/> the JwtBearer handler uses to
/// validate access tokens.
///
/// Extracted from <c>Program.cs</c> so the claim-type wiring — in particular
/// <see cref="TokenValidationParameters.RoleClaimType"/> — is unit-testable without
/// a WebApplicationFactory pipeline (see <c>JwtBearerConfigurationTests</c>).
/// </summary>
public static class JwtBearerConfiguration
{
    /// <summary>
    /// Claim type that carries a user's role. Access tokens are issued with a literal
    /// <c>"role"</c> claim (<see cref="Services.Implementations.TokenService"/>) and
    /// inbound claim mapping is disabled, so the validation must look for exactly
    /// <c>"role"</c>. Without this, <c>[Authorize(Roles = ...)]</c> /
    /// <c>User.IsInRole(...)</c> would look for the legacy WS-* role URI and deny every
    /// caller.
    /// </summary>
    public const string RoleClaimName = "role";

    /// <summary>
    /// Creates the token validation parameters for the given JWT options.
    /// </summary>
    /// <param name="options">The bound <c>Jwt</c> configuration section.</param>
    /// <returns>Validation parameters matching how <c>TokenService</c> signs tokens.</returns>
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
