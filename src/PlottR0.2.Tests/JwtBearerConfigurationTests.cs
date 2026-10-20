using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using PlottR0._2.Api;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Domain;
using PlottR0._2.Services.Configuration;
using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Tests;

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
