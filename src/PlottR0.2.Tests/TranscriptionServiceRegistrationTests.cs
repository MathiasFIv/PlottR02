using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlottR0._2.Services;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Configuration;
using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Tests;

public sealed class TranscriptionServiceRegistrationTests
{
    [Fact]
    public void ConfigureApplication_CloudflareCredentialsPresent_SelectsCloudflareService()
    {
        var configuration = ConfigurationWith(accountId: "acct", apiToken: "token");

        using var provider = BuildProvider(configuration);

        var service = provider.GetRequiredService<ITranscriptionService>();

        service.Should().BeOfType<HttpCloudflareAiTranscriptionService>();
    }

    [Fact]
    public void ConfigureApplication_CloudflareSectionAbsent_SelectsFasterWhisperService()
    {
        var configuration = new ConfigurationBuilder().Build();

        using var provider = BuildProvider(configuration);

        var service = provider.GetRequiredService<ITranscriptionService>();

        service.Should().BeOfType<HttpFasterWhisperTranscriptionService>();
    }

    [Theory]
    [InlineData("acct", "")]
    [InlineData("", "token")]
    [InlineData("   ", "   ")]
    public void ConfigureApplication_CloudflarePartiallyConfigured_SelectsFasterWhisperService(
        string accountId, string apiToken)
    {
        var configuration = ConfigurationWith(accountId, apiToken);

        using var provider = BuildProvider(configuration);

        var service = provider.GetRequiredService<ITranscriptionService>();

        service.Should().BeOfType<HttpFasterWhisperTranscriptionService>();
    }

    [Fact]
    public void ConfigureApplication_Parameterless_ResolvesFasterWhisperService_WithoutThrowing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureApplication();

        using var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<ITranscriptionService>();

        service.Should().BeOfType<HttpFasterWhisperTranscriptionService>();
    }

    [Fact]
    public void ConfigureApplication_RegistersBothConcreteProvidersAsThemselves()
    {
        using var provider = BuildProvider(new ConfigurationBuilder().Build());

        var act = () =>
        {
            provider.GetRequiredService<HttpFasterWhisperTranscriptionService>();
            provider.GetRequiredService<HttpCloudflareAiTranscriptionService>();
        };

        act.Should().NotThrow();
    }

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
        services.AddLogging();
        services.ConfigureApplication(configuration);
        return services.BuildServiceProvider();
    }
}
