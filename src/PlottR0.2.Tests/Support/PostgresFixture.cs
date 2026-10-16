using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlottR0._2.Contracts;
using PlottR0._2.Repository;
using PlottR0._2.Repository.Persistence;
using PlottR0._2.Services;
using PlottR0._2.Services.Configuration;
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
    /// Mutable in-memory configuration used to drive the <c>Auth</c> feature flags
    /// (<see cref="UserService"/> resolves <c>Auth:AllowAnonymousSignup</c> through a scoped
    /// <see cref="IOptions{T}"/> built from this root). Tests set the flag via
    /// <see cref="SetAllowAnonymousSignup"/> / <see cref="SetAllowAnonymousSignupRaw"/> and the
    /// owning test class resets it after each test.
    /// </summary>
    public IConfigurationRoot Configuration { get; } =
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    /// <summary>
    /// Sets the <c>Auth:AllowAnonymousSignup</c> feature flag in the shared test
    /// configuration. Callers must restore it (see each test class's dispose hook) so the
    /// flag never leaks into the next test — the contract default is OFF.
    /// </summary>
    /// <param name="allow">The value to publish for the flag.</param>
    public void SetAllowAnonymousSignup(bool allow)
        => SetAllowAnonymousSignupRaw(allow ? "true" : "false");

    /// <summary>
    /// Publishes a raw string for <c>Auth:AllowAnonymousSignup</c> so tests can exercise
    /// malformed/empty values (e.g. <c>"notabool"</c>) and the fail-closed parser. Pass
    /// <c>null</c> to remove the key entirely (absent ⇒ OFF).
    /// </summary>
    /// <param name="rawValue">The raw configuration value, or <c>null</c> to clear the key.</param>
    public void SetAllowAnonymousSignupRaw(string? rawValue)
        => Configuration[AuthOptions.AllowAnonymousSignupKey] = rawValue;

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
            "TRUNCATE TABLE \"RefreshTokens\", \"AuditLogs\", \"Assignments\", \"Trials\", \"VoiceRecordings\", \"Users\" RESTART IDENTITY CASCADE;");
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging(); // registers ILogger<T> (required by AuditService)
        services.ConfigurePersistence(_container.GetConnectionString());
        services.ConfigureApplication();
        services.AddSingleton<ICurrentUser>(CurrentUser);

        // Feature flags: the production host binds AuthOptions once at startup, but this
        // fixture builds its DI graph once per test CLASS while tests flip the flag per test.
        // Registering IOptions<AuthOptions> as SCOPED (rebuilt on every CreateScope) through
        // the SAME fail-closed parser the app uses means each test observes its own value
        // instead of a stale cached singleton — without changing the production
        // IOptions<AuthOptions> contract the service consumes.
        services.AddScoped<IOptions<AuthOptions>>(_ =>
            Options.Create(new AuthOptions
            {
                AllowAnonymousSignup = AuthOptions.ParseAllowAnonymousSignup(Configuration),
            }));

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
