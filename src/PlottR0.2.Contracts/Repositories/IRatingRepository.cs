using PlottR0._2.Contracts.Ratings;

namespace PlottR0._2.Contracts.Repositories;

/// <summary>
/// Workbook-backed store contract for the trial sheet workflow: reading a trial's
/// sheet region and applying the locked-overwrite policy write used by the voice sync
/// pipeline. The repository owns resolving <c>(plot, subsample, trait)</c> to a
/// workbook cell; consumers never compute cell coordinates (design.md §7).
/// </summary>
public interface IRatingRepository
{
    /// <summary>
    /// Reads the WHOLE ratings sheet region of the workbook at <paramref name="workbookPath"/>:
    /// the column identity strip (rows 3 + 8), the ARM metadata strip (rows 5, 6, 10, 11, 12,
    /// 20 — read for display, approved 2026-10-07) and every key-present data row from row 22
    /// onwards, in workbook order. Empty and non-numeric text cells come back as
    /// <see langword="null"/> (empty ≠ 0 — design.md Rule 2); <c>0</c> is a real value.
    /// <para/>
    /// Deliberately synchronous with NO <see cref="CancellationToken"/>: ClosedXML cannot
    /// abort mid-parse, so a token here would be a dishonest signature (plan §6).
    /// </summary>
    /// <param name="workbookPath">ABSOLUTE path to the workbook to read (e.g. a trial's workbook).</param>
    /// <returns>The parsed sheet region, including its metadata strip.</returns>
    /// <exception cref="InvalidDataException">
    /// The workbook has no "Subsample Plot Data" sheet (non-ARM upload).
    /// </exception>
    SheetRegion ReadSheetRegion(string workbookPath);

    /// <summary>
    /// Writes <paramref name="values"/> into the given subsample rows of one plot of the
    /// workbook at <paramref name="workbookPath"/>, applying the locked-overwrite policy:
    /// only EMPTY or "missing" cells accept a value (0 is a legal write); any cell that
    /// already holds a value — including 0 or non-"missing" text — is left untouched and
    /// reported as skipped. With <paramref name="scanForFreeCells"/> it instead APPENDS the
    /// values into the row's next free cells within the current category block (derived from
    /// row 8 of the sheet, not hardcoded), see the parameter docs. The result is exported as
    /// a fresh copy into <paramref name="outputDirectory"/>; the input workbook is never
    /// mutated.
    /// </summary>
    /// <param name="workbookPath">ABSOLUTE path to the workbook to write (e.g. a trial's workbook).</param>
    /// <param name="outputDirectory">ABSOLUTE directory for the exported copy.</param>
    /// <param name="plot">The plot number.</param>
    /// <param name="traitOrdinal">1-indexed trait ordinal (1..9) of the FIRST value's column.</param>
    /// <param name="values">Rating values, each validated 0..10 by the service.</param>
    /// <param name="subsampleIndices">The 1-indexed subsample indices to consider.</param>
    /// <param name="scanForFreeCells">
    /// When <see langword="false"/>, the HORIZONTAL fixed-column behaviour applies: value
    /// index <c>i</c> targets the trait column <c>traitOrdinal + i</c> (traitOrdinal 1 →
    /// column C, 2 → D, …, 9 → K); a value whose column would exceed trait 9
    /// (<c>traitOrdinal + i &gt; 9</c>) is NOT written and NOT reported skipped — it is
    /// logged and counted via <see cref="PolicyWritePlotColumnResult.OutOfTraitRangeValueCount"/>.
    /// <para/>
    /// When <see langword="true"/>, the category-block APPEND behaviour applies: for each
    /// subsample row the values are placed into the row's next free cells, one per column,
    /// starting at the first writable cell at or after <c>traitOrdinal</c>. The append stays
    /// inside the category block the first free cell belongs to — blocks are contiguous runs
    /// of equal, non-empty labels read from row 8 of the sheet (C:E "LEAF1", F:H "LEAF2",
    /// I:K "LEAF3"; an EMPTY label inherits the last non-empty label to its left — a leading
    /// empty run inherits the NEXT non-empty label — and a region with NO labels at all acts
    /// as ONE block). An utterance NEVER spans category blocks: when the next free cell would
    /// fall outside the current block, the value in hand and every remaining value of the
    /// utterance are counted as out-of-trait-range (flagged for review, never silently lost).
    /// Progression to the next category happens on LATER utterances, once the block holds no
    /// free cells — the cursor always targets the region's first free cell, so a partially
    /// filled block keeps receiving the following recordings' values until it is full. Cells
    /// already present are never overwritten and never reported skipped in this mode.
    /// </param>
    /// <returns>The cells written, the cells skipped as already present, and the missing subsamples.</returns>
    PolicyWritePlotColumnResult WritePlotColumnPreserving(
        string workbookPath,
        string outputDirectory,
        int plot,
        int traitOrdinal,
        IReadOnlyList<int> values,
        IReadOnlySet<int> subsampleIndices,
        bool scanForFreeCells);
}
