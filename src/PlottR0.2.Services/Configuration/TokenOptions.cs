namespace PlottR0._2.Services.Configuration;

/// <summary>
/// JWT / token issuance settings, bound from the <c>Jwt</c> configuration
/// section. Lives inside Services (not Api) so the token feature stays
/// self-contained and the Api project only wires it up via
/// <see cref="Microsoft.Extensions.Options.IOptions{T}"/>.
/// </summary>
public sealed class TokenOptions
{
    /// <summary>
    /// The configuration section name this options type binds to.
    /// </summary>
    public const string SectionName = "Jwt";

    /// <summary>
    /// The symmetric signing key used to sign access tokens (HMAC-SHA256).
    /// Must be at least 256 bits in a production deployment.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// The token issuer (<c>iss</c> claim).
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// The intended token audience (<c>aud</c> claim).
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Lifetime of an access token in minutes. Short-lived by design; the
    /// refresh token is the long-lived credential.
    /// </summary>
    public int AccessTokenExpirationMinutes { get; set; } = 15;

    /// <summary>
    /// Lifetime of a refresh token in days.
    /// </summary>
    public int RefreshTokenExpirationDays { get; set; } = 7;
}