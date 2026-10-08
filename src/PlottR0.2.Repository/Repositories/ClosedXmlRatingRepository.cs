using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;

namespace PlottR0._2.Repository.Repositories;

/// <summary>
/// Workbook-backed implementation of <see cref="IRatingRepository"/> using ClosedXML.
/// This is the ONLY class in the solution that touches ClosedXML/Excel (design.md §5,
/// Rule 1 layering): it opens the pristine input, reads/writes rating cells on the
/// "Subsample Plot Data" sheet region C22:K245, and exports the result as a copy.
///
/// The entity-style generic <see cref="IRepository{TEntity,TKey}"/> methods are NOT
/// supported: the workbook store can only legally write rating cells (Rule 3 metadata
/// read-only, Rule 2 empty ≠ 0), so full entity CRUD is deliberately unavailable here.
/// </summary>
public class ClosedXmlRatingRepository : IRatingRepository
{
    private const string SheetName = "Subsample Plot Data";
    private const int FirstDataRow = 22;     // region start (C22:K245)

    /// <summary>
    /// Row 8 of "Subsample Plot Data" carries the Part Rated category metadata used by the
    /// scan-for-free-cells append (design.md §5): B8 = "Part Rated" header, then contiguous
    /// label runs over the nine trait columns, e.g. C:E "LEAF1", F:H "LEAF2", I:K "LEAF3".
    /// </summary>
    private const int PartRatedHeaderRow = 8;

    /// <summary>
    /// Row 3 carries the per-column Pest Code labels (design.md §5) — the other half of the
    /// trait identity the sheet view displays. Read for display (Rule 3, approved 2026-10-07).
    /// </summary>
    private const int PestHeaderRow = 3;

    // ARM metadata strip rows read for display by ReadSheetRegion (first non-empty cell of
    // each row, scanning C→K). Never written — design.md Rule 3.
    private const int CropRow = 5;
    private const int RatingDateRow = 6;
    private const int RatingTypeRow = 10;
    private const int UnitRow = 11;
    private const int SampleSizeRow = 12;
    private const int SubsampleCountRow = 20;

    private readonly ExcelWorkbookOptions _options;
    private readonly ILogger<ClosedXmlRatingRepository> _logger;

    public ClosedXmlRatingRepository(
        IOptions<ExcelWorkbookOptions> options,
        ILogger<ClosedXmlRatingRepository> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<RatingCell> ReadPlotColumn(int plot, int traitOrdinal)
    {
        using var wb = OpenInput();
        var sheet = wb.Worksheet(SheetName);
        var column = TraitColumn(traitOrdinal);
        var rows = FindSubsampleRows(sheet, plot);

        return rows
            .OrderBy(kv => kv.Value.subsample)
            .Select(kv =>
            {
                var (subsample, row) = kv.Value;
                var cell = sheet.Cell(row, column);
                return new RatingCell(plot, subsample, traitOrdinal, ReadInt(cell), cell.Address.ToString() ?? string.Empty);
            })
            .ToList();
    }

    /// <inheritdoc />
    public SheetRegion ReadSheetRegion(string workbookPath)
    {
        // Path-parameterized open (mirrors WritePlotColumnPreserving) — NEVER OpenInput(),
        // which reads the global Excel:InputPath rather than this trial's workbook.
        using var wb = OpenWorkbook(workbookPath);
        if (!wb.TryGetWorksheet(SheetName, out var sheet) || sheet is null)
        {
            // Non-ARM upload: the endpoint maps this to a 400 with a readable body (plan §3).
            throw new InvalidDataException(
                $"Sheet '{SheetName}' was not found in workbook '{Path.GetFileName(workbookPath)}'.");
        }

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? FirstDataRow;

        // Row-label normalization is shared with the category-block append, so the label
        // semantics (empty inherits its left neighbour, leading empties inherit the first
        // non-empty label) cannot diverge between readers.
        var pestLabels = NormalizeRowLabels(sheet, PestHeaderRow, TraitColumn(1), TraitColumn(9));
        var partLabels = NormalizeRowLabels(sheet, PartRatedHeaderRow, TraitColumn(1), TraitColumn(9));

        var columns = new List<SheetColumn>(capacity: 9);
        for (var traitOrdinal = 1; traitOrdinal <= 9; traitOrdinal++)
        {
            var index = traitOrdinal - 1; // INVARIANT: values[i] ↔ columns[i].TraitOrdinal = i + 1
            var column = TraitColumn(traitOrdinal); // TraitColumn(t) = t + 2 (design.md §5)
            var letter = XLHelper.GetColumnLetterFromNumber(column);
            var pestCode = EmptyToNull(pestLabels[index]);
            var partRated = EmptyToNull(partLabels[index]);
            columns.Add(new SheetColumn(
                traitOrdinal, letter, pestCode, partRated, BuildColumnLabel(pestCode, partRated, letter)));
        }

        // Key-present rows only, in workbook order; row number kept for the C22 ↔ row 22
        // cross-reference (decision A4). Values via ReadIntSafe semantics: int → value,
        // 0 preserved, empty → null, non-numeric text → null (NOT ReadInt — it throws on text).
        var rows = new List<SheetRow>();
        for (var row = FirstDataRow; row <= lastRow; row++)
        {
            if (!TryParseSubsampleKey(sheet, row, out var plot, out var subsample))
            {
                continue;
            }

            var values = new int?[9];
            for (var traitOrdinal = 1; traitOrdinal <= 9; traitOrdinal++)
            {
                values[traitOrdinal - 1] = ReadIntSafe(sheet.Cell(row, TraitColumn(traitOrdinal)));
            }

            rows.Add(new SheetRow(row, plot, subsample, values));
        }

        return new SheetRegion(columns, rows)
        {
            // Workbook-level ARM metadata strip (read-for-display approved 2026-10-07):
            // first non-empty cell scanning C→K on each metadata row.
            Metadata = new SheetMetadata(
                ReadMetadataValue(sheet, CropRow),
                ReadMetadataValue(sheet, RatingDateRow),
                ReadMetadataValue(sheet, RatingTypeRow),
                ReadMetadataValue(sheet, UnitRow),
                ReadMetadataValue(sheet, SampleSizeRow),
                ReadMetadataValue(sheet, SubsampleCountRow)),
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<RatingCell> WritePlotColumn(int plot, int traitOrdinal, int value, IReadOnlySet<int> subsampleIndices)
    {
        using var wb = OpenInput();
        var sheet = wb.Worksheet(SheetName);
        var column = TraitColumn(traitOrdinal);
        var rows = FindSubsampleRows(sheet, plot).ToDictionary(kv => kv.Value.subsample, kv => kv.Value.row);

        var written = new List<RatingCell>();
        foreach (var subsample in subsampleIndices.OrderBy(i => i))
        {
            if (!rows.TryGetValue(subsample, out var row))
            {
                _logger.LogDebug("Plot {Plot} has no subsample {Subsample}; skipping.", plot, subsample);
                continue;
            }

            var cell = sheet.Cell(row, column);
            // Idempotent (Rule 5): writing the same value to an already-equal cell is a no-op.
            cell.Value = value;
            written.Add(new RatingCell(plot, subsample, traitOrdinal, value, cell.Address.ToString() ?? string.Empty));
        }

        if (written.Count > 0)
        {
            SaveCopy(wb);
        }

        return written;
    }

    /// <inheritdoc />
    public PolicyWritePlotColumnResult WritePlotColumnPreserving(
        string workbookPath,
        string outputDirectory,
        int plot,
        int traitOrdinal,
        IReadOnlyList<int> values,
        IReadOnlySet<int> subsampleIndices,
        bool scanForFreeCells)
    {
        // Defense in depth: the rating region has exactly nine trait columns (C..K) and
        // traitOrdinal 0 would address column B — the row-key column. No caller may ever
        // write there or into an invalid column (the sync service catches this and flags).
        if (traitOrdinal is < 1 or > 9)
        {
            throw new InvalidDataException(
                $"traitOrdinal must be within 1..9 (one column per write), got {traitOrdinal}.");
        }

        using var wb = OpenWorkbook(workbookPath);
        var sheet = wb.Worksheet(SheetName);
        var rows = FindSubsampleRows(sheet, plot).ToDictionary(kv => kv.Value.subsample, kv => kv.Value.row);

        var written = new List<RatingCell>();
        var skipped = new List<RatingCell>();
        var missing = new List<int>();
        var outOfTraitRange = 0;

        foreach (var subsample in subsampleIndices.OrderBy(i => i))
        {
            if (!rows.TryGetValue(subsample, out var row))
            {
                _logger.LogDebug("Plot {Plot} has no subsample {Subsample}; skipping.", plot, subsample);
                missing.Add(subsample);
                continue;
            }

            if (scanForFreeCells)
            {
                // Category-block append (design.md §5): the recording's values fill the row's
                // NEXT FREE cells inside the Part Rated category block the first free cell
                // belongs to (row-8 metadata). An utterance NEVER spans category blocks —
                // values that cannot fit the current block are counted as out-of-trait-range
                // and flagged; progression to the next category happens on later utterances
                // once the block holds no free cells. Already-present cells are left untouched
                // and — unlike the fixed-column mode — never reported skipped (nothing is
                // "skipped" in an append; the cursor simply moves past it).
                outOfTraitRange += AppendToCategoryBlock(
                    sheet, row, plot, subsample, traitOrdinal, values, written);
                continue;
            }

            // Multi-value write: values fill the row's trait cells left-to-right, starting at
            // the recording's traitOrdinal (traitOrdinal 1 → column C, 2 → D, … 9 → K).
            for (var i = 0; i < values.Count; i++)
            {
                var targetTraitOrdinal = traitOrdinal + i;
                if (targetTraitOrdinal > 9)
                {
                    // Beyond the region's last trait column (K): neither written nor reported
                    // as skipped — COUNTED so the sync flags the recording for review (a
                    // spoken rating was lost); log the drop and continue.
                    _logger.LogDebug(
                        "Value {Value} for plot {Plot} subsample {Subsample} would land beyond trait 9 " +
                        "(traitOrdinal {TraitOrdinal} + index {Index}); ignored.",
                        values[i], plot, subsample, traitOrdinal, i);
                    outOfTraitRange++;
                    continue;
                }

                var cell = sheet.Cell(row, TraitColumn(targetTraitOrdinal));
                var isEmpty = cell.IsEmpty();
                var textIsMissing = !isEmpty
                    && cell.GetValue<string>().Trim().Equals("missing", StringComparison.OrdinalIgnoreCase);

                if (isEmpty || textIsMissing)
                {
                    // Locked-overwrite policy: only empty / "missing" cells accept a value (0 is valid).
                    cell.Value = values[i];
                    written.Add(new RatingCell(
                        plot, subsample, targetTraitOrdinal, values[i],
                        cell.Address.ToString() ?? string.Empty));
                }
                else
                {
                    // Already present (0 counts as a value): report the current content, walk away.
                    skipped.Add(new RatingCell(
                        plot,
                        subsample,
                        targetTraitOrdinal,
                        ReadIntSafe(cell),
                        cell.Address.ToString() ?? string.Empty));
                }
            }
        }

        if (written.Count > 0)
        {
            SaveCopyTo(wb, outputDirectory, workbookPath);
        }

        return new PolicyWritePlotColumnResult
        {
            PlotExists = rows.Count > 0,
            WrittenCells = written,
            SkippedPresentCells = skipped,
            MissingSubsampleIndices = missing,
            OutOfTraitRangeValueCount = outOfTraitRange,
        };
    }

    // ----- Generic entity-style methods: NOT supported by the workbook store -----

    public Task<Rating?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => throw new NotSupportedException("The workbook store does not address Rating entities by id; use the rating operations.");

    public Task<IReadOnlyList<Rating>> GetAllAsync(int? skip = null, int? take = null, CancellationToken ct = default)
        => throw new NotSupportedException("The workbook store does not list Rating entities; use ReadPlotColumn.");

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
        => throw new NotSupportedException("The workbook store does not address Rating entities by id.");

    public Task<int> CountAsync(CancellationToken ct = default)
        => throw new NotSupportedException("The workbook store does not count Rating entities.");

    // ----- Workbook helpers (ClosedXML confined here) -----

    private static int TraitColumn(int traitOrdinal)
        => traitOrdinal + 2; // column C(3)..K(11) => trait ordinal 1..9 (design.md §5)

    /// <summary>
    /// Category-block append for ONE subsample row (scan mode): locates the row's first
    /// WRITABLE trait cell at or after the recording's <paramref name="traitOrdinal"/>
    /// (empty or "missing"), then writes <paramref name="values"/> one per column from there,
    /// honouring the Part Rated category blocks read from row 8 — STRICTLY inside the block
    /// that first free cell belongs to: an utterance never spans category blocks. Values that
    /// cannot fit the current block are dropped and counted (their drops must surface for
    /// review, mirroring the fixed-column trait-9 overflow count); progression to the next
    /// category happens on later utterances once the block holds no free cells.
    /// </summary>
    private int AppendToCategoryBlock(
        IXLWorksheet sheet,
        int row,
        int plot,
        int subsample,
        int traitOrdinal,
        IReadOnlyList<int> values,
        List<RatingCell> written)
    {
        var regionStart = TraitColumn(traitOrdinal);
        var regionEnd = TraitColumn(9);

        // First writable cell of the row — the cursor starts here; everything before it is
        // already occupied, and an entirely occupied row has no legal column at all.
        var firstFree = 0;
        for (var col = regionStart; col <= regionEnd; col++)
        {
            if (IsWritableCell(sheet.Cell(row, col)))
            {
                firstFree = col;
                break;
            }
        }

        if (firstFree == 0)
        {
            _logger.LogDebug(
                "Plot {Plot} subsample {Subsample} has no free trait cell from column {Start} to {End}; " +
                "{Count} value(s) dropped.",
                plot, subsample, regionStart, regionEnd, values.Count);
            return values.Count;
        }

        // The append stays inside the category block (row-8 label run) that CONTAINS the
        // first free cell — a value may NOT spill into the NEXT leaf's columns, ever.
        var blocks = BuildCategoryBlocks(sheet, TraitColumn(1), TraitColumn(9));
        var targetBlock = blocks.First(b => firstFree >= b.StartColumn && firstFree <= b.EndColumn);

        _logger.LogDebug(
            "Category append for plot {Plot} subsample {Subsample}: first free cell {Address}, " +
            "category block '{Label}' (columns {Start}..{End}).",
            plot, subsample, sheet.Cell(row, firstFree).Address,
            targetBlock.Label, targetBlock.StartColumn, targetBlock.EndColumn);

        var pointer = firstFree;
        var dropped = 0;
        for (var i = 0; i < values.Count; i++)
        {
            // STRICT category rule: the cursor never leaves the block the first free cell
            // belongs to — one utterance = one Part Rated category. When the next value's
            // write would land at or past the block boundary, the value in hand and every
            // remaining value of the utterance have NO home in this category: they are
            // dropped and COUNTED (never silently lost, never spilled into the next leaf).
            // Progression to the next category happens on LATER utterances, once this block
            // holds no free cells at all (the cursor always targets the first free cell).
            if (pointer > targetBlock.EndColumn)
            {
                _logger.LogDebug(
                    "Value {Value} for plot {Plot} subsample {Subsample} has no free cell left " +
                    "in category block '{Label}' (columns {Start}..{End}); {Count} value(s) " +
                    "dropped — an utterance never spans category blocks.",
                    values[i], plot, subsample, targetBlock.Label,
                    targetBlock.StartColumn, targetBlock.EndColumn, values.Count - i);
                dropped = values.Count - i;
                break;
            }

            var cell = sheet.Cell(row, pointer);
            cell.Value = values[i];
            written.Add(new RatingCell(
                plot, subsample, pointer - 2 /* column C(3) → trait ordinal 1 */, values[i],
                cell.Address.ToString() ?? string.Empty));

            // Advance the cursor to the row's next writable cell INSIDE the same block
            // (skipping occupied holes); blockEnd + 1 signals "no writable cell left in this
            // category" and the guard above drops the remaining values of the utterance.
            pointer = AdvancePointer(sheet, row, pointer, targetBlock.EndColumn);
        }

        return dropped;
    }

    /// <summary>
    /// True when a cell may legally accept a value under the locked-overwrite policy: it is
    /// EMPTY, or its text is exactly "missing" (case-insensitive). Numeric content — 0
    /// included — and any other text count as "present" and are never overwritten.
    /// </summary>
    private static bool IsWritableCell(IXLCell cell)
        => cell.IsEmpty()
        || cell.GetValue<string>().Trim().Equals("missing", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Advances the append cursor to the row's next writable cell after
    /// <paramref name="fromColumn"/>, scanning ONLY inside the current category block (up to
    /// <paramref name="blockEnd"/>); occupied holes inside the block are skipped. Returns
    /// <paramref name="blockEnd"/> + 1 when no writable cell remains IN THE BLOCK — the
    /// caller then drops the remaining values of the utterance (strict rule: an utterance
    /// never spans category blocks).
    /// </summary>
    private static int AdvancePointer(
        IXLWorksheet sheet, int row, int fromColumn, int blockEnd)
    {
        for (var col = fromColumn + 1; col <= blockEnd; col++)
        {
            if (IsWritableCell(sheet.Cell(row, col)))
            {
                return col;
            }
        }

        return blockEnd + 1;
    }

    /// <summary>
    /// Reads the Part Rated category metadata from <see cref="PartRatedHeaderRow"/> and groups
    /// the trait columns <paramref name="startColumn"/>..<paramref name="endColumn"/> into
    /// contiguous blocks of EQUAL labels (compared case-insensitively, e.g. C:E "LEAF1",
    /// F:H "LEAF2", I:K "LEAF3"). An EMPTY label cell inherits the last non-empty label to
    /// its LEFT — the ARM header row is decoration, not a category change. When the LEADING
    /// label(s) of the region are empty but a later column is labelled, they inherit the NEXT
    /// non-empty label instead, so the region effectively starts as that category (a
    /// multi-value utterance whose first free cell is a leading column must not be forced
    /// into a singleton anonymous block). A region with NO non-empty label at all acts as ONE
    /// block so workbooks manufactured without the metadata still append sanely.
    /// </summary>
    private static List<(int StartColumn, int EndColumn, string Label)> BuildCategoryBlocks(
        IXLWorksheet sheet, int startColumn, int endColumn)
    {
        var normalized = NormalizeRowLabels(sheet, PartRatedHeaderRow, startColumn, endColumn);

        if (normalized.All(label => label.Length == 0))
        {
            return new List<(int, int, string)> { (startColumn, endColumn, string.Empty) };
        }

        var blocks = new List<(int StartColumn, int EndColumn, string Label)>();
        for (var col = startColumn; col <= endColumn;)
        {
            var label = normalized[col - startColumn];
            var runEnd = col;
            while (runEnd + 1 <= endColumn
                && string.Equals(normalized[(runEnd + 1) - startColumn], label, StringComparison.OrdinalIgnoreCase))
            {
                runEnd++;
            }

            blocks.Add((col, runEnd, label));
            col = runEnd + 1;
        }

        return blocks;
    }

    /// <summary>
    /// Normalizes one ARM label row (row 8 Part Rated, row 3 Pest Code) over
    /// <paramref name="startColumn"/>..<paramref name="endColumn"/> into one label per column:
    /// an EMPTY label cell inherits the last non-empty label to its LEFT — the ARM header row
    /// is decoration, not a category change. When the LEADING label(s) of the region are empty
    /// but a later column is labelled, they inherit the NEXT non-empty label instead, so the
    /// region effectively starts as that category. A region with NO non-empty label at all
    /// yields all-empty labels (callers fall back — e.g. one anonymous block for the category
    /// scan, null pest/part for the sheet columns). Shared by
    /// <see cref="BuildCategoryBlocks"/> and <see cref="ReadSheetRegion"/> so the two scans
    /// can never diverge.
    /// </summary>
    private static List<string> NormalizeRowLabels(IXLWorksheet sheet, int row, int startColumn, int endColumn)
    {
        var normalized = new List<string>(endColumn - startColumn + 1);
        var lastNonEmpty = string.Empty;
        for (var col = startColumn; col <= endColumn; col++)
        {
            var raw = sheet.Cell(row, col).GetValue<string>().Trim();
            if (raw.Length == 0)
            {
                normalized.Add(lastNonEmpty);
            }
            else
            {
                lastNonEmpty = raw;
                normalized.Add(raw);
            }
        }

        // Leading EMPTY labels (nothing non-empty before them) inherit the NEXT non-empty
        // label, so the region starts as that category instead of declaring an anonymous
        // single-column block that would drop every multi-value utterance starting at it.
        // (All-empty → FindIndex returns -1 and the loop never runs; nothing to inherit.)
        if (normalized.Count > 0 && normalized[0].Length == 0)
        {
            var firstNonEmptyIndex = normalized.FindIndex(label => label.Length > 0);
            for (var i = 0; i < firstNonEmptyIndex; i++)
            {
                normalized[i] = normalized[firstNonEmptyIndex];
            }
        }

        return normalized;
    }

    /// <summary>
    /// Reads and parses the column-B composite key ("{plot} {subsample}") of one row using
    /// the exact parse rules every key scan shares (<see cref="FindSubsampleRows"/>'s plot
    /// scan and <see cref="ReadSheetRegion"/>'s full-region scan): trimmed, split on spaces,
    /// exactly two int-parseable parts. Keys are parsed FROM the file, never hardcoded
    /// (design.md §5). Returns <see langword="false"/> for empty or malformed keys.
    /// </summary>
    private static bool TryParseSubsampleKey(IXLWorksheet sheet, int row, out int plot, out int subsample)
    {
        plot = 0;
        subsample = 0;

        var key = sheet.Cell(row, 2).GetValue<string>().Trim();
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var parts = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], out plot)
            && int.TryParse(parts[1], out subsample);
    }

    /// <summary>
    /// Builds a map of <c>(plot, subsample) → row</c> for the given plot by parsing the
    /// composite keys in column B ("{plot} {subsample}") via
    /// <see cref="TryParseSubsampleKey"/> — the SAME parse rules as the full-region scan in
    /// <see cref="ReadSheetRegion"/>. Keys are parsed FROM the file, never hardcoded
    /// (design.md §5).
    /// </summary>
    private static Dictionary<int, (int subsample, int row)> FindSubsampleRows(IXLWorksheet sheet, int plot)
    {
        var result = new Dictionary<int, (int subsample, int row)>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? FirstDataRow;

        for (var row = FirstDataRow; row <= lastRow; row++)
        {
            if (!TryParseSubsampleKey(sheet, row, out var keyPlot, out var subsample))
            {
                continue;
            }

            if (keyPlot == plot)
            {
                result.TryAdd(subsample, (subsample, row));
            }
        }

        return result;
    }

    /// <summary>Unwraps a cell value as an integer, or <see langword="null"/> when empty (empty ≠ 0).</summary>
    private static int? ReadInt(IXLCell cell)
        => cell.IsEmpty() ? null : (int)cell.GetDouble();

    private IXLWorkbook OpenInput()
    {
        var inputPath = ResolvePath(_options.InputPath);
        if (string.IsNullOrWhiteSpace(_options.InputPath) || !File.Exists(inputPath))
        {
            throw new FileNotFoundException(
                $"Excel input workbook not found at '{inputPath}' (configured '{_options.InputPath}'). " +
                "Configure 'Excel:InputPath' and place a file there.",
                inputPath);
        }

        return new XLWorkbook(inputPath);
    }

    /// <summary>Saves the (possibly written) workbook as a fresh output copy — the input is never overwritten.</summary>
    private void SaveCopy(IXLWorkbook wb)
    {
        if (string.IsNullOrWhiteSpace(_options.OutputDirectory))
        {
            throw new InvalidOperationException("'Excel:OutputDirectory' is not configured; cannot export the output copy.");
        }

        var outputDirectory = ResolvePath(_options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var sourceName = Path.GetFileNameWithoutExtension(ResolvePath(_options.InputPath));
        var outputPath = Path.Combine(
            outputDirectory,
            $"{sourceName}_out_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

        wb.SaveAs(outputPath);
        _logger.LogInformation("Exported output copy to {OutputPath}", outputPath);
    }

    /// <summary>
    /// Resolves a configured workbook path to an absolute path. Absolute paths pass through
    /// unchanged; relative paths are resolved against the repository root (the folder that
    /// contains <c>Service/</c>), so <c>Service/Input/...</c> works regardless of the launch
    /// directory or content root.
    /// </summary>
    private static string ResolvePath(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || Path.IsPathRooted(configured))
        {
            return configured ?? string.Empty;
        }

        var root = FindRepositoryRoot();
        return Path.GetFullPath(Path.Combine(root, configured));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Service")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        // Fallback: resolve relative to the current working directory.
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Opens the workbook at the given ABSOLUTE path, validating that it exists first (same
    /// not-found style as <see cref="OpenInput"/>). Consumers pass an explicit path — e.g. a
    /// trial's workbook — rather than the configured input.
    /// </summary>
    private static IXLWorkbook OpenWorkbook(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException(
                $"Excel input workbook not found at '{workbookPath}'.", workbookPath);
        }

        return new XLWorkbook(workbookPath);
    }

    /// <summary>
    /// Unwraps a cell value as an integer without throwing on non-numeric content:
    /// <see langword="null"/> for empty cells and text cells, the numeric value otherwise.
    /// Unlike <see cref="ReadInt"/>, it never calls <c>GetDouble()</c> on text (which ClosedXML
    /// throws on) — needed because a skipped cell may legally hold legacy text.
    /// </summary>
    private static int? ReadIntSafe(IXLCell cell)
        => cell.IsEmpty() || cell.DataType != XLDataType.Number ? null : (int)cell.GetDouble();

    /// <summary>
    /// Reads one ARM metadata value (display strip) as the FIRST NON-EMPTY cell of
    /// <paramref name="row"/> scanning C→K — the physical cells repeat per column in the
    /// real template, so the first occupied one is the logical value (plan §3). Returns
    /// <see langword="null"/> when the whole row is empty.
    /// </summary>
    private static string? ReadMetadataValue(IXLWorksheet sheet, int row)
    {
        for (var column = TraitColumn(1); column <= TraitColumn(9); column++)
        {
            var raw = sheet.Cell(row, column).GetValue<string>().Trim();
            if (raw.Length > 0)
            {
                return raw;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the display label of one trait column: "<c>PEST × PART</c>" when both halves
    /// are known; whatever is known alone otherwise; the column letter as the last resort
    /// (workbooks manufactured without the ARM label rows).
    /// </summary>
    private static string BuildColumnLabel(string? pestCode, string? partRated, string columnLetter)
        => (pestCode, partRated) switch
        {
            (not null, not null) => $"{pestCode} × {partRated}",
            (not null, null) => pestCode,
            (null, not null) => partRated,
            _ => columnLetter,
        };

    private static string? EmptyToNull(string value)
        => value.Length == 0 ? null : value;

    /// <summary>
    /// Saves the (possibly written) workbook as a fresh output copy into the given ABSOLUTE
    /// directory — the input workbook at <paramref name="workbookPath"/> is never overwritten.
    /// </summary>
    private void SaveCopyTo(IXLWorkbook wb, string outputDirectory, string workbookPath)
    {
        Directory.CreateDirectory(outputDirectory);

        var sourceName = Path.GetFileNameWithoutExtension(workbookPath);
        var outputPath = Path.Combine(
            outputDirectory,
            $"{sourceName}_out_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

        wb.SaveAs(outputPath);
        _logger.LogInformation("Exported output copy to {OutputPath}", outputPath);
    }
}
