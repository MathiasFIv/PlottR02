namespace PlottR0._2.Services.Configuration;

public sealed class CloudflareAiOptions
{
    public const string SectionName = "CloudflareAi";

    public string AccountId { get; set; } = string.Empty;

    public string ApiToken { get; set; } = string.Empty;

    public string Model { get; set; } = "@cf/openai/whisper-large-v3-turbo";

    public int TimeoutSeconds { get; set; } = 120;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountId) && !string.IsNullOrWhiteSpace(ApiToken);
}
