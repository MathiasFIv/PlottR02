namespace PlottR0._2.Contracts.Transcription;

public sealed class TranscriptionResult
{
    public string Text { get; init; } = string.Empty;

    public string Language { get; init; } = string.Empty;

    public double? Duration { get; init; }
}