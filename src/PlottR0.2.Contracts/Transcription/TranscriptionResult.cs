namespace PlottR0._2.Contracts.Transcription;

/// <summary>
/// The result of transcribing one audio clip to text through the speech-to-text sidecar.
/// A missing or empty <see cref="Text"/> is valid — some recordings contain no
/// understandable speech and the sidecar reports an empty transcript — so callers must
/// treat <see cref="Text"/> as an ordinary string, not as a failure signal.
/// </summary>
public sealed class TranscriptionResult
{
    /// <summary>The recognized speech, or an empty string when nothing was understood.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// The language the recognizer attributed to the clip (e.g. <c>"en"</c>, <c>"da"</c>),
    /// or an empty string when the sidecar did not report one.
    /// </summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>
    /// Duration of the transcribed audio in seconds, when the sidecar reported it.
    /// </summary>
    public double? Duration { get; init; }
}