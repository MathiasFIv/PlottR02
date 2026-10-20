using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlottR0._2.Services.Configuration;
using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Tests;

public sealed class HttpCloudflareAiTranscriptionServiceTests
{
    private const string AccountId = "test-account";
    private const string ApiToken = "test-token";
    private const string Model = "@cf/openai/whisper-large-v3-turbo";
    private static readonly byte[] AudioBytes = Encoding.UTF8.GetBytes("fake-audio-bytes");

    [Fact]
    public async Task TranscribeAsync_PostsBase64AudioAsJson_WithBearerToken()
    {
        const string responseBody =
            """{"result":{"text":"values 3 9 8","transcription_info":{"language":"en","duration":2.5}},"success":true,"errors":[]}""";
        var (service, handler) = Create(HttpStatusCode.OK, responseBody);
        using var audio = new MemoryStream(AudioBytes);

        var result = await service.TranscribeAsync(audio, "clip.webm");

        result.Text.Should().Be("values 3 9 8");

        handler.CapturedRequest.Should().NotBeNull();
        var request = handler.CapturedRequest!;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.Host.Should().Be("api.cloudflare.com");
        request.RequestUri.AbsolutePath.Should().Be($"/client/v4/accounts/{AccountId}/ai/run/{Model}");
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().Be(ApiToken);

        request.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(handler.CapturedBody!);
        json.RootElement.GetProperty("audio").GetString()
            .Should().Be(Convert.ToBase64String(AudioBytes), "the raw bytes must arrive base64-encoded");
        json.RootElement.GetProperty("task").GetString().Should().Be("transcribe");
        json.RootElement.GetProperty("vad_filter").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TranscribeAsync_TrimsTrailingSlashFromAccountId()
    {
        const string responseBody = """{"result":{"text":"ok"},"success":true,"errors":[]}""";
        var (service, handler) = Create(HttpStatusCode.OK, responseBody, accountId: $"{AccountId}/");
        using var audio = new MemoryStream(AudioBytes);

        await service.TranscribeAsync(audio, "clip.wav");

        handler.CapturedRequest!.RequestUri!.AbsolutePath
            .Should().Be($"/client/v4/accounts/{AccountId}/ai/run/{Model}");
        handler.CapturedRequest.Content!.Headers.ContentType!.MediaType
            .Should().Be("application/json", "the format is conveyed in the JSON body, not via Content-Type");
    }

    [Fact]
    public async Task TranscribeAsync_TurboShape_MapsTextAndNestedTranscriptionInfo()
    {
        const string responseBody =
            """{"result":{"text":"hej verden","transcription_info":{"language":"da","language_probability":0.99,"duration":3.2,"duration_after_vad":3.0},"word_count":2,"vtt":"WEBVTT"},"success":true,"errors":[]}""";
        var (service, _) = Create(HttpStatusCode.OK, responseBody);
        using var audio = new MemoryStream(AudioBytes);

        var result = await service.TranscribeAsync(audio, "clip.webm");

        result.Text.Should().Be("hej verden");
        result.Language.Should().Be("da");
        result.Duration.Should().Be(3.2);
    }

    [Fact]
    public async Task TranscribeAsync_FlatShape_MapsRootLanguageAndDuration()
    {
        const string responseBody =
            """{"result":{"text":"hello","language":"en","duration":1.25},"success":true,"errors":[]}""";
        var (service, _) = Create(HttpStatusCode.OK, responseBody);
        using var audio = new MemoryStream(AudioBytes);

        var result = await service.TranscribeAsync(audio, "clip.webm");

        result.Text.Should().Be("hello");
        result.Language.Should().Be("en");
        result.Duration.Should().Be(1.25);
    }

    [Fact]
    public async Task TranscribeAsync_NestedTranscriptionInfo_TakesPrecedenceOverFlat()
    {
        const string responseBody =
            """{"result":{"text":"x","language":"en","duration":9.9,"transcription_info":{"language":"da","duration":1.1}},"success":true,"errors":[]}""";
        var (service, _) = Create(HttpStatusCode.OK, responseBody);
        using var audio = new MemoryStream(AudioBytes);

        var result = await service.TranscribeAsync(audio, "clip.webm");

        result.Language.Should().Be("da");
        result.Duration.Should().Be(1.1);
    }

    [Fact]
    public async Task TranscribeAsync_MissingText_MapsToEmptyString()
    {
        const string responseBody = """{"result":{},"success":true,"errors":[]}""";
        var (service, _) = Create(HttpStatusCode.OK, responseBody);
        using var audio = new MemoryStream(AudioBytes);

        var result = await service.TranscribeAsync(audio, "clip.webm");

        result.Text.Should().BeEmpty();
        result.Language.Should().BeEmpty();
        result.Duration.Should().BeNull();
    }

    [Fact]
    public async Task TranscribeAsync_SuccessWithNonJsonBody_ReturnsEmptyResultWithoutThrowing()
    {
        var (service, _) = Create(HttpStatusCode.OK, "<html>ok</html>");
        using var audio = new MemoryStream(AudioBytes);

        var result = await service.TranscribeAsync(audio, "clip.webm");

        result.Text.Should().BeEmpty();
        result.Language.Should().BeEmpty();
        result.Duration.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task TranscribeAsync_NonSuccessStatus_ThrowsHttpRequestException(HttpStatusCode status)
    {
        const string responseBody =
            """{"result":null,"success":false,"errors":[{"code":7000,"message":"No route for that URI"}]}""";
        var (service, _) = Create(status, responseBody);
        using var audio = new MemoryStream(AudioBytes);

        var act = async () => await service.TranscribeAsync(audio, "clip.webm");

        var exception = await act.Should().ThrowAsync<HttpRequestException>();
        exception.Which.Message.Should().Contain(((int)status).ToString());
        exception.Which.Message.Should().Contain("clip.webm");
        exception.Which.Message.Should().Contain("7000");
        exception.Which.Message.Should().Contain("No route for that URI");
    }

    [Fact]
    public async Task TranscribeAsync_OkStatusButSuccessFalse_ThrowsHttpRequestException()
    {
        const string responseBody =
            """{"result":null,"success":false,"errors":[{"code":5005,"message":"model not found"}]}""";
        var (service, _) = Create(HttpStatusCode.OK, responseBody);
        using var audio = new MemoryStream(AudioBytes);

        var act = async () => await service.TranscribeAsync(audio, "clip.webm");

        var exception = await act.Should().ThrowAsync<HttpRequestException>();
        exception.Which.Message.Should().Contain("5005");
        exception.Which.Message.Should().Contain("model not found");
    }

    [Fact]
    public async Task TranscribeAsync_StringErrorCode_StillThrowsAndSurfacesCode()
    {
        const string responseBody =
            """{"result":null,"success":false,"errors":[{"code":"bad_request","message":"invalid audio"}]}""";
        var (service, _) = Create(HttpStatusCode.OK, responseBody);
        using var audio = new MemoryStream(AudioBytes);

        var act = async () => await service.TranscribeAsync(audio, "clip.webm");

        var exception = await act.Should().ThrowAsync<HttpRequestException>();
        exception.Which.Message.Should().Contain("bad_request");
        exception.Which.Message.Should().Contain("invalid audio");
    }

    [Fact]
    public async Task TranscribeAsync_NonJsonErrorBody_StillThrowsHttpRequestException()
    {
        var (service, _) = Create(HttpStatusCode.BadGateway, "<html>bad gateway</html>");
        using var audio = new MemoryStream(AudioBytes);

        var act = async () => await service.TranscribeAsync(audio, "clip.webm");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task TranscribeAsync_DoesNotLeakApiTokenInExceptionMessage()
    {
        const string responseBody =
            """{"result":null,"success":false,"errors":[{"code":10000,"message":"Authentication error"}]}""";
        var (service, _) = Create(HttpStatusCode.Unauthorized, responseBody);
        using var audio = new MemoryStream(AudioBytes);

        var act = async () => await service.TranscribeAsync(audio, "clip.webm");

        var exception = await act.Should().ThrowAsync<HttpRequestException>();
        exception.Which.Message.Should().NotContain(ApiToken);
        exception.Which.Message.Should().NotContain("Bearer");
    }

    [Fact]
    public async Task TranscribeAsync_CallerTokenCancelled_ThrowsOperationCanceled()
    {
        const string responseBody = """{"result":{"text":"ok"},"success":true,"errors":[]}""";
        var (service, _) = Create(HttpStatusCode.OK, responseBody);
        using var audio = new MemoryStream(AudioBytes);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await service.TranscribeAsync(audio, "clip.webm", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TranscribeAsync_TimeoutSecondsElapsed_ThrowsOperationCanceled()
    {
        var handler = new StubHttpMessageHandler(Timeout.InfiniteTimeSpan);
        var service = BuildService(handler, timeoutSeconds: 1);
        using var audio = new MemoryStream(AudioBytes);
        var stopwatch = Stopwatch.StartNew();

        var act = async () => await service.TranscribeAsync(audio, "clip.webm");

        await act.Should().ThrowAsync<OperationCanceledException>();
        stopwatch.Stop();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "the timeout must abort promptly");
    }

    [Fact]
    public async Task TranscribeAsync_DoesNotDisposeCallerStream()
    {
        const string responseBody = """{"result":{"text":"ok"},"success":true,"errors":[]}""";
        var (service, _) = Create(HttpStatusCode.OK, responseBody);
        var audio = new MemoryStream(AudioBytes);

        await service.TranscribeAsync(audio, "clip.webm");

        audio.CanRead.Should().BeTrue("the service must never dispose the caller-owned stream");
        audio.CanSeek.Should().BeTrue();
    }

    [Fact]
    public async Task TranscribeAsync_NullAudio_ThrowsArgumentNullException()
    {
        const string responseBody = """{"result":{"text":"ok"},"success":true,"errors":[]}""";
        var (service, _) = Create(HttpStatusCode.OK, responseBody);

        var act = async () => await service.TranscribeAsync(null!, "clip.webm");

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static (HttpCloudflareAiTranscriptionService Service, StubHttpMessageHandler Handler) Create(
        HttpStatusCode status,
        string responseBody,
        string accountId = AccountId)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });

        return (BuildService(handler, accountId: accountId), handler);
    }

    private static HttpCloudflareAiTranscriptionService BuildService(
        HttpMessageHandler handler,
        int timeoutSeconds = 120,
        string accountId = AccountId)
    {
        return new HttpCloudflareAiTranscriptionService(
            new StubHttpClientFactory(handler),
            Options.Create(new CloudflareAiOptions
            {
                AccountId = accountId,
                ApiToken = ApiToken,
                Model = Model,
                TimeoutSeconds = timeoutSeconds,
            }),
            NullLogger<HttpCloudflareAiTranscriptionService>.Instance);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpMessageHandler handler) => _client = new HttpClient(handler);

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _responder;
        private readonly TimeSpan? _hangFor;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        public StubHttpMessageHandler(TimeSpan hangFor) => _hangFor = hangFor;

        public HttpRequestMessage? CapturedRequest { get; private set; }

        public byte[]? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            if (request.Content is not null)
                CapturedBody = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            if (_hangFor is { } hangFor)
                await Task.Delay(hangFor, cancellationToken).ConfigureAwait(false);

            return _responder!(request);
        }
    }
}
