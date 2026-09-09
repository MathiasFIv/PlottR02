using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Options;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Services.Configuration;
using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Tests;

/// <summary>
/// Unit tests for <see cref="TokenService"/>. No Testcontainers / Postgres —
/// only requires <see cref="IOptions{T}"/> and a <see cref="UserResponse"/>.
/// </summary>
public sealed class TokenServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string UserEmail = "alice@example.com";
    private const string UserName = "Alice";
    private const string UserRole = "Agronomist";

    private static TokenService CreateService()
    {
        var options = new TokenOptions
        {
            SecretKey = "test-secret-key-that-is-long-enough-for-hmac-256",
            Issuer = "test-issuer",
            Audience = "test-audience",
            AccessTokenExpirationMinutes = 15,
        };
        return new TokenService(Options.Create(options));
    }

    private static UserResponse CreateUser(string? role = UserRole) => new()
    {
        Id = UserId,
        Email = UserEmail,
        Name = UserName,
        Role = role,
    };

    [Fact]
    public void GenerateAccessToken_WithRole_ProducesCorrectClaims()
    {
        // Arrange
        var service = CreateService();
        var user = CreateUser();

        // Act
        var token = service.GenerateAccessToken(user);

        // Assert — decode raw payload claims (no inbound mapping)
        var decoded = new JwtSecurityTokenHandler().ReadJwtToken(token);

        decoded.Claims.Should().Contain(c => c.Type == "sub" && c.Value == UserId.ToString());
        decoded.Claims.Should().Contain(c => c.Type == "email" && c.Value == UserEmail);
        decoded.Claims.Should().Contain(c => c.Type == "name" && c.Value == UserName);
        decoded.Claims.Should().Contain(c => c.Type == "role" && c.Value == UserRole);
        decoded.Claims.Should().Contain(c => c.Type == "jti");

        // Legacy mapped names must NOT appear
        decoded.Claims.Should().NotContain(c => c.Type == "nameid");
        decoded.Claims.Should().NotContain(c => c.Type == "unique_name");
        decoded.Claims.Should().NotContain(c =>
            c.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
    }

    [Fact]
    public void GenerateAccessToken_NullRole_OmitsRoleClaim()
    {
        // Arrange
        var service = CreateService();
        var user = CreateUser(role: null);

        // Act
        var token = service.GenerateAccessToken(user);

        // Assert
        var decoded = new JwtSecurityTokenHandler().ReadJwtToken(token);

        decoded.Claims.Should().Contain(c => c.Type == "sub" && c.Value == UserId.ToString());
        decoded.Claims.Should().NotContain(c => c.Type == "role");
    }
}
