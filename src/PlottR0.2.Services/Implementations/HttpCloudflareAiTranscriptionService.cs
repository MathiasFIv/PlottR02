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

public sealed class HttpCloudflareAiTranscriptionService : ITranscriptionService
{
    private const string ApiBaseUrl = "https://api.cloudflare.com/client/v4";

    private const string TranscribeTask = "transcribe";

    private const int MaxErrorDetailLength = 500;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CloudflareAiOptions _options;
    private readonly ILogger<HttpCloudflareAiTranscriptionService> _logger;

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

    /// <summary>
    /// Model id is part of the URL path and must not be URL-encoded; a 200 with success:false
    /// still throws. The client timeout is disabled so the configured timeout (via the linked
    /// token) is the only limit, and the caller's audio stream is read but never disposed here.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);

        var accountId = _options.AccountId.TrimEnd('/');
        var requestUrl = $"{ApiBaseUrl}/accounts/{accountId}/ai/run/{_options.Model}";

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var requestCt = linkedCts.Token;

        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, requestCt).ConfigureAwait(false);
        var audioBase64 = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length);

        var payload = JsonSerializer.Serialize(new CloudflareAiRequest { Audio = audioBase64 });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        _logger.LogInformation(
            "Transcribing '{FileName}' via Cloudflare Workers AI model {Model}.",
            fileName, _options.Model);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);

        var client = _httpClientFactory.CreateClient();

        client.Timeout = Timeout.InfiniteTimeSpan;

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCt)
            .ConfigureAwait(false);

        var body = await response.Content
            .ReadAsStringAsync(requestCt)
            .ConfigureAwait(false);

        var envelope = TryParse(body);

        if (!response.IsSuccessStatusCode || envelope?.Success == false)
        {
            throw new HttpRequestException(
                $"Cloudflare Workers AI model {_options.Model} returned " +
                $"{(int)response.StatusCode} {response.ReasonPhrase} while transcribing " +
                $"'{fileName}'.{DescribeErrors(envelope)}");
        }

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

    private static string FormatError(CloudflareAiMessage entry)
    {
        var code = FormatCode(entry.Code);

        if (string.IsNullOrWhiteSpace(entry.Message))
            return code;

        return string.IsNullOrWhiteSpace(code) ? entry.Message : $"[{code}] {entry.Message}";
    }

    private static string FormatCode(JsonElement? code)
    {
        if (code is not { } element)
            return string.Empty;

        return element.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? element.ToString()
            : string.Empty;
    }

    private sealed class CloudflareAiRequest
    {
        [JsonPropertyName("audio")]
        public string Audio { get; init; } = string.Empty;

        [JsonPropertyName("task")]
        public string Task { get; init; } = TranscribeTask;

        [JsonPropertyName("vad_filter")]
        public bool VadFilter { get; init; } = true;
    }

    private sealed class CloudflareAiResponse
    {
        [JsonPropertyName("result")]
        public CloudflareAiResult? Result { get; init; }

        [JsonPropertyName("success")]
        public bool? Success { get; init; }

        [JsonPropertyName("errors")]
        public List<CloudflareAiMessage>? Errors { get; init; }

        [JsonPropertyName("messages")]
        public List<CloudflareAiMessage>? Messages { get; init; }
    }

    private sealed class CloudflareAiResult
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("transcription_info")]
        public CloudflareAiTranscriptionInfo? TranscriptionInfo { get; init; }

        [JsonPropertyName("language")]
        public string? Language { get; init; }

        [JsonPropertyName("duration")]
        public double? Duration { get; init; }
    }

    private sealed class CloudflareAiTranscriptionInfo
    {
        [JsonPropertyName("language")]
        public string? Language { get; init; }

        [JsonPropertyName("duration")]
        public double? Duration { get; init; }
    }

    private sealed class CloudflareAiMessage
    {
        [JsonPropertyName("code")]
        public JsonElement? Code { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }
}
