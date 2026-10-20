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

public static class ServiceCollectionExtensions
{
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

        var config = configuration ?? new ConfigurationBuilder().Build();
        services.AddOptions<StorageOptions>()
            .Bind(config.GetSection(StorageOptions.SectionName));
        services.AddOptions<ObjectStorageOptions>()
            .Bind(config.GetSection(ObjectStorageOptions.SectionName));

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
