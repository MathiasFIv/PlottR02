namespace PlottR0._2.Services.Configuration;

/// <summary>
/// Settings for the local FasterWhisper speech-to-text sidecar, bound from the
/// <c>FasterWhisper</c> configuration section. Lives inside Services (not Api) so the
/// transcription feature stays self-contained and the Api project only wires it up via
/// <see cref="Microsoft.Extensions.Options.IOptions{T}"/>.
/// </summary>
public sealed class FasterWhisperOptions
{
    /// <summary>
    /// The configuration section name this options type binds to.
    /// </summary>
    public const string SectionName = "FasterWhisper";

    /// <summary>
    /// Base URL of the sidecar HTTP service; the transcription endpoint is
    /// <c>{BaseUrl}/transcribe</c>.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>
    /// The Whisper model the sidecar loads (e.g. <c>"base"</c>, <c>"small"</c>).
    /// Largely informational to the sidecar; kept in configuration so deployments can
    /// describe the model they run.
    /// </summary>
    public string Model { get; set; } = "base";

    /// <summary>
    /// Per-request timeout in seconds. A transcription that outlives this is aborted
    /// rather than hanging the caller indefinitely.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;
}