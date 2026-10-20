using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class TrialsController : ControllerBase
{
    private const long MaxUploadSizeBytes = 50L * 1024 * 1024;

    public const string CreateTrialRoles = "SystemAdmin,Researcher,TrialManager";

    private readonly ITrialService _trialService;
    private readonly IVoiceCellEditService _voiceCellEditService;

    public TrialsController(ITrialService trialService, IVoiceCellEditService voiceCellEditService)
    {
        _trialService = trialService;
        _voiceCellEditService = voiceCellEditService;
    }

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

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TrialSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TrialSummary>>> List(CancellationToken ct = default)
    {
        var trials = await _trialService.ListTrialsAsync(ct);
        return Ok(trials);
    }

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

    [HttpGet("{trialId:guid}/workbook")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadWorkbook(Guid trialId, CancellationToken ct = default)
    {
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

    [HttpPost("{trialId:guid}/cells")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(CellEditResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(CellEditResult), StatusCodes.Status409Conflict)]
    /// <summary>
    /// 200 with the result on success. A filled cell without <c>overwrite</c> is refused with 409
    /// carrying the same <see cref="CellEditResult"/> body shape (not an error payload), so the
    /// caller can read <c>priorValue</c> and offer the overwrite confirmation.
    /// </summary>
    public async Task<ActionResult<CellEditResult>> EditCell(
        Guid trialId, [FromBody] EditCellRequest request, CancellationToken ct = default)
    {
        try
        {
            var result = await _trialService.EditCellAsync(trialId, request, ct);

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

public sealed class VoiceCellEditFormRequest
{
    public int Plot { get; init; }

    public int Subsample { get; init; }

    public int TraitOrdinal { get; init; }

    public bool Overwrite { get; init; } = true;

    public IFormFile? File { get; init; }
}
