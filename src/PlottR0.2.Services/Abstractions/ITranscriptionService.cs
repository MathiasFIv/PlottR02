using PlottR0._2.Contracts.Transcription;

namespace PlottR0._2.Services.Abstractions;

/// <summary>
/// Transcribes audio clips to text through the configured speech-to-text provider — either
/// the local FasterWhisper sidecar (a FastAPI HTTP service wrapped by the Python sidecar
/// project) or Cloudflare Workers AI. Which implementation is selected is a composition-root
/// concern driven by configuration.
/// Produces raw transcript text only — interpretation of that text into plot/subsample/
/// value fields is a separate concern (<see cref="PlottR0._2.Services.Implementations.TranscriptParser"/>).
/// </summary>
public interface ITranscriptionService
{
    /// <summary>
    /// Transcribes one audio clip and returns the recognized text plus provider metadata.
    /// </summary>
    /// <param name="audio">The audio bytes to transcribe. Owned by the caller — the
    /// implementation never disposes this stream.</param>
    /// <param name="fileName">The source file name (used to convey the format to the
    /// configured speech-to-text provider).</param>
    /// <param name="ct">Cancellation token; also bounded by the configured provider timeout.</param>
    /// <returns>
    /// The transcription result. A <see cref="TranscriptionResult.Text"/> of empty string is
    /// valid — it means the clip carried no understandable speech, not that the call failed.
    /// </returns>
    /// <exception cref="HttpRequestException">The configured speech-to-text provider answered
    /// with a non-success status, or its response envelope reported a failure.</exception>
    Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken ct = default);
}
