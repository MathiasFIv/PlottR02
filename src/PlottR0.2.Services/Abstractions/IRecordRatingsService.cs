using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Domain;

namespace PlottR0._2.Services.Abstractions;

/// <summary>
/// Rating-specific service contract extending the generic <see cref="ICrudService{TEntity,TKey}"/>.
/// Inherits the structural entity-level reads (<see cref="ICrudService{TEntity,TKey}.GetByIdAsync"/>
/// / <see cref="ICrudService{TEntity,TKey}.GetAllAsync"/>) and adds the scoring workflows.
/// The inherited reads are honoured only insofar as the underlying cell store can address
/// Rating entities; the workbook-backed repository throws for entity-by-id reads, so those
/// generic members are documented as unsupported here (design.md §7, Rule 3 metadata read-only).
/// Rule-guarded scoring validates the command (value 0..10, trait ordinal 1..9, plot existence
/// against the workbook, deduplicated subsample set) and orchestrates the write through
/// <see cref="Contracts.Repositories.IRatingRepository"/>. This is where the domain rules and
/// audit trail live; the repository only persists cells (design.md §7).
/// </summary>
public interface IRecordRatingsService : ICrudService<Rating, Guid>
{
    /// <summary>
    /// Records a scoring value for a set of subsamples of one plot in one trait column.
    /// </summary>
    /// <param name="command">The scoring command. Must not be <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result describing the written cells and preserved state.</returns>
    Task<SubsampleRatingResult> RecordSubsampleRatingsAsync(
        RecordSubsampleRatingsCommand command,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the current rating cells for one plot across one trait column.
    /// </summary>
    /// <param name="plot">The plot number.</param>
    /// <param name="traitOrdinal">1-indexed trait ordinal (1..9).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plot's rating cells for that trait, empty cells reported as <see langword="null"/>.</returns>
    Task<IReadOnlyList<RatingCell>> ReadPlotColumnAsync(int plot, int traitOrdinal, CancellationToken ct = default);
}
