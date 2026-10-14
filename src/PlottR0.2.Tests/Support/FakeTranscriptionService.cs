using PlottR0._2.Contracts.Transcription;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Tests.Support;

/// <summary>
/// Test double for <see cref="ITranscriptionService"/> — returns scripted results from a
/// queue, one per call, so tests can drive exact transcripts and prove call counts.
/// Throws once the queue is exhausted, surfacing unexpected extra calls as a test failure.
/// </summary>
public sealed class FakeTranscriptionService : ITranscriptionService
{
    private readonly Queue<TranscriptionResult> _results;

    /// <summary>Number of <see cref="TranscribeAsync"/> calls made so far.</summary>
    public int CallCount { get; private set; }

    /// <summary>
    /// Creates a fake that returns each of <paramref name="results"/> in order.
    /// </summary>
    /// <param name="results">The scripted results, at least one. An empty queue makes the
    /// first call throw — equivalent to asserting the service is never used.</param>
    public FakeTranscriptionService(params TranscriptionResult[] results)
    {
        ArgumentNullException.ThrowIfNull(results);
        _results = new Queue<TranscriptionResult>(results);
    }

    /// <inheritdoc />
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