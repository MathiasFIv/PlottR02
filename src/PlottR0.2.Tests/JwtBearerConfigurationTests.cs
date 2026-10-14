using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using PlottR0._2.Api;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Domain;
using PlottR0._2.Services.Configuration;
using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Tests;

/// <summary>
/// Pins the JwtBearer claim wiring that makes <c>[Authorize(Roles = ...)]</c> work.
/// Plan A14 forbids <c>WebApplicationFactory</c>, so instead of an HTTP pipeline this
/// mints a real token with <see cref="TokenService"/> and validates it with the exact
/// <see cref="Microsoft.IdentityModel.Tokens.TokenValidationParameters"/> the API uses
/// (<see cref="JwtBearerConfiguration"/>), then asserts <c>ClaimsPrincipal.IsInRole</c>.
/// This is what catches the "deny everyone" trap when <c>RoleClaimType</c> is left unset
/// while inbound claim mapping is off.
/// </summary>
public sealed class JwtBearerConfigurationTests
{
    private static readonly TokenOptions TestOptions = new()
    {
        SecretKey = "test-secret-key-that-is-long-enough-for-hmac-256",
        Issuer = "test-issuer",
        Audience = "test-audience",
        AccessTokenExpirationMinutes = 15,
    };

    private static string MintToken(UserRole? role)
    {
        var service = new TokenService(Options.Create(TestOptions));
        return service.GenerateAccessToken(new UserResponse
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            Name = "User",
            Role = role,
        });
    }

    private static ClaimsPrincipal Validate(string token)
    {
        // MapInboundClaims = false mirrors JwtBearerOptions in Program.cs, so the claim
        // stays the literal "role" that the validation parameters are configured for.
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        return handler.ValidateToken(
            token,
            JwtBearerConfiguration.CreateTokenValidationParameters(TestOptions),
            out _);
    }

    [Fact]
    public void ValidationParameters_UseRoleClaimTypeOfRole()
    {
        JwtBearerConfiguration.CreateTokenValidationParameters(TestOptions)
            .RoleClaimType.Should().Be(JwtBearerConfiguration.RoleClaimName);
    }

    [Fact]
    public void ValidationParameters_UseRoleClaimTypeOfRole_Literal()
    {
        JwtBearerConfiguration.CreateTokenValidationParameters(TestOptions)
            .RoleClaimType.Should().Be("role");
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin, true)]
    [InlineData(UserRole.TrialManager, true)]
    [InlineData(UserRole.Researcher, false)]
    [InlineData(UserRole.FieldWorker, false)]
    public void ValidatedToken_IsInRole_MatchesTheTokenRole(UserRole role, bool canManageUsers)
    {
        var principal = Validate(MintToken(role));

        principal.Identity!.IsAuthenticated.Should().BeTrue();
        (principal.IsInRole("SystemAdmin") || principal.IsInRole("TrialManager"))
            .Should().Be(canManageUsers);
    }

    [Fact]
    public void ValidatedToken_WithNullRole_IsInNoAdminRole()
    {
        var principal = Validate(MintToken(role: null));

        principal.IsInRole("SystemAdmin").Should().BeFalse();
        principal.IsInRole("TrialManager").Should().BeFalse();
    }
}
