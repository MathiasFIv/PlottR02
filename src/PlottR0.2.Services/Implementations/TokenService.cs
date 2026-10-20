using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Configuration;

namespace PlottR0._2.Services.Implementations;

public class TokenService : ITokenService
{
    private const int RefreshTokenByteLength = 64;

    // HMAC-SHA256 needs a 256-bit key, so the constructor rejects a shorter or placeholder secret.
    private const int MinSecretKeyLength = 32;

    private readonly TokenOptions _options;

    public TokenService(IOptions<TokenOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        if (_options.SecretKey?.Trim().Length < MinSecretKeyLength)
        {
            throw new InvalidOperationException(
                $"The JWT signing secret ('{TokenOptions.SectionName}:SecretKey') must be at least " +
                $"{MinSecretKeyLength} characters (256 bits for HMAC-SHA256). Configure a strong, " +
                "random secret before starting the application.");
        }
    }

    public string GenerateAccessToken(UserResponse user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(BuildClaims(user)),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Expires = DateTime.UtcNow.AddMinutes(_options.AccessTokenExpirationMinutes),
            SigningCredentials = credentials,
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(RefreshTokenByteLength);
        return Base64UrlEncoder.Encode(bytes);
    }

    public int RefreshTokenLifetimeDays => _options.RefreshTokenExpirationDays;

    private static List<Claim> BuildClaims(UserResponse user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.Name),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        if (user.Role is not null)
        {
            claims.Add(new Claim("role", user.Role.Value.ToString()));
        }

        return claims;
    }
}