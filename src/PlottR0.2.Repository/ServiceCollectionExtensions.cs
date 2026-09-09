using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Repository.Persistence;
using PlottR0._2.Repository.Repositories;

namespace PlottR0._2.Repository;

/// <summary>
/// Extension methods for <see cref="IServiceCollection"/> that wire up the
/// persistence layer (EF Core + PostgreSQL) and register repository implementations.
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
    /// <returns>The supplied <paramref name="services"/> for fluent chaining.</returns>
    public static IServiceCollection ConfigurePersistence(
        this IServiceCollection services,
        string connectionString,
        bool enableDetailedErrors = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<PlottR0DbContext>(options =>
        {
            options.UseNpgsql(connectionString);

            if (enableDetailedErrors)
                options.EnableDetailedErrors();
        });

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRatingRepository, ClosedXmlRatingRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<ITrialRepository, TrialRepository>();
        services.AddScoped<ITrialWorkbookStore, TrialWorkbookStore>();

        return services;
    }
}
