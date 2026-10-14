using PlottR0._2.Contracts.Recordings;
using PlottR0._2.Domain;

namespace PlottR0._2.Services.Abstractions;

/// <summary>
/// Orchestrates the voice-to-excel "send now" sync (design.md V2E): buffers the audio,
/// deduplicates by the PWA idempotency key, stores the blob, creates the audit row,
/// transcribes, parses, performs the locked-overwrite policy write into a fresh workbook
/// copy, and records the outcome back on the <c>VoiceRecording</c> row.
/// Consumers call this EXACTLY once per recording.
/// </summary>
public interface IRecordingSyncService
{
    /// <summary>
    /// Syncs one voice recording into the trial workbook.
    /// </summary>
    /// <param name="audio">The audio bytes to store and transcribe. Owned by the caller —
    /// the service never disposes this stream.</param>
    /// <param name="fileName">The source file name (used to convey the format to the
    /// speech-to-text sidecar's multipart upload).</param>
    /// <param name="command">The confirmed recording metadata and idempotency key.</param>
    /// <param name="ct">Cancellation token; propagated to every asynchronous call.</param>
    /// <returns>
    /// The sync outcome including status, written/skipped/missing cells and review flags.
    /// A <c>Replayed</c> result is returned (rather than an error) when the idempotency
    /// key was already processed.
    /// </returns>
    /// <exception cref="KeyNotFoundException">
    /// The referenced <see cref="SyncVoiceRecordingCommand.TrialId"/> does not exist.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="audio"/> or <paramref name="command"/> is <c>null</c>.
    /// </exception>
    Task<SyncVoiceRecordingResult> SyncAsync(
        Stream audio,
        string fileName,
        SyncVoiceRecordingCommand command,
        CancellationToken ct = default);

    /// <summary>
    /// Corrects one previously synced voice recording by writing the reviewer-confirmed
    /// target into the trial workbook. The stored blob, transcript and existing row are
    /// reused — no re-transcription, no re-upload.
    /// </summary>
    /// <param name="clientRecordingId">The PWA idempotency key of the recording to correct.</param>
    /// <param name="plot">The confirmed plot number (guide: 101..115).</param>
    /// <param name="subsample">The confirmed subsample index (guide: 1..4).</param>
    /// <param name="value">The confirmed rating value (0..10; 0 means "no plants").</param>
    /// <param name="traitOrdinal">The trait column the correction records against (1-based,
    /// matches the PWA's trait selection; forwarded verbatim to the workbook write and echoed
    /// on the result — never substituted by the service).</param>
    /// <param name="ct">Cancellation token; propagated to every asynchronous call.</param>
    /// <returns>
    /// The correction outcome with the same shape as <see cref="SyncAsync"/> — including
    /// status, written/skipped cells and review flags (<c>WasTranscriptOnly</c> is always
    /// <see langword="false"/> because the reviewer supplied the target).
    /// </returns>
    /// <exception cref="KeyNotFoundException">
    /// No <c>VoiceRecording</c> row exists for <paramref name="clientRecordingId"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The recording is in a terminal state (<see cref="VoiceRecordingStatus.Written"/> or
    /// <see cref="VoiceRecordingStatus.SkippedPresent"/>) and cannot be corrected.
    /// </exception>
    Task<SyncVoiceRecordingResult> CorrectAsync(
        string clientRecordingId,
        int plot,
        int subsample,
        int value,
        int traitOrdinal,
        CancellationToken ct = default);
}