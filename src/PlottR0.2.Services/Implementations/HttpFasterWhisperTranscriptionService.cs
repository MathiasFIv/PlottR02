using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlottR0._2.Contracts.Transcription;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Configuration;

namespace PlottR0._2.Services.Implementations;

/// <summary>
/// Concrete <see cref="ITranscriptionService"/> that pushes audio to the local
/// FasterWhisper sidecar (a FastAPI wrapper around CTranslate2 Whisper) as a
/// multipart/form-data upload and maps the JSON response into a
/// <see cref="TranscriptionResult"/>. The client comes from
/// <see cref="IHttpClientFactory"/> so socket lifetimes and DNS are managed by the
/// framework; every request is bounded by <see cref="FasterWhisperOptions.TimeoutSeconds"/>.
/// The caller's audio stream is never disposed here: the request content wrapper is left
/// to GC so ownership of the stream stays with the caller (the sync service opens and
/// closes clip files).
/// </summary>
public sealed class HttpFasterWhisperTranscriptionService : ITranscriptionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FasterWhisperOptions _options;
    private readonly ILogger<HttpFasterWhisperTranscriptionService> _logger;

    /// <summary>
    /// Creates a new <see cref="HttpFasterWhisperTranscriptionService"/>.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory to obtain clients from.</param>
    /// <param name="options">The bound FasterWhisper sidecar settings.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
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

    /// <inheritdoc />
    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);

        var sidecarUrl = $"{_options.BaseUrl.TrimEnd('/')}/transcribe";

        // Bound the request to the configured timeout even when the caller passes no token.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var requestCt = linkedCts.Token;

        // Field name "audio" is what the sidecar's /transcribe endpoint expects. The
        // StreamContent (and the request carrying it) is intentionally not disposed —
        // disposing it would close the caller's stream, which we do not own.
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