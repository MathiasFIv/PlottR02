using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Contracts.Recordings;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Api.Controllers;

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
/// One wrapper model rather than split parameters: multipart uploads bind (and generate Swagger
/// documentation) correctly only when the whole form, including the <see cref="File"/>, is a single
/// <c>[FromForm]</c> object.
/// </summary>
public sealed class SyncVoiceRecordingRequest
{
    public Guid ClientRecordingId { get; init; }
    public Guid TrialId { get; init; }

    public int TraitOrdinal { get; init; }

    public int? Plot { get; init; }

    public int? Subsample { get; init; }

    public int? Value { get; init; }

    public int? DefaultPlot { get; init; }

    public int? DefaultSubsample { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }
    public IFormFile? File { get; init; }
}

public sealed class CorrectRecordingRequest
{
    public int Plot { get; init; }

    public int Subsample { get; init; }

    public int Value { get; init; }

    public int TraitOrdinal { get; init; }
}
