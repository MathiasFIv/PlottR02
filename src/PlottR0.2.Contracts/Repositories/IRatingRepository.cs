using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Repositories;

/// <summary>
/// Rating-specific repository contract extending the generic read-only
/// <see cref="IRepository{TEntity,TKey}"/>. Adds the two workbook operations the
/// scoring feature needs. The repository owns resolving <c>(plot, subsample, trait)</c>
/// to a workbook cell; consumers never compute cell coordinates (design.md §7).
/// </summary>
public interface IRatingRepository : IRepository<Rating, Guid>
{
    /// <summary>
    /// Reads the current rating cells for one plot across ONE trait column.
    /// Cells that are empty are returned with a <see langword="null"/> value
    /// (never fabricated as 0 — design.md Rule 2).
    /// </summary>
    /// <param name="plot">The plot number (e.g. 101).</param>
    /// <param name="traitOrdinal">1-indexed trait ordinal (1..9) mapping to a column.</param>
    /// <returns>The plot's rating cells for that trait, in subsample order.</returns>
    IReadOnlyList<RatingCell> ReadPlotColumn(int plot, int traitOrdinal);

    /// <summary>
    /// Writes <paramref name="value"/> to the given subsample cells of one plot in one trait
    /// column against a pristine copy of the input workbook, and saves that copy. The write is
    /// idempotent and honours the empty ≠ 0 / metadata read-only / partial-fill rules.
    /// </summary>
    /// <param name="plot">The plot number.</param>
    /// <param name="traitOrdinal">1-indexed trait ordinal (1..9).</param>
    /// <param name="value">Rating value, validated 0..10 by the service.</param>
    /// <param name="subsampleIndices">The 1-indexed subsample indices to write.</param>
    /// <returns>The rating cells that were written (for reporting).</returns>
    IReadOnlyList<RatingCell> WritePlotColumn(int plot, int traitOrdinal, int value, IReadOnlySet<int> subsampleIndices);

    /// <summary>
    /// Writes <paramref name="value"/> to the given subsample cells of one plot in one trait
    /// column of the workbook at <paramref name="workbookPath"/>, applying the locked-overwrite
    /// policy: only EMPTY or "missing" cells accept a value (0 is a legal write); any cell that
    /// already holds a value — including 0 or non-"missing" text — is left untouched and
    /// reported as skipped. The result is exported as a fresh copy into
    /// <paramref name="outputDirectory"/>; the input workbook is never mutated.
    /// </summary>
    /// <param name="workbookPath">ABSOLUTE path to the workbook to write (e.g. a trial's workbook).</param>
    /// <param name="outputDirectory">ABSOLUTE directory for the exported copy.</param>
    /// <param name="plot">The plot number.</param>
    /// <param name="traitOrdinal">1-indexed trait ordinal (1..9).</param>
    /// <param name="value">Rating value, validated 0..10 by the service.</param>
    /// <param name="subsampleIndices">The 1-indexed subsample indices to consider.</param>
    /// <returns>The cells written, the cells skipped as already present, and the missing subsamples.</returns>
    PolicyWritePlotColumnResult WritePlotColumnPreserving(
        string workbookPath,
        string outputDirectory,
        int plot,
        int traitOrdinal,
        int value,
        IReadOnlySet<int> subsampleIndices);
}
