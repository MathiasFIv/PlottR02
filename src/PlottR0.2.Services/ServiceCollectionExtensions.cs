using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

        // Register the options infrastructure unconditionally so IOptions<T> resolves even
        // on the no-configuration path (the parameterless call). Without this the providers
        // would only resolve options because AddHttpClient() happens to add the same services —
        // an implicit dependency that would silently break if that call moved.
        services.AddOptions();

        // Bind the settings sections the services consume (JWT/token, plus the two
        // speech-to-text providers) so their IOptions<T> resolve at runtime. Only when
        // configuration is available — keeps the parameterless call working (the options
        // then resolve to their defaults via the AddOptions() registration above).
        if (configuration is not null)
        {
            services.AddOptions<TokenOptions>()
                .Bind(configuration.GetSection(TokenOptions.SectionName));

            services.AddOptions<FasterWhisperOptions>()
                .Bind(configuration.GetSection(FasterWhisperOptions.SectionName));

            // Cloudflare Workers AI is the production STT provider (Path A, direct REST).
            // Bound alongside the sidecar so IOptions<CloudflareAiOptions> resolves the
            // provider-selection flag below; absent/empty credentials leave IsConfigured
            // false and the factory falls back to the sidecar.
            services.AddOptions<CloudflareAiOptions>()
                .Bind(configuration.GetSection(CloudflareAiOptions.SectionName));
        }

        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITrialService, TrialService>();
        services.AddScoped<IRecordingSyncService, RecordingSyncService>();

        // IHttpClientFactory plumbing (managed sockets/DNS) + the two transcription
        // implementations registered as themselves. NOT typed
        // AddHttpClient<ITranscriptionService, ...> registrations: each implementation's
        // constructor takes IHttpClientFactory (it builds per-request clients and enforces
        // its own TimeoutSeconds), whereas typed-client activation requires a constructor
        // accepting an HttpClient and would throw at runtime.
        //
        // ITranscriptionService is chosen by a factory from configuration, not a fixed
        // implementation: Cloudflare Workers AI (production) when its credentials are set,
        // otherwise the local FasterWhisper sidecar (dev). The factory only READS the bound
        // options; when configuration was never supplied (the parameterless call) the options
        // resolve to their defaults, so IsConfigured is false and the sidecar wins — the
        // factory never NREs.
        services.AddHttpClient();

        services.AddTransient<HttpFasterWhisperTranscriptionService>();
        services.AddTransient<HttpCloudflareAiTranscriptionService>();

        services.AddTransient<ITranscriptionService>(sp =>
        {
            var cloudflare = sp.GetRequiredService<IOptions<CloudflareAiOptions>>().Value;

            return cloudflare.IsConfigured
                ? sp.GetRequiredService<HttpCloudflareAiTranscriptionService>()
                : sp.GetRequiredService<HttpFasterWhisperTranscriptionService>();
        });

        return services;
    }
}
