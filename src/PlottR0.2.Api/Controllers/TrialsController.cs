using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Contracts.Dtos.Trial;
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

    private readonly ITrialService _trialService;

    public TrialsController(ITrialService trialService)
    {
        _trialService = trialService;
    }

    /// <summary>
    /// Creates a new trial by uploading an xlsx workbook.
    /// </summary>
    /// <param name="file">The xlsx workbook file from the multipart form.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>201 with the created trial summary and a Location header.</returns>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(TrialSummary), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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
    /// Lists all trials, ordered by creation time (newest first).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the list of trial summaries.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TrialSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TrialSummary>>> List(CancellationToken ct = default)
    {
        var trials = await _trialService.ListTrialsAsync(ct);
        return Ok(trials);
    }

    /// <summary>Downloads the trial's current workbook — the newest accumulated
    /// export copy if any syncs have happened, otherwise the uploaded original.</summary>
    [HttpGet("{trialId:guid}/workbook")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
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
    /// no ratings sheet; 404 when the trial or its workbook is missing.</returns>
    [HttpGet("{trialId:guid}/sheet")]
    [ProducesResponseType(typeof(TrialSheet), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TrialSheet>> GetSheet(
        Guid trialId, [FromQuery] string? since, CancellationToken ct = default)
    {
        try
        {
            return Ok(await _trialService.GetSheetAsync(trialId, since, ct));
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (FileNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidDataException ex) { return BadRequest(ex.Message); }
    }
}
