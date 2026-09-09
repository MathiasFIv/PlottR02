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
}
