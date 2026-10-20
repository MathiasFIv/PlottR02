using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Configuration;
using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Services;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// AddOptions() is registered unconditionally: without it IOptions&lt;T&gt; would only resolve
    /// where AddHttpClient() happens to register them — an implicit dependency that would silently
    /// break if that call moved.
    /// </summary>
    public static IServiceCollection ConfigureApplication(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();

        if (configuration is not null)
        {
            services.AddOptions<TokenOptions>()
                .Bind(configuration.GetSection(TokenOptions.SectionName));

            services.AddOptions<FasterWhisperOptions>()
                .Bind(configuration.GetSection(FasterWhisperOptions.SectionName));

            services.AddOptions<CloudflareAiOptions>()
                .Bind(configuration.GetSection(CloudflareAiOptions.SectionName));
        }

        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITrialService, TrialService>();
        services.AddScoped<IRecordingSyncService, RecordingSyncService>();
        services.AddScoped<IVoiceCellEditService, VoiceCellEditService>();

        services.AddSingleton<ITrialWriteGate, TrialWriteGate>();

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
