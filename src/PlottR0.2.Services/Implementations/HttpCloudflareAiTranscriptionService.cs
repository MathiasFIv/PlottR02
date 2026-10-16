using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlottR0._2.Contracts.Transcription;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Configuration;

namespace PlottR0._2.Services.Implementations;

/// <summary>
/// Concrete <see cref="ITranscriptionService"/> that pushes audio to Cloudflare Workers AI
/// (Path A, the direct REST <c>/accounts/{AccountId}/ai/run/{Model}</c> endpoint) and maps
/// the Workers AI envelope into a <see cref="TranscriptionResult"/>. The Whisper
/// large-v3-turbo build accepts ONLY a JSON body — <c>{"audio":"&lt;base64&gt;","task":"transcribe"}</c> —
/// because it declares <c>audio</c> as a base64 string (or a <c>{body,contentType}</c> object)
/// in its input schema; it has no raw-binary mode (unlike <c>@cf/openai/whisper</c>). The
/// caller's audio stream is copied into a buffer to base64 it and is never disposed — the
/// sync service owns and closes clip files. The client comes from
/// <see cref="IHttpClientFactory"/> so socket lifetimes and DNS are managed by the framework;
/// every request is bounded by <see cref="CloudflareAiOptions.TimeoutSeconds"/>.
/// </summary>
public sealed class HttpCloudflareAiTranscriptionService : ITranscriptionService
{
    /// <summary>
    /// The Workers AI REST root. The per-account run path is appended per request.
    /// </summary>
    private const string ApiBaseUrl = "https://api.cloudflare.com/client/v4";

    /// <summary>
    /// The transcription task sent in the request body. Transcribe is the schema default;
    /// it is sent explicitly so the wire contract is self-describing.
    /// </summary>
    private const string TranscribeTask = "transcribe";

    /// <summary>
    /// Maximum number of characters of the provider's error text folded into the thrown
    /// exception message. Cloudflare can echo large validation payloads; the log line is
    /// the place for the rest.
    /// </summary>
    private const int MaxErrorDetailLength = 500;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CloudflareAiOptions _options;
    private readonly ILogger<HttpCloudflareAiTranscriptionService> _logger;

    /// <summary>
    /// Creates a new <see cref="HttpCloudflareAiTranscriptionService"/>.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory to obtain clients from.</param>
    /// <param name="options">The bound Cloudflare Workers AI settings.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public HttpCloudflareAiTranscriptionService(
        IHttpClientFactory httpClientFactory,
        IOptions<CloudflareAiOptions> options,
        ILogger<HttpCloudflareAiTranscriptionService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);

        // The model id contains a '/' (e.g. "@cf/openai/whisper-large-v3-turbo") and is a path
        // segment of the run endpoint — it must NOT be URL-encoded. Only the account id's
        // trailing slash (if any) is trimmed so the URL never carries a doubled separator.
        var accountId = _options.AccountId.TrimEnd('/');
        var requestUrl = $"{ApiBaseUrl}/accounts/{accountId}/ai/run/{_options.Model}";

        // Bound the request to the configured timeout even when the caller passes no token.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var requestCt = linkedCts.Token;

        // The turbo models accept ONLY a JSON body: their input schema types "audio" as a
        // base64 string (or a {body,contentType} object), with no raw-binary option. Read the
        // caller's stream into a buffer and base64 it — the stream is left open (the sync
        // service owns it) and is merely read from its current position.
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, requestCt).ConfigureAwait(false);
        var audioBase64 = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length);

        var payload = JsonSerializer.Serialize(new CloudflareAiRequest { Audio = audioBase64 });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        // The log line deliberately carries only the model + file name: the endpoint embeds
        // the account id and the Authorization header carries the API token (a secret).
        _logger.LogInformation(
            "Transcribing '{FileName}' via Cloudflare Workers AI model {Model}.",
            fileName, _options.Model);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);

        var client = _httpClientFactory.CreateClient();

        // The framework default HttpClient.Timeout is 100 s, which would pre-empt a configured
        // timeout above that; disable it so the linked CTS above is the single source of truth.
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCt)
            .ConfigureAwait(false);

        // Read the body up front so a non-success status can still surface Cloudflare's
        // error code/message (the envelope is JSON even on failure).
        var body = await response.Content
            .ReadAsStringAsync(requestCt)
            .ConfigureAwait(false);

        var envelope = TryParse(body);

        // A non-success status OR an explicit "success": false must throw — the latter case
        // covers a 200 whose payload still reports a failure in the JSON envelope.
        if (!response.IsSuccessStatusCode || envelope?.Success == false)
        {
            throw new HttpRequestException(
                $"Cloudflare Workers AI model {_options.Model} returned " +
                $"{(int)response.StatusCode} {response.ReasonPhrase} while transcribing " +
                $"'{fileName}'.{DescribeErrors(envelope)}");
        }

        // "text" is top-level in the result; the turbo build nests language/duration under
        // "transcription_info". The flat fallbacks cover models that surface them at the root.
        var result = new TranscriptionResult
        {
            Text = envelope?.Result?.Text ?? string.Empty,
            Language = envelope?.Result?.TranscriptionInfo?.Language
                ?? envelope?.Result?.Language
                ?? string.Empty,
            Duration = envelope?.Result?.TranscriptionInfo?.Duration
                ?? envelope?.Result?.Duration,
        };

        _logger.LogDebug(
            "Transcribed '{FileName}' (language: {Language}, duration: {Duration}s, text: '{Text}').",
            fileName, result.Language, result.Duration, result.Text);

        return result;
    }

    /// <summary>
    /// Deserializes the Workers AI envelope, tolerating a non-JSON body (e.g. an edge/proxy
    /// HTML error page) by returning <c>null</c> — the caller's status check still throws.
    /// </summary>
    private static CloudflareAiResponse? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<CloudflareAiResponse>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Renders Cloudflare's <c>errors</c> (falling back to <c>messages</c>) as a bounded,
    /// single-line detail suffix for the thrown exception, or an empty string when the
    /// envelope was absent/unparsed.
    /// </summary>
    private static string DescribeErrors(CloudflareAiResponse? envelope)
    {
        var entries = envelope?.Errors is { Count: > 0 } ? envelope.Errors
            : envelope?.Messages is { Count: > 0 } ? envelope.Messages
            : null;

        if (entries is null)
            return string.Empty;

        var text = string.Join("; ", entries.Select(FormatError).Where(m => !string.IsNullOrWhiteSpace(m)));

        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        if (text.Length > MaxErrorDetailLength)
            text = text[..MaxErrorDetailLength] + "…";

        return $" Details: {text}";
    }

    /// <summary>Renders one error entry as <c>[code] message</c> (dropping whichever half is absent).</summary>
    private static string FormatError(CloudflareAiMessage entry)
    {
        var code = FormatCode(entry.Code);

        if (string.IsNullOrWhiteSpace(entry.Message))
            return code;

        return string.IsNullOrWhiteSpace(code) ? entry.Message : $"[{code}] {entry.Message}";
    }

    /// <summary>
    /// Renders a provider <c>code</c> that may be either a JSON string (e.g.
    /// <c>"bad_request"</c>) or a number (e.g. <c>7000</c>) — Cloudflare's schema is not
    /// consistent across error types.
    /// </summary>
    private static string FormatCode(JsonElement? code)
    {
        if (code is not { } element)
            return string.Empty;

        return element.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? element.ToString()
            : string.Empty;
    }

    /// <summary>
    /// The Workers AI request body for speech-to-text:
    /// <c>{ "audio": "&lt;base64&gt;", "task": "transcribe", "vad_filter": true }</c>.
    /// </summary>
    private sealed class CloudflareAiRequest
    {
        /// <summary>The audio bytes, base64-encoded (the schema's required <c>audio</c> field).</summary>
        [JsonPropertyName("audio")]
        public string Audio { get; init; } = string.Empty;

        /// <summary>The transcription task; always <c>"transcribe"</c>.</summary>
        [JsonPropertyName("task")]
        public string Task { get; init; } = TranscribeTask;

        /// <summary>
        /// Whether to strip silence before transcribing. Mirrors the local FasterWhisper
        /// sidecar (<c>vad_filter=True</c>) so both providers behave the same on quiet clips.
        /// </summary>
        [JsonPropertyName("vad_filter")]
        public bool VadFilter { get; init; } = true;
    }

    /// <summary>
    /// The Workers AI response envelope:
    /// <c>{ "result": {...}, "success": true, "errors": [], "messages": [] }</c>.
    /// </summary>
    private sealed class CloudflareAiResponse
    {
        /// <summary>The model output payload; absent when the call failed.</summary>
        [JsonPropertyName("result")]
        public CloudflareAiResult? Result { get; init; }

        /// <summary>
        /// The provider's success flag. Nullable so an absent flag (unexpected shape) is not
        /// mistaken for an explicit failure.
        /// </summary>
        [JsonPropertyName("success")]
        public bool? Success { get; init; }

        /// <summary>Provider-reported errors (present on failure).</summary>
        [JsonPropertyName("errors")]
        public List<CloudflareAiMessage>? Errors { get; init; }

        /// <summary>Provider messages; some failures report here instead of <c>errors</c>.</summary>
        [JsonPropertyName("messages")]
        public List<CloudflareAiMessage>? Messages { get; init; }
    }

    /// <summary>
    /// The <c>result</c> payload. <c>text</c> is top-level; the Whisper large-v3-turbo build
    /// nests <c>language</c>/<c>duration</c> under <c>transcription_info</c>, while other
    /// models expose them flat — hence both shapes.
    /// </summary>
    private sealed class CloudflareAiResult
    {
        /// <summary>The recognized speech, or null when the model reported none.</summary>
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        /// <summary>Language/duration metadata, as reported by the turbo build.</summary>
        [JsonPropertyName("transcription_info")]
        public CloudflareAiTranscriptionInfo? TranscriptionInfo { get; init; }

        /// <summary>Flat language fallback for models that surface it at the result root.</summary>
        [JsonPropertyName("language")]
        public string? Language { get; init; }

        /// <summary>Flat duration fallback for models that surface it at the result root.</summary>
        [JsonPropertyName("duration")]
        public double? Duration { get; init; }
    }

    /// <summary>The <c>result.transcription_info</c> object (turbo models).</summary>
    private sealed class CloudflareAiTranscriptionInfo
    {
        /// <summary>The detected language, when the model reports one.</summary>
        [JsonPropertyName("language")]
        public string? Language { get; init; }

        /// <summary>The audio duration in seconds, when the model reports one.</summary>
        [JsonPropertyName("duration")]
        public double? Duration { get; init; }
    }

    /// <summary>A single entry in the envelope's <c>errors</c>/<c>messages</c> arrays.</summary>
    private sealed class CloudflareAiMessage
    {
        /// <summary>
        /// The provider error code, when present. Typed as a raw <see cref="JsonElement"/> so
        /// either a string (<c>"bad_request"</c>) or a number (<c>7000</c>) deserializes —
        /// a mismatch would otherwise fail the whole envelope and defeat the
        /// "200 + success:false must throw" guarantee.
        /// </summary>
        [JsonPropertyName("code")]
        public JsonElement? Code { get; init; }

        /// <summary>The human-readable provider message, when present.</summary>
        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }
}
