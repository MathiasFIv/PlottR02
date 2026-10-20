namespace PlottR0._2.Domain;

public class VoiceRecording : AuditableEntity
{
    public Guid Id { get; set; }
    public string ClientRecordingId { get; set; } = string.Empty;
    public Guid? TrialId { get; set; }
    public string BlobPath { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public int? ConfirmedPlot { get; set; }
    public int? ConfirmedSubsample { get; set; }
    public int? ConfirmedValue { get; set; }

    public int? ResolvedTraitOrdinal { get; set; }

    public string? Transcript { get; set; }
    public int? ParsedPlot { get; set; }
    public int? ParsedSubsample { get; set; }
    public int? ParsedValue { get; set; }
    public double? Confidence { get; set; }
    public string Status { get; set; } = VoiceRecordingStatus.Received;
}
