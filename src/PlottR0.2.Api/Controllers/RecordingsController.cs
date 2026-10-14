using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Contracts.Recordings;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Api.Controllers;

/// <summary>
/// Voice-recording endpoints (design.md V2E "send now").
/// Thin controller — the full sync orchestration (idempotency gate, blob storage,
/// audit row, transcription, parse, policy write) and the correction flow live in
/// <see cref="IRecordingSyncService"/> (mirrors <see cref="TrialsController"/>).
/// </summary>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public class RecordingsController : ControllerBase
{
    private readonly IRecordingSyncService _syncService;

    public RecordingsController(IRecordingSyncService syncService)
    {
        _syncService = syncService;
    }

    /// <summary>
    /// Syncs one voice recording from the PWA into the trial workbook.
    /// Idempotent per <c>clientRecordingId</c>: a repeat key short-circuits as a Replay.
    /// </summary>
    /// <param name="request">The confirmed recording metadata (clientRecordingId, trialId,
    /// traitOrdinal, plot, subsample, value, createdAt) + the remembered default target
    /// (<c>defaultPlot</c>/<c>defaultSubsample</c>, the values-only fallback) + the audio
    /// file (form field <c>file</c>, e.g. <c>rec.webm</c>) from the multipart form. A null
    /// or non-positive plot/subsample and a null value are treated as "not confirmed" so
    /// the transcript becomes authoritative for those fields.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the sync outcome; 400 on a missing file or invalid form values;
    /// 403 when the caller is not a manager and is not assigned to the recording's trial;
    /// or 404 when the referenced trial does not exist.</returns>
    [HttpPost("sync")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(SyncVoiceRecordingResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Sync(
        [FromForm] SyncVoiceRecordingRequest request,
        CancellationToken ct)
    {
        if (request.File is null || request.File.Length == 0)
        {
            return BadRequest("No audio file uploaded or file is empty.");
        }

        try
        {
            var command = new SyncVoiceRecordingCommand
            {
                ClientRecordingId = request.ClientRecordingId,
                TrialId = request.TrialId,
                TraitOrdinal = request.TraitOrdinal,
                Plot = request.Plot,
                Subsample = request.Subsample,
                DefaultPlot = request.DefaultPlot,
                DefaultSubsample = request.DefaultSubsample,
                Value = request.Value,
                CreatedAt = request.CreatedAt,
            };

            await using var stream = request.File.OpenReadStream();
            var result = await _syncService.SyncAsync(stream, request.File.FileName, command, ct);
            return Ok(result);
        }
        catch (ForbiddenAccessException)
        {
            return Forbid();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Corrects one previously synced voice recording by writing the reviewer-confirmed
    /// target into the trial workbook. The stored audio and transcript are reused — no
    /// re-upload, no re-transcription.
    /// </summary>
    /// <param name="clientRecordingId">The PWA idempotency key of the recording to correct.</param>
    /// <param name="request">The reviewer-confirmed target (plot, subsample, value) and the
    /// trait ordinal the PWA records against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the correction outcome, 400 on an invalid body, 403 when the
    /// caller is not a manager and is not assigned to the recording's trial, 404 when no
    /// recording exists for <paramref name="clientRecordingId"/>, or 409 when the recording
    /// is already in the terminal <c>Written</c>/<c>SkippedPresent</c> state.</returns>
    [HttpPost("{clientRecordingId}/correct")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(SyncVoiceRecordingResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Correct(
        Guid clientRecordingId,
        [FromBody] CorrectRecordingRequest request,
        CancellationToken ct)
    {
        try
        {
            var result = await _syncService.CorrectAsync(
                clientRecordingId.ToString(),
                request.Plot,
                request.Subsample,
                request.Value,
                request.TraitOrdinal,
                ct);
            return Ok(result);
        }
        catch (ForbiddenAccessException)
        {
            return Forbid();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }
}

/// <summary>
/// Multipart form payload for <c>POST api/Recordings/sync</c>.
/// A single <c>[FromForm]</c> model (scalar fields + <see cref="IFormFile"/>) keeps the
/// action Swagger-documentable — Swashbuckle rejects an action that mixes a
/// <c>[FromForm]</c> complex parameter with a separate <c>[FromForm] IFormFile</c>
/// parameter ("The output is not a valid form attribute" / generator exception).
/// </summary>
public sealed class SyncVoiceRecordingRequest
{
    public Guid ClientRecordingId { get; init; }
    public Guid TrialId { get; init; }

    /// <summary>
    /// The confirmed trait ordinal (workbook column anchor, 1..9). Optional on the wire:
    /// an OMITTED form field binds to 0 and the service treats absent, 0 and negative
    /// identically as "unspecified" (all normalize to 1), so an older PWA that never sends
    /// the field keeps working unchanged. Only an explicit value above 9 is flagged.
    /// </summary>
    public int TraitOrdinal { get; init; }

    /// <summary>
    /// The confirmed plot number, or <see langword="null"/> to let the transcript be
    /// authoritative. A non-positive value is treated the same as <see langword="null"/>.
    /// </summary>
    public int? Plot { get; init; }

    /// <summary>
    /// The confirmed subsample index, or <see langword="null"/> to let the transcript be
    /// authoritative. A non-positive value is treated the same as <see langword="null"/>.
    /// </summary>
    public int? Subsample { get; init; }

    /// <summary>
    /// The confirmed rating value, or <see langword="null"/> to let the transcript be
    /// authoritative. 0 is a legal "no plants" rating and is never treated as absent.
    /// </summary>
    public int? Value { get; init; }

    /// <summary>
    /// The client-sent default plot (<c>defaultPlot</c>) the PWA remembers from the previous
    /// successful utterance, or <see langword="null"/> when the form does not suggest a
    /// values-only utterance. A non-positive value is treated the same as
    /// <see langword="null"/>. Always overridden by an explicit spoken/confirmed plot.
    /// </summary>
    public int? DefaultPlot { get; init; }

    /// <summary>
    /// The client-sent default subsample (<c>defaultSubsample</c>) the PWA remembers from
    /// the previous successful utterance, or <see langword="null"/> when the form does not
    /// suggest a values-only utterance. A non-positive value is treated the same as
    /// <see langword="null"/>. Always overridden by an explicit spoken/confirmed subsample.
    /// </summary>
    public int? DefaultSubsample { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }
    public IFormFile? File { get; init; }
}

/// <summary>
/// JSON payload for <c>POST api/Recordings/{clientRecordingId}/correct</c>.
/// All fields are required; the reviewer must supply the complete target.
/// </summary>
public sealed class CorrectRecordingRequest
{
    /// <summary>The confirmed plot number (guide: 101..115).</summary>
    public int Plot { get; init; }

    /// <summary>The confirmed subsample index (guide: 1..4).</summary>
    public int Subsample { get; init; }

    /// <summary>The confirmed rating value (0..10; 0 means "no plants").</summary>
    public int Value { get; init; }

    /// <summary>The trait ordinal (workbook column) the PWA records against.</summary>
    public int TraitOrdinal { get; init; }
}
