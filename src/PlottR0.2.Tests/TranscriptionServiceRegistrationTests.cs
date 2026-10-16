using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlottR0._2.Services;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Configuration;
using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Tests;

/// <summary>
/// Provider-selection tests for <see cref="ServiceCollectionExtensions.ConfigureApplication"/>:
/// the <c>ITranscriptionService</c> factory must choose Cloudflare Workers AI when the
/// <c>CloudflareAi</c> section carries both credentials, and the local FasterWhisper sidecar
/// otherwise — including the parameterless (no configuration) call the test fixture uses.
/// </summary>
public sealed class TranscriptionServiceRegistrationTests
{
    [Fact]
    public void ConfigureApplication_CloudflareCredentialsPresent_SelectsCloudflareService()
    {
        // Arrange — both credentials set ⇒ the production provider wins.
        var configuration = ConfigurationWith(accountId: "acct", apiToken: "token");

        using var provider = BuildProvider(configuration);

        // Act
        var service = provider.GetRequiredService<ITranscriptionService>();

        // Assert
        service.Should().BeOfType<HttpCloudflareAiTranscriptionService>();
    }

    [Fact]
    public void ConfigureApplication_CloudflareSectionAbsent_SelectsFasterWhisperService()
    {
        // Arrange — no CloudflareAi section at all: IsConfigured stays false.
        var configuration = new ConfigurationBuilder().Build();

        using var provider = BuildProvider(configuration);

        // Act
        var service = provider.GetRequiredService<ITranscriptionService>();

        // Assert
        service.Should().BeOfType<HttpFasterWhisperTranscriptionService>();
    }

    [Theory]
    [InlineData("acct", "")]
    [InlineData("", "token")]
    [InlineData("   ", "   ")]
    public void ConfigureApplication_CloudflarePartiallyConfigured_SelectsFasterWhisperService(
        string accountId, string apiToken)
    {
        // Arrange — a half-configured provider (or whitespace) must NOT be selected.
        var configuration = ConfigurationWith(accountId, apiToken);

        using var provider = BuildProvider(configuration);

        // Act
        var service = provider.GetRequiredService<ITranscriptionService>();

        // Assert
        service.Should().BeOfType<HttpFasterWhisperTranscriptionService>();
    }

    [Fact]
    public void ConfigureApplication_Parameterless_ResolvesFasterWhisperService_WithoutThrowing()
    {
        // Arrange — the fixture calls ConfigureApplication() with no configuration; the
        // options must resolve to defaults (IsConfigured false) rather than NRE.
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureApplication();

        using var provider = services.BuildServiceProvider();

        // Act
        var service = provider.GetRequiredService<ITranscriptionService>();

        // Assert
        service.Should().BeOfType<HttpFasterWhisperTranscriptionService>();
    }

    [Fact]
    public void ConfigureApplication_RegistersBothConcreteProvidersAsThemselves()
    {
        // Arrange — both concrete implementations must be resolvable by their own type
        // (not only behind ITranscriptionService), for direct injection/testing.
        using var provider = BuildProvider(new ConfigurationBuilder().Build());

        // Act
        var act = () =>
        {
            provider.GetRequiredService<HttpFasterWhisperTranscriptionService>();
            provider.GetRequiredService<HttpCloudflareAiTranscriptionService>();
        };

        // Assert — resolution does not throw.
        act.Should().NotThrow();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static IConfiguration ConfigurationWith(string accountId, string apiToken)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{CloudflareAiOptions.SectionName}:AccountId"] = accountId,
                [$"{CloudflareAiOptions.SectionName}:ApiToken"] = apiToken,
            })
            .Build();

    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging(); // ILogger<T> for the implementations (AddHttpClient also adds it)
        services.ConfigureApplication(configuration);
        return services.BuildServiceProvider();
    }
}
