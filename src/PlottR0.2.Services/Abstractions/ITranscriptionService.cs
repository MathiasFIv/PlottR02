using PlottR0._2.Contracts.Transcription;

namespace PlottR0._2.Services.Abstractions;

/// <summary>
/// Transcribes audio clips to text through the configured speech-to-text sidecar
/// (a local FasterWhisper HTTP service wrapped by the Python sidecar project).
/// Produces raw transcript text only — interpretation of that text into plot/subsample/
/// value fields is a separate concern (<see cref="PlottR0._2.Services.Implementations.TranscriptParser"/>).
/// </summary>
public interface ITranscriptionService
{
    /// <summary>
    /// Transcribes one audio clip and returns the recognized text plus sidecar metadata.
    /// </summary>
    /// <param name="audio">The audio bytes to transcribe. Owned by the caller — the
    /// implementation never disposes this stream.</param>
    /// <param name="fileName">The source file name (used to convey the format to the
    /// sidecar's multipart upload).</param>
    /// <param name="ct">Cancellation token; also bounded by the configured sidecar timeout.</param>
    /// <returns>
    /// The transcription result. A <see cref="TranscriptionResult.Text"/> of empty string is
    /// valid — it means the clip carried no understandable speech, not that the call failed.
    /// </returns>
    /// <exception cref="HttpRequestException">The sidecar answered with a non-success status.</exception>
    Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken ct = default);
}