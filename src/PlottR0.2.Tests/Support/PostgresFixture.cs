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

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("plottr02_test")
        .WithUsername("plottr02")
        .WithPassword("plottr02")
        .Build();

    private IServiceProvider? _services;

    public IServiceProvider Services => _services
        ?? throw new InvalidOperationException("PostgresFixture has not been initialized yet.");

    public TestCurrentUser CurrentUser { get; } = new();

    public IConfigurationRoot Configuration { get; } =
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    public void SetAllowAnonymousSignup(bool allow)
        => SetAllowAnonymousSignupRaw(allow ? "true" : "false");

    public void SetAllowAnonymousSignupRaw(string? rawValue)
        => Configuration[AuthOptions.AllowAnonymousSignupKey] = rawValue;

    public IServiceScope CreateScope() => Services.CreateScope();

    public T GetService<T>() where T : notnull
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlottR0DbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"RefreshTokens\", \"AuditLogs\", \"Assignments\", \"Trials\", \"VoiceRecordings\", \"Users\" RESTART IDENTITY CASCADE;");
    }

    /// <summary>
    /// The host binds AuthOptions once at startup, but this fixture builds its DI graph once per
    /// test class while tests flip the flag per test; registering IOptions&lt;AuthOptions&gt; as
    /// scoped (through the app's own fail-closed parser) lets each test observe its own value
    /// instead of a stale cached singleton, without changing the production options contract.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigurePersistence(_container.GetConnectionString());
        services.ConfigureApplication();
        services.AddSingleton<ICurrentUser>(CurrentUser);

        services.AddScoped<IOptions<AuthOptions>>(_ =>
            Options.Create(new AuthOptions
            {
                AllowAnonymousSignup = AuthOptions.ParseAllowAnonymousSignup(Configuration),
            }));

        _services = services.BuildServiceProvider();

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

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
