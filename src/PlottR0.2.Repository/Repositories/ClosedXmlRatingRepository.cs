using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Repository.Storage;

namespace PlottR0._2.Repository.Repositories;

public class ClosedXmlRatingRepository : IRatingRepository
{
    private const string SheetName = "Subsample Plot Data";
    private const int FirstDataRow = 22;

    // Workbook layout of "Subsample Plot Data": row 3 carries the pest codes, row 8 the Part Rated
    // category labels whose runs define the append blocks.
    private const int PartRatedHeaderRow = 8;

    private const int PestHeaderRow = 3;

    private const int CropRow = 5;
    private const int RatingDateRow = 6;
    private const int RatingTypeRow = 10;
    private const int UnitRow = 11;
    private const int SampleSizeRow = 12;
    private const int SubsampleCountRow = 20;

    private readonly ILogger<ClosedXmlRatingRepository> _logger;

    public ClosedXmlRatingRepository(ILogger<ClosedXmlRatingRepository> logger)
    {
        _logger = logger;
    }

    public SheetRegion ReadSheetRegion(string workbookPath)
    {
        using var wb = OpenWorkbook(workbookPath);
        if (!wb.TryGetWorksheet(SheetName, out var sheet) || sheet is null)
        {
            throw new InvalidDataException(
                $"Sheet '{SheetName}' was not found in workbook '{Path.GetFileName(workbookPath)}'.");
        }

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? FirstDataRow;

        var pestLabels = NormalizeRowLabels(sheet, PestHeaderRow, TraitColumn(1), TraitColumn(9));
        var partLabels = NormalizeRowLabels(sheet, PartRatedHeaderRow, TraitColumn(1), TraitColumn(9));

        var columns = new List<SheetColumn>(capacity: 9);
        for (var traitOrdinal = 1; traitOrdinal <= 9; traitOrdinal++)
        {
            var index = traitOrdinal - 1;
            var column = TraitColumn(traitOrdinal);
            var letter = XLHelper.GetColumnLetterFromNumber(column);
            var pestCode = EmptyToNull(pestLabels[index]);
            var partRated = EmptyToNull(partLabels[index]);
            columns.Add(new SheetColumn(
                traitOrdinal, letter, pestCode, partRated, BuildColumnLabel(pestCode, partRated, letter)));
        }

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
            Metadata = new SheetMetadata(
                ReadMetadataValue(sheet, CropRow),
                ReadMetadataValue(sheet, RatingDateRow),
                ReadMetadataValue(sheet, RatingTypeRow),
                ReadMetadataValue(sheet, UnitRow),
                ReadMetadataValue(sheet, SampleSizeRow),
                ReadMetadataValue(sheet, SubsampleCountRow)),
        };
    }

    /// <summary>
    /// Writes values down one trait column of a plot's subsample rows, preserving the input: a
    /// "<c>&lt;source&gt;_out_&lt;stamp&gt;.xlsx</c>" copy is written to <paramref name="outputDirectory"/>
    /// and the source workbook is never mutated. A trait ordinal outside 1..9 is refused so column B
    /// (the plot/subsample key) can never be addressed.
    /// </summary>
    public PolicyWritePlotColumnResult WritePlotColumnPreserving(
        string workbookPath,
        string outputDirectory,
        int plot,
        int traitOrdinal,
        IReadOnlyList<int> values,
        IReadOnlySet<int> subsampleIndices,
        bool scanForFreeCells)
    {
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
                outOfTraitRange += AppendToCategoryBlock(
                    sheet, row, plot, subsample, traitOrdinal, values, written);
                continue;
            }

            for (var i = 0; i < values.Count; i++)
            {
                var targetTraitOrdinal = traitOrdinal + i;
                if (targetTraitOrdinal > 9)
                {
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
                    cell.Value = values[i];
                    written.Add(new RatingCell(
                        plot, subsample, targetTraitOrdinal, values[i],
                        cell.Address.ToString() ?? string.Empty));
                }
                else
                {
                    skipped.Add(new RatingCell(
                        plot,
                        subsample,
                        targetTraitOrdinal,
                        ReadIntSafe(cell),
                        cell.Address.ToString() ?? string.Empty));
                }
            }
        }

        var outputPath = written.Count > 0 ? SaveCopyTo(wb, outputDirectory, workbookPath) : null;

        return new PolicyWritePlotColumnResult
        {
            PlotExists = rows.Count > 0,
            WrittenCells = written,
            SkippedPresentCells = skipped,
            MissingSubsampleIndices = missing,
            OutOfTraitRangeValueCount = outOfTraitRange,
            OutputPath = outputPath,
        };
    }

    public SingleCellWriteResult WriteSingleCell(
        string workbookPath,
        string outputDirectory,
        int plot,
        int subsample,
        int traitOrdinal,
        int value,
        bool allowOverwrite)
    {
        if (traitOrdinal is < 1 or > 9)
        {
            throw new InvalidDataException(
                $"traitOrdinal must be within 1..9 (one column per write), got {traitOrdinal}.");
        }

        using var wb = OpenWorkbook(workbookPath);
        var sheet = wb.Worksheet(SheetName);

        var rows = FindSubsampleRows(sheet, plot).ToDictionary(kv => kv.Value.subsample, kv => kv.Value.row);
        if (!rows.TryGetValue(subsample, out var row))
        {
            _logger.LogDebug(
                "Plot {Plot} has no subsample {Subsample}; single-cell write skipped.", plot, subsample);
            return new SingleCellWriteResult
            {
                Written = false,
                Skipped = true,
                SkippedReason = SingleCellWriteResult.RowNotFound,
                CellAddress = string.Empty,
                Plot = plot,
                Subsample = subsample,
                TraitOrdinal = traitOrdinal,
                Value = value,
            };
        }

        var cell = sheet.Cell(row, TraitColumn(traitOrdinal));
        var priorValue = ReadIntSafe(cell);

        if (!IsWritableCell(cell) && !allowOverwrite)
        {
            return new SingleCellWriteResult
            {
                Written = false,
                Skipped = true,
                SkippedReason = SingleCellWriteResult.CellAlreadyFilled,
                PriorValue = priorValue,
                CellAddress = cell.Address.ToString() ?? string.Empty,
                Plot = plot,
                Subsample = subsample,
                TraitOrdinal = traitOrdinal,
                Value = value,
            };
        }

        cell.Value = value;

        var outputPath = SaveCopyTo(wb, outputDirectory, workbookPath);

        return new SingleCellWriteResult
        {
            Written = true,
            Skipped = false,
            SkippedReason = null,
            PriorValue = priorValue,
            CellAddress = cell.Address.ToString() ?? string.Empty,
            Plot = plot,
            Subsample = subsample,
            TraitOrdinal = traitOrdinal,
            Value = value,
            OutputPath = outputPath,
        };
    }

    private static int TraitColumn(int traitOrdinal)
        => traitOrdinal + 2;

    /// <summary>
    /// Fills the next free cells inside the Part Rated category block that contains the first free
    /// cell. An utterance never spans blocks: values that do not fit the current block are dropped
    /// and counted (never spilled into the next leaf), and occupied cells are left untouched.
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
                plot, subsample, pointer - 2, values[i],
                cell.Address.ToString() ?? string.Empty));

            pointer = AdvancePointer(sheet, row, pointer, targetBlock.EndColumn);
        }

        return dropped;
    }

    // Empty, or the literal marker "missing", counts as writable; any present value does too —
    // including a numeric 0.
    private static bool IsWritableCell(IXLCell cell)
        => cell.IsEmpty()
        || cell.GetValue<string>().Trim().Equals("missing", StringComparison.OrdinalIgnoreCase);

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

    // An empty label cell inherits the nearest non-empty label to its left, so a merged-style
    // header row resolves to one label per category run.
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

    private static IXLWorkbook OpenWorkbook(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException(
                $"Excel input workbook not found at '{workbookPath}'.", workbookPath);
        }

        return new XLWorkbook(workbookPath);
    }

    // Reading a text cell (e.g. "missing") as a number throws in ClosedXML, so only a real numeric
    // cell is read; anything else is treated as absent.
    private static int? ReadIntSafe(IXLCell cell)
        => cell.IsEmpty() || cell.DataType != XLDataType.Number ? null : (int)cell.GetDouble();

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
    /// Saves the edited workbook as "<c>&lt;base&gt;_out_&lt;yyyyMMdd_HHmmss_fff&gt;.xlsx</c>" in
    /// <paramref name="outputDirectory"/> and returns the absolute path written. The base is the
    /// source name with any generated chain stripped (never re-derived from it) and truncated to
    /// <see cref="ObjectKeys.MaxOutputBaseLength"/>, so the name stays bounded at ~129 chars instead
    /// of growing by a stamp per write toward the 255-char filesystem limit. A name taken by an
    /// instant-earlier copy is dodged by bumping the stamp one millisecond; the per-trial write gate
    /// serialises writers in-process, and the copy is published with a no-clobber move so a colliding
    /// name can never silently overwrite an existing copy — the uniqueness the old chained names
    /// only got for free by growing the filename.
    /// </summary>
    private string SaveCopyTo(IXLWorkbook wb, string outputDirectory, string workbookPath)
    {
        Directory.CreateDirectory(outputDirectory);

        // ClosedXML refuses a non-OpenXML extension, so staging keeps the ".xlsx" name but lives in
        // a hidden sub-directory: every reader scans the working directory NON-recursively for
        // "*.xlsx", so a half-written copy is never observed. Publishing is a same-volume rename.
        var stagingDirectory = Path.Combine(outputDirectory, StagingDirectoryName);
        Directory.CreateDirectory(stagingDirectory);

        var sourceName = Path.GetFileNameWithoutExtension(workbookPath);
        var stamp = DateTime.Now;

        for (var attempt = 0; attempt < MaxCopyNameAttempts; attempt++)
        {
            var fileName = ObjectKeys.BuildOutputFileName(sourceName, stamp);
            var stagingPath = Path.Combine(stagingDirectory, fileName);
            var outputPath = Path.Combine(outputDirectory, fileName);

            try
            {
                wb.SaveAs(stagingPath);

                // Atomic publish: throws IOException when the target already exists, which is the
                // uniqueness guarantee the stamp loop alone could not give (an Exists-then-SaveAs
                // window would silently overwrite an existing copy - the property the old chained
                // names only got for free by growing the filename).
                File.Move(stagingPath, outputPath, overwrite: false);

                _logger.LogInformation("Exported output copy to {OutputPath}", outputPath);
                return outputPath;
            }
            catch (IOException) when (File.Exists(outputPath))
            {
                // Name was taken between the Exists check and the move - bump and try again.
                stamp = stamp.AddMilliseconds(1);
            }
            finally
            {
                // Never leave a staging copy behind when the move did not consume it.
                if (File.Exists(stagingPath))
                {
                    try
                    {
                        File.Delete(stagingPath);
                    }
                    catch
                    {
                        // Best effort: a leftover staged file is invisible to every reader.
                    }
                }
            }
        }

        throw new IOException(
            $"Could not allocate a free output-copy name for '{sourceName}' in '{outputDirectory}' " +
            $"after {MaxCopyNameAttempts} attempts.");
    }

    private const int MaxCopyNameAttempts = 100;

    private const string StagingDirectoryName = ".staging";
}
