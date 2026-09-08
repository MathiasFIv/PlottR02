using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Api.Controllers;

/// <summary>
/// Scoring endpoints for subsample ratings on the Excel workbook.
/// Thin controller — all validation, rule enforcement and auditing is delegated to
/// <see cref="IRecordRatingsService"/> (design.md §7). Currently flat/full-body because
/// there is no trial registry yet; design.md §6 plans a nested
/// <c>trials/{trialId}/traits/{traitOrdinal}/ratings</c> shape once trials are real.
/// </summary>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public class RatingsController : ControllerBase
{
    private readonly IRecordRatingsService _ratingsService;

    public RatingsController(IRecordRatingsService ratingsService)
    {
        _ratingsService = ratingsService;
    }

    /// <summary>
    /// Reads the current rating cells for one plot across one trait column.
    /// Empty cells are reported with a <see langword="null"/> value (never fabricated as 0).
    /// </summary>
    /// <param name="plot">The plot number (e.g. 101).</param>
    /// <param name="traitOrdinal">1-indexed trait ordinal (1..9) mapping to a column.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the plot's rating cells, or 400 on an invalid trait ordinal.</returns>
    [HttpGet("{plot:int}/{traitOrdinal:int}")]
    [ProducesResponseType(typeof(IReadOnlyList<RatingCell>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<RatingCell>>> ReadColumn(
        int plot,
        int traitOrdinal,
        CancellationToken ct = default)
    {
        try
        {
            var cells = await _ratingsService.ReadPlotColumnAsync(plot, traitOrdinal, ct);
            return Ok(cells);
        }
        catch (Exception ex) when (IsValidation(ex))
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Records a scoring value for a set of subsamples of ONE plot in ONE trait column.
    /// All targeted subsamples receive the same value; unmentioned cells are left untouched.
    /// </summary>
    /// <param name="command">The scoring command (plot, traitOrdinal, value, subsampleIndices).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the write result, or 400 on validation failure.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(SubsampleRatingResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SubsampleRatingResult>> Record(
        [FromBody] RecordSubsampleRatingsCommand command,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _ratingsService.RecordSubsampleRatingsAsync(command, ct);
            return Ok(result);
        }
        catch (Exception ex) when (IsValidation(ex))
        {
            return BadRequest(ex.Message);
        }
    }

    private static bool IsValidation(Exception ex)
        => ex is ArgumentException
           or ArgumentOutOfRangeException
           or FormatException;
}
