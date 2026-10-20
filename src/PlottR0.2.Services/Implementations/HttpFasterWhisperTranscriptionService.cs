using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlottR0._2.Contracts.Transcription;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Configuration;

namespace PlottR0._2.Services.Implementations;

public sealed class HttpFasterWhisperTranscriptionService : ITranscriptionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FasterWhisperOptions _options;
    private readonly ILogger<HttpFasterWhisperTranscriptionService> _logger;

    public HttpFasterWhisperTranscriptionService(
        IHttpClientFactory httpClientFactory,
        IOptions<FasterWhisperOptions> options,
        ILogger<HttpFasterWhisperTranscriptionService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Streams the audio to the sidecar; the caller's stream is never disposed here.</summary>
    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);

        var sidecarUrl = $"{_options.BaseUrl.TrimEnd('/')}/transcribe";

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var requestCt = linkedCts.Token;

        var form = new MultipartFormDataContent();
        form.Add(new StreamContent(audio), "audio", fileName);

        _logger.LogInformation(
            "Transcribing '{FileName}' via FasterWhisper sidecar at {SidecarUrl}.",
            fileName, sidecarUrl);

        var request = new HttpRequestMessage(HttpMethod.Post, sidecarUrl) { Content = form };

        using var response = await _httpClientFactory.CreateClient()
            .SendAsync(request, requestCt)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"FasterWhisper sidecar at {sidecarUrl} returned {(int)response.StatusCode} " +
                $"{response.ReasonPhrase} while transcribing '{fileName}'.");
        }

        var result = await response.Content
            .ReadFromJsonAsync<TranscriptionResult>(cancellationToken: requestCt)
            .ConfigureAwait(false) ?? new TranscriptionResult();

        _logger.LogDebug(
            "Transcribed '{FileName}' (language: {Language}, duration: {Duration}s, text: '{Text}').",
            fileName, result.Language, result.Duration, result.Text);

        return result;
    }
}