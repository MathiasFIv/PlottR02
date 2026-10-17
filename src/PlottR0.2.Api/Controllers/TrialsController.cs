using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Api.Controllers;

/// <summary>
/// REST endpoints for trial creation and listing.
/// Thin controller — all business logic is delegated to <see cref="ITrialService"/>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public class TrialsController : ControllerBase
{
    private const long MaxUploadSizeBytes = 50L * 1024 * 1024; // 50 MB

    /// <summary>
    /// Roles permitted to create trials, referenced by the
    /// <see cref="AuthorizeAttribute.Roles"/> gate on <see cref="Create"/> (and
    /// enforced again in <see cref="ITrialService"/>). Mirrors the gate/error-mapping
    /// style established by <c>UsersController.ManageUsersRoles</c>.
    /// </summary>
    public const string CreateTrialRoles = "SystemAdmin,Researcher,TrialManager";

    private readonly ITrialService _trialService;
    private readonly IVoiceCellEditService _voiceCellEditService;

    public TrialsController(ITrialService trialService, IVoiceCellEditService voiceCellEditService)
    {
        _trialService = trialService;
        _voiceCellEditService = voiceCellEditService;
    }

    /// <summary>
    /// Creates a new trial by uploading an xlsx workbook. Restricted to system
    /// administrators, researchers and trial managers. A researcher or trial-manager
    /// creator is automatically assigned to the new trial by the service; a
    /// system-administrator creator is not (admins sit above the assignment matrix).
    /// </summary>
    /// <param name="file">The xlsx workbook file from the multipart form.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>201 with the created trial summary and a Location header, 400 for an
    /// invalid/oversized/missing file, or 403 when the caller's role may not create trials.</returns>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [Authorize(Roles = CreateTrialRoles)]
    [ProducesResponseType(typeof(TrialSummary), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TrialSummary>> Create(IFormFile file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest("No file uploaded or file is empty.");
        }

        if (file.Length > MaxUploadSizeBytes)
        {
            return BadRequest("File exceeds the 50 MB limit.");
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var summary = await _trialService.CreateTrialAsync(
                Path.GetFileName(file.FileName), stream, ct);

            return CreatedAtAction(nameof(List), new { }, summary);
        }
        catch (ForbiddenAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Lists the trials visible to the caller, ordered by creation time (newest first).
    /// Managers (system administrators, researchers and trial managers) receive every
    /// trial; field workers (and legacy/role-less users) receive only the trials they are
    /// assigned to, which may be an empty list.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the list of visible trial summaries.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TrialSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TrialSummary>>> List(CancellationToken ct = default)
    {
        var trials = await _trialService.ListTrialsAsync(ct);
        return Ok(trials);
    }

    /// <summary>
    /// Replaces the set of users assigned to a trial. Restricted to system administrators,
    /// researchers and trial managers; the set is validated all-or-nothing, and an empty
    /// list clears the crew.
    /// </summary>
    /// <param name="trialId">The trial whose assignment set is replaced.</param>
    /// <param name="request">The full desired set of user ids.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>204 on success, 400 for an invalid payload, 403 when the caller's role may
    /// not assign users, or 404 when the trial does not exist.</returns>
    [HttpPut("{trialId:guid}/users")]
    [Consumes("application/json")]
    [Authorize(Roles = CreateTrialRoles)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignUsers(
        Guid trialId, [FromBody] AssignTrialUsersRequest request, CancellationToken ct = default)
    {
        try
        {
            await _trialService.AssignTrialUsersAsync(trialId, request, ct);
            return NoContent();
        }
        catch (ForbiddenAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Lists the users assigned to a trial, in assignment order (oldest first).
    /// Restricted to system administrators, researchers and trial managers.
    /// </summary>
    /// <param name="trialId">The trial whose assigned users are read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the assigned users, 403 when the caller's role may not view
    /// assignments, or 404 when the trial does not exist.</returns>
    [HttpGet("{trialId:guid}/users")]
    [Authorize(Roles = CreateTrialRoles)]
    [ProducesResponseType(typeof(IReadOnlyList<UserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<UserResponse>>> ListUsers(
        Guid trialId, CancellationToken ct = default)
    {
        try
        {
            var users = await _trialService.ListTrialUsersAsync(trialId, ct);
            return Ok(users);
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

    /// <summary>Downloads the trial's current workbook — the newest accumulated
    /// export copy if any syncs have happened, otherwise the uploaded original.
    /// Managers may download any trial; other callers must be assigned to it.</summary>
    /// <param name="trialId">The trial whose workbook is downloaded.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The xlsx file; 403 when the caller may not access the trial, or 404 when
    /// the trial or its workbook is missing.</returns>
    [HttpGet("{trialId:guid}/workbook")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadWorkbook(Guid trialId, CancellationToken ct = default)
    {
        // The 404 catches cover only the resolve step: PhysicalFile streams the bytes after
        // this action has returned, so a workbook deleted between resolve and send surfaces
        // as a 500. That narrow TOCTOU race is accepted, consistent with the codebase.
        try
        {
            var export = await _trialService.GetWorkbookExportAsync(trialId, ct);
            return PhysicalFile(
                export.FilePath,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                export.FileName,
                enableRangeProcessing: true);
        }
        catch (ForbiddenAccessException) { return Forbid(); }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (FileNotFoundException ex) { return NotFound(ex.Message); }
    }

    /// <summary>Returns the read-only ratings sheet for a trial — column identity, ARM
    /// metadata strip and every key-present data row — plus an opaque <c>version</c>
    /// token. Echo the token back as <c>?since=</c> to get the ~100-byte
    /// <c>changed:false</c> fast path while the workbook is untouched; a malformed
    /// <c>since</c> is ignored (full payload), never an error.</summary>
    /// <param name="trialId">The trial whose sheet is read.</param>
    /// <param name="since">Last-seen opaque version; omit for the full payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the sheet payload (full or unchanged); 400 when the workbook has
    /// no ratings sheet; 403 when the caller may not access the trial; 404 when the trial
    /// or its workbook is missing.</returns>
    [HttpGet("{trialId:guid}/sheet")]
    [ProducesResponseType(typeof(TrialSheet), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TrialSheet>> GetSheet(
        Guid trialId, [FromQuery] string? since, CancellationToken ct = default)
    {
        try
        {
            return Ok(await _trialService.GetSheetAsync(trialId, since, ct));
        }
        catch (ForbiddenAccessException) { return Forbid(); }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (FileNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidDataException ex) { return BadRequest(ex.Message); }
    }

    /// <summary>Edits one ratings cell of a trial's sheet (the inline "edit a sheet cell"
    /// feature). The addressed <c>(plot, subsample, traitOrdinal)</c> cell is written and a
    /// fresh <c>_out_</c> workbook copy is exported; the response carries the new opaque
    /// <c>version</c> to poll the sheet with. Writing into a cell that already holds a value
    /// requires <c>overwrite: true</c> — without it the write is REFUSED with a 409 so the UI
    /// can read <c>priorValue</c> and ask the user to confirm. Managers may edit any trial;
    /// other callers must be assigned to it.</summary>
    /// <param name="trialId">The trial whose sheet cell is edited.</param>
    /// <param name="request">The addressed cell, the new value and the overwrite opt-in.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the write outcome; 400 for an invalid payload; 403 when the caller may
    /// not access the trial; 404 when the trial or its workbook is missing; 409 when the target
    /// cell already holds a value and overwrite was not requested.</returns>
    [HttpPost("{trialId:guid}/cells")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(CellEditResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(CellEditResult), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CellEditResult>> EditCell(
        Guid trialId, [FromBody] EditCellRequest request, CancellationToken ct = default)
    {
        try
        {
            var result = await _trialService.EditCellAsync(trialId, request, ct);

            // A filled-cell refusal is not an error: 409 signals "confirm to overwrite" and the
            // body still carries PriorValue so the UI can show what is currently there.
            if (result.Skipped && result.SkippedReason == SingleCellWriteResult.CellAlreadyFilled)
                return Conflict(result);

            return Ok(result);
        }
        catch (ForbiddenAccessException) { return Forbid(); }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (FileNotFoundException ex) { return NotFound(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidDataException ex) { return BadRequest(ex.Message); }
    }

    /// <summary>Edits one ratings cell of a trial's sheet FROM VOICE (the "double-tap a cell →
    /// record → stop → number is saved to that cell" feature): the uploaded audio is
    /// transcribed server-side, ONE number (0..10) is parsed out of the transcript, and — when
    /// one was understood — it is written into the addressed cell through the SAME
    /// <see cref="ITrialService.EditCellAsync"/> path as <see cref="EditCell"/> (access gate,
    /// per-trial write serialization, durable mirror, audit row and opaque version all reused).
    /// The trial access check runs BEFORE transcription, so an unauthorized caller or unknown
    /// trial never reaches the speech-to-text provider. An unparseable transcript is NOT an
    /// error: the 200 body then carries <c>status: "not_understood"</c> and the raw transcript
    /// so the UI can simply ask the user to try again. Writing into a filled cell overwrites
    /// by default (<c>overwrite</c> defaults to true); send <c>overwrite: false</c> to get the
    /// <c>"cell_already_filled"</c> status instead.</summary>
    /// <param name="trialId">The trial whose sheet cell is edited.</param>
    /// <param name="request">The addressed cell (plot, subsample, traitOrdinal, overwrite) +
    /// the audio file (form field <c>file</c>, e.g. <c>cell.webm</c>) from the multipart form.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the outcome (written, skipped or not-understood); 400 on a missing
    /// file or an invalid form value; 403 when the caller is not a manager and is not assigned
    /// to the trial; or 404 when the trial does not exist.</returns>
    [HttpPost("{trialId:guid}/cells/voice")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(VoiceCellEditResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VoiceCellEditResult>> EditCellByVoice(
        Guid trialId, [FromForm] VoiceCellEditFormRequest request, CancellationToken ct = default)
    {
        if (request.File is null || request.File.Length == 0)
        {
            return BadRequest("No audio file uploaded or file is empty.");
        }

        try
        {
            var command = new VoiceCellEditRequest
            {
                Plot = request.Plot,
                Subsample = request.Subsample,
                TraitOrdinal = request.TraitOrdinal,
                Overwrite = request.Overwrite,
            };

            await using var stream = request.File.OpenReadStream();
            var result = await _voiceCellEditService.TranscribeAndWriteAsync(
                trialId, command, stream, request.File.FileName, ct);
            return Ok(result);
        }
        catch (ForbiddenAccessException) { return Forbid(); }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (FileNotFoundException ex) { return NotFound(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidDataException ex) { return BadRequest(ex.Message); }
    }
}

/// <summary>
/// Multipart form payload for <c>POST api/Trials/{trialId}/cells/voice</c>.
/// A single <c>[FromForm]</c> model (scalar fields + <see cref="IFormFile"/>) keeps the
/// action Swagger-documentable — the same shape as <c>SyncVoiceRecordingRequest</c>.
/// An OMITTED <c>overwrite</c> field keeps its <see langword="true"/> default, matching the
/// voice flow's "speak over what is there" contract.
/// </summary>
public sealed class VoiceCellEditFormRequest
{
    /// <summary>The plot number of the target cell (must be &gt; 0).</summary>
    public int Plot { get; init; }

    /// <summary>The 1-indexed subsample of the target cell (must be &gt; 0).</summary>
    public int Subsample { get; init; }

    /// <summary>The 1-indexed trait ordinal of the target cell (1..9; 1 → column C, 9 → column K).</summary>
    public int TraitOrdinal { get; init; }

    /// <summary>Opt-in to overwriting a filled cell; defaults to <see langword="true"/>.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>The audio clip to transcribe (form field <c>file</c>, e.g. <c>cell.webm</c>).</summary>
    public IFormFile? File { get; init; }
}
