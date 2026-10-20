namespace PlottR0._2.Services.Configuration;

public sealed class FasterWhisperOptions
{
    public const string SectionName = "FasterWhisper";

    public string BaseUrl { get; set; } = "http://localhost:8000";

    public string Model { get; set; } = "base";

    public int TimeoutSeconds { get; set; } = 120;
}