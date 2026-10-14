namespace PlottR0._2.Contracts.Recordings;

/// <summary>
/// Command to sync one voice recording from the PWA into the trial workbook
/// (design.md V2E auto-sync). Carries the PWA-emitted idempotency key
/// (<see cref="ClientRecordingId"/>, a <c>crypto.randomUUID</c>) plus the optionally
/// CONFIRMED cell target — plot, subsample and rating value. The confirmed fields are
/// the worker-reviewed write targets when present; fields the worker never reviewed are
/// <see langword="null"/>, which makes the transcript authoritative for exactly those
/// fields. Plot/subsample values ≤ 0 are normalized to "not confirmed" by the sync
/// service (0 is data-garbage from old clients, never a real target); value 0 is legal
/// data and always passes through.
/// </summary>
public sealed class SyncVoiceRecordingCommand
{
    /// <summary>
    /// The PWA-emitted idempotency key for this recording. Re-syncing the same key
    /// is a no-op replay rather than a duplicate write.
    /// </summary>
    public Guid ClientRecordingId { get; init; }

    /// <summary>The trial the recording belongs to. Must exist before the sync runs.</summary>
    public Guid TrialId { get; init; }

    /// <summary>
    /// The confirmed plot number — the worker-reviewed write target when present;
    /// <see langword="null"/> (or ≤ 0, normalized to null by the service) means the
    /// transcript is authoritative and the parsed plot is used.
    /// </summary>
    public int? Plot { get; init; }

    /// <summary>
    /// The confirmed 1-indexed subsample index — the worker-reviewed write target when
    /// present; <see langword="null"/> (or ≤ 0, normalized to null by the service) means
    /// the transcript is authoritative and the parsed subsample is used. An unresolved
    /// subsample disables the write entirely — the four-cell default fallback was removed
    /// by design.
    /// </summary>
    public int? Subsample { get; init; }

    /// <summary>
    /// The CLIENT-SENT default plot number (<c>defaultPlot</c> form field): the PWA
    /// remembers the target of the last successful utterance and posts it whenever the form
    /// suggests a values-only utterance (plot/subsample not spoken). The resolution falls
    /// back to this value only when the confirmed field AND the transcript both omitted the
    /// plot; an explicit spoken/confirmed plot always wins. Nullable because the client only
    /// sends it on values-only forms — a missing form field binds as <see langword="null"/>,
    /// which disables the fallback.
    /// </summary>
    public int? DefaultPlot { get; init; }

    /// <summary>
    /// The CLIENT-SENT default subsample index (<c>defaultSubsample</c> form field): the
    /// PWA remembers the target of the last successful utterance and posts it whenever the
    /// form suggests a values-only utterance. The resolution falls back to this value only
    /// when the confirmed field AND the transcript both omitted the subsample AND no NEW
    /// plot was spoken (or the spoken plot equals the default plot — a new plot never
    /// inherits the previous subsample cursor); an explicit spoken/confirmed subsample
    /// always wins. Nullable because the client only sends it on values-only forms — a
    /// missing form field binds as <see langword="null"/>, which disables the fallback.
    /// </summary>
    public int? DefaultSubsample { get; init; }

    /// <summary>1-indexed trait ordinal (1..9) mapping to a single workbook column (design.md §5).
    /// Non-nullable on purpose: an OMITTED form field binds to 0, and the service treats absent,
    /// 0 and negative identically as "unspecified" — all coerce to 1 (the first Part Rated
    /// column) — so no separate "was it sent" flag is needed and an all-zeros legacy payload is
    /// a legal request. Only an explicit value above 9 is out of range and gets flagged.</summary>
    public int TraitOrdinal { get; init; }

    /// <summary>
    /// The confirmed ordinal rating value, validated 0..10 (design.md §9). Never nulled by
    /// the service: 0 is a legal rating ("unrated", passed through unchanged); a present
    /// value is always a worker confirmation, out-of-range values are flagged and stored
    /// but never written.
    /// </summary>
    public int? Value { get; init; }

    /// <summary>
    /// UTC moment the PWA recorded the audio clip, when known. When present the audit
    /// row carries the worker's recording time instead of the backend sync time.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; init; }
}
