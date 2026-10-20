using PlottR0._2.Contracts.Transcription;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Tests.Support;

public sealed class FakeTranscriptionService : ITranscriptionService
{
    private readonly Queue<TranscriptionResult> _results;

    public int CallCount { get; private set; }

    public FakeTranscriptionService(params TranscriptionResult[] results)
    {
        ArgumentNullException.ThrowIfNull(results);
        _results = new Queue<TranscriptionResult>(results);
    }

    public Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken ct = default)
    {
        if (_results.Count == 0)
        {
            throw new InvalidOperationException(
                "FakeTranscriptionService queue is exhausted — more TranscribeAsync calls than scripted results.");
        }

        CallCount++;
        return Task.FromResult(_results.Dequeue());
    }
}