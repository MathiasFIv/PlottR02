using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlottR0._2.Contracts;
using PlottR0._2.Repository;
using PlottR0._2.Repository.Persistence;
using PlottR0._2.Services;
using Testcontainers.PostgreSql;

namespace PlottR0._2.Tests.Support;

/// <summary>
/// xUnit <see cref="IAsyncLifetime"/> fixture that starts a disposable PostgreSQL
/// container via Testcontainers and wires a fully migrated <see cref="PlottR0DbContext"/>
/// plus the full persistence + service DI graph against it. Shared across an entire
/// test collection via <see cref="CollectionDefinitionAttribute"/>.
///
/// All tests in this solution run inside Docker (Testcontainers) — no local Postgres
/// install is required. The container is torn down automatically when the collection
/// finishes. Tests are isolated from one another by calling <see cref="ResetDatabaseAsync"/>
/// (typically from an async test-lifecycle hook).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("plottr02_test")
        .WithUsername("plottr02")
        .WithPassword("plottr02")
        .Build();

    private IServiceProvider? _services;

    /// <summary>
    /// The root service provider for the running test container. Tests should resolve
    /// scoped services by calling <see cref="CreateScope"/> (each DbContext is scoped,
    /// so fresh repositories must come from a scope).
    /// </summary>
    public IServiceProvider Services => _services
        ?? throw new InvalidOperationException("PostgresFixture has not been initialized yet.");

    /// <summary>
    /// The <see cref="ICurrentUser"/> singleton registered in the test DI container.
    /// Tests can mutate its identity to simulate an authenticated actor.
    /// </summary>
    public TestCurrentUser CurrentUser { get; } = new();

    /// <summary>
    /// Opens a new DI scope. Use to resolve scoped repositories/services and dispose
    /// when done (returns a <see cref="ServiceProvider"/> you must own and dispose).
    /// </summary>
    public IServiceScope CreateScope() => Services.CreateScope();

    /// <summary>Resolves a scoped service from a newly created scope (owned/disposed here).</summary>
    public T GetService<T>() where T : notnull
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// Wipes all application tables so every test starts from a clean database.
    /// Call between tests (e.g. from an async <c>DisposeAsync</c> hook) to prevent
    /// cross-test pollution — the container persists across the whole collection.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlottR0DbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"RefreshTokens\", \"AuditLogs\", \"Ratings\", \"Subsamples\", \"Plots\", \"Trials\", \"Users\" RESTART IDENTITY CASCADE;");
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging(); // registers ILogger<T> (required by AuditService)
        services.ConfigurePersistence(_container.GetConnectionString());
        services.ConfigureApplication();
        services.AddSingleton<ICurrentUser>(CurrentUser);

        _services = services.BuildServiceProvider();

        // Apply all EF Core migrations (mirrors the Api's auto-migrate on startup).
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlottR0DbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_services is IDisposable disposable)
            disposable.Dispose();

        await _container.DisposeAsync();
    }
}

/// <summary>
/// Names the shared xUnit collection that owns the single <see cref="PostgresFixture"/>.
/// Decorate test classes with <c>[Collection(nameof(PostgresCollection))]</c> to share it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";

    // Empty collection definition — the fixture provides the container lifecycle.
}
