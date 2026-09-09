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

/// <summary>
/// Concrete <see cref="ITokenService"/> that signs short-lived access tokens
/// (HMAC-SHA256 JWTs) from <see cref="TokenOptions"/> and mints opaque refresh
/// tokens using <see cref="RandomNumberGenerator"/>. The signing secret and
/// token lifetimes come from configuration; no external secrets are hard-coded.
/// </summary>
public class TokenService : ITokenService
{
    /// <summary>
    /// Entropy of a refresh token in bytes (64 bytes = 512 bits).
    /// </summary>
    private const int RefreshTokenByteLength = 64;

    /// <summary>
    /// Minimum accepted signing-secret length, in characters (bytes of UTF-8),
    /// for the HMAC-SHA256 signing key. HMAC-SHA256 requires at least 256 bits
    /// (32 bytes) of key material; shorter keys silently weaken the signature.
    /// </summary>
    private const int MinSecretKeyLength = 32;

    private readonly TokenOptions _options;

    /// <summary>
    /// Creates a new <see cref="TokenService"/>.
    /// </summary>
    /// <param name="options">The bound token settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The configured signing secret is shorter
    /// than <see cref="MinSecretKeyLength"/> characters (i.e. weaker than 256 bits of
    /// HMAC-SHA256 key material). This guards against a deployment silently running with a
    /// weak or placeholder secret.</exception>
    public TokenService(IOptions<TokenOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        // Fail fast: reject a weak signing key before any token is minted. The value
        // itself is left untouched (only a trimmed copy is measured), so a secret that
        // accidentally carries surrounding whitespace is not silently re-encoded.
        if (_options.SecretKey?.Trim().Length < MinSecretKeyLength)
        {
            throw new InvalidOperationException(
                $"The JWT signing secret ('{TokenOptions.SectionName}:SecretKey') must be at least " +
                $"{MinSecretKeyLength} characters (256 bits for HMAC-SHA256). Configure a strong, " +
                "random secret before starting the application.");
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(RefreshTokenByteLength);
        return Base64UrlEncoder.Encode(bytes);
    }

    /// <inheritdoc />
    public int RefreshTokenLifetimeDays => _options.RefreshTokenExpirationDays;

    /// <summary>
    /// Builds the claim set the access token carries. Uses the short JWT
    /// claim names (<c>sub</c>, <c>email</c>, <c>name</c>, <c>role</c>)
    /// so the token payload is directly readable by callers that use
    /// <c>MapInboundClaims = false</c>.
    /// </summary>
    /// <param name="user">The authenticated user profile.</param>
    /// <returns>The populated claim list.</returns>
    private static List<Claim> BuildClaims(UserResponse user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.Name),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        // Role is optional — only emit the claim when the user has one assigned.
        if (!string.IsNullOrEmpty(user.Role))
        {
            claims.Add(new Claim("role", user.Role));
        }

        return claims;
    }
}