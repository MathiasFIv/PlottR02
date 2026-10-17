using PlottR0._2.Contracts.Dtos.Trial;

namespace PlottR0._2.Services.Abstractions;

/// <summary>
/// Transcribes ONE uploaded audio clip, parses exactly one rating number (0..10) out of the
/// transcript and writes it into a single, explicitly addressed ratings cell — the
/// "double-tap a cell → record → stop → number is saved to that cell" flow.
/// <para/>
/// Deliberately a thin orchestration over two existing concerns: the speech-to-text
/// provider behind <see cref="ITranscriptionService"/> (raw text only) and the locked,
/// audited single-cell write behind <see cref="ITrialService.EditCellAsync"/> (access gate,
/// per-trial write serialization, durable mirror, audit row and opaque version all reused
/// unchanged). No workbook, blob or DB access happens here.
/// </summary>
public interface IVoiceCellEditService
{
    /// <summary>
    /// Transcribes <paramref name="audio"/>, parses ONE number out of the transcript and —
    /// when one was understood — writes it into the
    /// <c>(request.Plot, request.Subsample, request.TraitOrdinal)</c> cell of the trial.
    /// <para/>
    /// Order of operations: request validation, then the trial access/existence pre-check
    /// (so an unauthorized caller or unknown trial fails BEFORE any speech-to-text spend),
    /// then transcription + parse, then — only when a value was understood — the same
    /// <see cref="ITrialService.EditCellAsync"/> call the JSON cell-edit endpoint uses.
    /// </summary>
    /// <param name="trialId">The trial whose sheet cell is written.</param>
    /// <param name="request">The addressed cell plus the overwrite opt-in; the value comes
    /// from the transcript.</param>
    /// <param name="audio">The audio bytes to transcribe. Owned by the caller.</param>
    /// <param name="fileName">The source file name (conveys the audio format to the
    /// speech-to-text provider).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The outcome. A transcript with no understood number is NOT an error — it is returned
    /// as <see cref="VoiceCellEditResult.StatusNotUnderstood"/> with <c>Written</c> false so
    /// the UI can ask the user to try again.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A field is out of range: plot/subsample ≤ 0 or traitOrdinal outside 1..9.
    /// </exception>
    /// <exception cref="KeyNotFoundException">The trial does not exist.</exception>
    /// <exception cref="ForbiddenAccessException">
    /// The caller is not a manager and is not assigned to the trial.
    /// </exception>
    /// <exception cref="HttpRequestException">The configured speech-to-text provider failed.
    /// </exception>
    Task<VoiceCellEditResult> TranscribeAndWriteAsync(
        Guid trialId,
        VoiceCellEditRequest request,
        Stream audio,
        string fileName,
        CancellationToken ct = default);
}
