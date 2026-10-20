using Microsoft.Extensions.Configuration;

namespace PlottR0._2.Services.Configuration;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public const string AllowAnonymousSignupKey = SectionName + ":AllowAnonymousSignup";

    public bool AllowAnonymousSignup { get; set; }

    public static bool ParseAllowAnonymousSignup(IConfiguration? configuration)
        => bool.TryParse(configuration?[AllowAnonymousSignupKey], out var allow) && allow;
}
