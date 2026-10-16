using Microsoft.Extensions.Configuration;

namespace PlottR0._2.Services.Configuration;

/// <summary>
/// Strongly typed options for the <c>Auth</c> configuration section — the auth feature
/// flags that gate API behaviour. It lives in Services (the lowest layer that consumes it)
/// and is read by BOTH the Api's <c>UsersController</c> guard and the Services'
/// <see cref="Implementations.UserService"/> matrix, so there is a single source of truth.
///
/// The flag is permanent code — only the configuration value flips, at runtime, via
/// appsettings or the <c>Auth__*</c> environment variables (e.g. Render's
/// <c>Auth__AllowAnonymousSignup=true</c>).
/// </summary>
public sealed class AuthOptions
{
    /// <summary>
    /// The configuration section name this options type binds to.
    /// </summary>
    public const string SectionName = "Auth";

    /// <summary>
    /// The full configuration key for <see cref="AllowAnonymousSignup"/>.
    /// </summary>
    public const string AllowAnonymousSignupKey = SectionName + ":AllowAnonymousSignup";

    /// <summary>
    /// Allows an anonymous caller to create an account through
    /// <c>POST /api/Users</c> (the login-page self-signup panel).
    ///
    /// Defaults to <c>false</c>: when the key is absent or <c>false</c>, the endpoint
    /// behaves exactly as before the flag existed — an anonymous caller gets 401 and only
    /// authenticated actors, bound by the account-creation/assignment matrix in
    /// <see cref="Implementations.UserService"/>, may create users. When <c>true</c>, an
    /// anonymous create is allowed and the account is ALWAYS a
    /// <see cref="Domain.UserRole.SystemAdmin"/> (the bootstrap account); any role the
    /// payload carries is ignored on that path.
    /// </summary>
    public bool AllowAnonymousSignup { get; set; }

    /// <summary>
    /// Fail-closed parse of <see cref="AllowAnonymousSignup"/> from configuration: only an
    /// explicit <c>"true"</c> (case-insensitive) turns the flag ON; an absent, empty or
    /// unparseable value is OFF and this never throws. This is the ONE parser used by the
    /// composition-root binding, the service layer and the tests, so a malformed value can
    /// never be interpreted two different ways (and can never surface as a 500).
    /// </summary>
    /// <param name="configuration">The configuration root to read the flag from.</param>
    /// <returns><c>true</c> only when the flag is explicitly <c>"true"</c>.</returns>
    public static bool ParseAllowAnonymousSignup(IConfiguration? configuration)
        => bool.TryParse(configuration?[AllowAnonymousSignupKey], out var allow) && allow;
}
