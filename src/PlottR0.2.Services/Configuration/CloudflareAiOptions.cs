namespace PlottR0._2.Services.Configuration;

/// <summary>
/// Settings for the Cloudflare Workers AI speech-to-text provider, bound from the
/// <c>CloudflareAi</c> configuration section. Lives inside Services (not Api) so the
/// transcription feature stays self-contained and the Api project only wires it up via
/// <see cref="Microsoft.Extensions.Options.IOptions{T}"/>. This is the production
/// counterpart to the local dev-only <see cref="FasterWhisperOptions"/> sidecar: when
/// <see cref="IsConfigured"/> is <c>true</c> the composition root selects the Cloudflare
/// implementation over the sidecar.
/// </summary>
public sealed class CloudflareAiOptions
{
    /// <summary>
    /// The configuration section name this options type binds to.
    /// </summary>
    public const string SectionName = "CloudflareAi";

    /// <summary>
    /// The Cloudflare account id that owns the Workers AI binding. Used to build the
    /// REST endpoint <c>/accounts/{AccountId}/ai/run/{Model}</c>. Not a secret.
    /// </summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>
    /// SECRET: the Workers AI API token sent as the <c>Authorization: Bearer</c> header.
    /// Never log this value — it grants transcription access to the whole account.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// The Workers AI model used for speech-to-text. The default is Cloudflare's
    /// Whisper large-v3-turbo build, whose output nests <c>language</c>/<c>duration</c> under
    /// <c>result.transcription_info</c> (with <c>text</c> at the result root); the service
    /// maps those into <see cref="PlottR0._2.Contracts.Transcription.TranscriptionResult"/>
    /// and tolerates models that report them flat instead.
    /// </summary>
    public string Model { get; set; } = "@cf/openai/whisper-large-v3-turbo";

    /// <summary>
    /// Per-request timeout in seconds. A transcription that outlives this is aborted
    /// rather than hanging the caller indefinitely.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Whether the provider has enough configuration to be used (both <see cref="AccountId"/>
    /// and <see cref="ApiToken"/> present). Drives provider selection in
    /// <see cref="ServiceCollectionExtensions.ConfigureApplication"/>: unconfigured (the
    /// default, and the shape when the section is absent) falls back to the FasterWhisper
    /// sidecar.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountId) && !string.IsNullOrWhiteSpace(ApiToken);
}
