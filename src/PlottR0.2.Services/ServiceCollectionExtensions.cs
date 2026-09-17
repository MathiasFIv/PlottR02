using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Configuration;
using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Services;

/// <summary>
/// Extension methods for <see cref="IServiceCollection"/> that wire up the
/// application/business logic layer and register service implementations.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all service implementations as scoped services and binds the
    /// settings sections they consume.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">Optional configuration root used to bind options.
    /// When omitted (e.g. the parameterless call), settings are left unbound and
    /// options resolve to their defaults.</param>
    /// <returns>The supplied <paramref name="services"/> for fluent chaining.</returns>
    public static IServiceCollection ConfigureApplication(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Bind the JWT/token settings so IOptions<TokenOptions> resolves at runtime.
        // Only when configuration is available — keeps the parameterless call working.
        if (configuration is not null)
        {
            services.AddOptions<TokenOptions>()
                .Bind(configuration.GetSection(TokenOptions.SectionName));

            services.AddOptions<FasterWhisperOptions>()
                .Bind(configuration.GetSection(FasterWhisperOptions.SectionName));
        }

        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRecordRatingsService, RecordRatingsService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITrialService, TrialService>();
        services.AddScoped<IRecordingSyncService, RecordingSyncService>();

        // IHttpClientFactory plumbing (managed sockets/DNS) + a plain transient
        // registration for the transcription service. NOT a typed
        // AddHttpClient<ITranscriptionService, ...> registration: the implementation's
        // constructor takes IHttpClientFactory (it builds per-request clients and
        // enforces FasterWhisperOptions.TimeoutSeconds itself), whereas typed-client
        // activation requires a constructor accepting an HttpClient and would throw
        // at runtime.
        services.AddHttpClient();
        services.AddTransient<ITranscriptionService, HttpFasterWhisperTranscriptionService>();

        return services;
    }
}
