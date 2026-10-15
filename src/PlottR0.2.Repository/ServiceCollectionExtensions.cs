using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Repository.Configuration;
using PlottR0._2.Repository.Persistence;
using PlottR0._2.Repository.Repositories;
using PlottR0._2.Repository.Storage;

namespace PlottR0._2.Repository;

/// <summary>
/// Extension methods for <see cref="IServiceCollection"/> that wire up the
/// persistence layer (EF Core + PostgreSQL), the file-system stores with an optional
/// durable object store, and repository implementations.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PlottR0DbContext"/> with Npgsql and all repository
    /// implementations as scoped services.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <param name="enableDetailedErrors">
    /// When <c>true</c>, EF Core includes detailed database errors in exception messages.
    /// Recommended for Development only.
    /// </param>
    /// <param name="configuration">
    /// Optional configuration root used to bind <see cref="StorageOptions"/> and
    /// <see cref="ObjectStorageOptions"/>. When omitted (e.g. the parameterless call),
    /// the settings resolve to their defaults — the legacy repo-root <c>Service/</c> cache
    /// and durable storage disabled.
    /// </param>
    /// <returns>The supplied <paramref name="services"/> for fluent chaining.</returns>
    public static IServiceCollection ConfigurePersistence(
        this IServiceCollection services,
        string connectionString,
        bool enableDetailedErrors = false,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<PlottR0DbContext>(options =>
        {
            options.UseNpgsql(connectionString);

            if (enableDetailedErrors)
                options.EnableDetailedErrors();
        });

        // Bind the storage settings. With no configuration supplied, bind an empty root so the
        // options resolve to their defaults (legacy Service/ walk, durable storage disabled).
        var config = configuration ?? new ConfigurationBuilder().Build();
        services.AddOptions<StorageOptions>()
            .Bind(config.GetSection(StorageOptions.SectionName));
        services.AddOptions<ObjectStorageOptions>()
            .Bind(config.GetSection(ObjectStorageOptions.SectionName));

        // The configured cache root: a blank value or the "Service" sentinel means "walk to the
        // repo-root Service/ directory" (null → the store's ResolveServiceDirectory fallback), so
        // tests and local development keep resolving the shared workspace unchanged. Any other
        // value is used verbatim.
        var configuredRoot = config.GetSection(StorageOptions.SectionName).GetValue<string>(nameof(StorageOptions.Root));
        var explicitRoot = string.IsNullOrWhiteSpace(configuredRoot)
            || string.Equals(configuredRoot, "Service", StringComparison.Ordinal)
                ? null
                : configuredRoot;

        services.AddSingleton<IObjectStore>(sp =>
        {
            var objectOptions = sp.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
            return objectOptions.Enabled
                && !string.Equals(objectOptions.Provider, "None", StringComparison.Ordinal)
                    ? new S3ObjectStore(
                        sp.GetRequiredService<IOptions<ObjectStorageOptions>>(),
                        sp.GetService<ILogger<S3ObjectStore>>() ?? NullLogger<S3ObjectStore>.Instance)
                    : NullObjectStore.Instance;
        });

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRatingRepository, ClosedXmlRatingRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<ITrialRepository, TrialRepository>();
        services.AddScoped<ITrialWorkbookStore>(sp => new TrialWorkbookStore(
            explicitRoot,
            sp.GetRequiredService<IObjectStore>(),
            sp.GetService<ILogger<TrialWorkbookStore>>()));
        services.AddScoped<IVoiceRecordingRepository, VoiceRecordingRepository>();
        services.AddScoped<IVoiceBlobStore>(sp => new VoiceBlobStore(
            explicitRoot,
            sp.GetRequiredService<IObjectStore>(),
            sp.GetService<ILogger<VoiceBlobStore>>()));

        return services;
    }
}
