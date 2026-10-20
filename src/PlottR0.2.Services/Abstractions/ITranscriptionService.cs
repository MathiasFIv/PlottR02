using PlottR0._2.Contracts.Transcription;

namespace PlottR0._2.Services.Abstractions;

public interface ITranscriptionService
{
    /// <summary>
    /// Transcribes the supplied audio; an empty string is a valid "no speech" result, not a
    /// failure. The stream is never disposed here — the caller owns its lifetime.
    /// </summary>
    Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken ct = default);
}
