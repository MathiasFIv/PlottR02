namespace PlottR0._2.Domain;

/// <summary>
/// Represents a voice recording uploaded from the PWA and its processing results.
/// <see cref="ClientRecordingId"/> is the PWA-emitted idempotency key (crypto.randomUUID).
/// </summary>
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

    /// <summary>
    /// The trait ordinal this recording's write actually used; 1 when the client did not specify one;
    /// a value above 9 means the sync was flagged without writing. Nullable only for rows predating this column.
    /// </summary>
    public int? ResolvedTraitOrdinal { get; set; }

    public string? Transcript { get; set; }
    public int? ParsedPlot { get; set; }
    public int? ParsedSubsample { get; set; }
    public int? ParsedValue { get; set; }
    public double? Confidence { get; set; }
    public string Status { get; set; } = VoiceRecordingStatus.Received;
}
