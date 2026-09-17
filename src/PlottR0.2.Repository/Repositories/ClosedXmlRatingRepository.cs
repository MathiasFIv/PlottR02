using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        int value,
        IReadOnlySet<int> subsampleIndices)
    {
        using var wb = OpenWorkbook(workbookPath);
        var sheet = wb.Worksheet(SheetName);
        var column = TraitColumn(traitOrdinal);
        var rows = FindSubsampleRows(sheet, plot).ToDictionary(kv => kv.Value.subsample, kv => kv.Value.row);

        var written = new List<RatingCell>();
        var skipped = new List<RatingCell>();
        var missing = new List<int>();

        foreach (var subsample in subsampleIndices.OrderBy(i => i))
        {
            if (!rows.TryGetValue(subsample, out var row))
            {
                _logger.LogDebug("Plot {Plot} has no subsample {Subsample}; skipping.", plot, subsample);
                missing.Add(subsample);
                continue;
            }

            var cell = sheet.Cell(row, column);
            var isEmpty = cell.IsEmpty();
            var textIsMissing = !isEmpty
                && cell.GetValue<string>().Trim().Equals("missing", StringComparison.OrdinalIgnoreCase);

            if (isEmpty || textIsMissing)
            {
                // Locked-overwrite policy: only empty / "missing" cells accept a value (0 is valid).
                cell.Value = value;
                written.Add(new RatingCell(plot, subsample, traitOrdinal, value, cell.Address.ToString() ?? string.Empty));
            }
            else
            {
                // Already present (0 counts as a value): report the current content, walk away.
                skipped.Add(new RatingCell(
                    plot,
                    subsample,
                    traitOrdinal,
                    ReadIntSafe(cell),
                    cell.Address.ToString() ?? string.Empty));
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
    /// Builds a map of <c>(plot, subsample) → row</c> for the given plot by parsing the
    /// composite keys in column B ("{plot} {subsample}"). Keys are parsed FROM the file,
    /// never hardcoded (design.md §5).
    /// </summary>
    private static Dictionary<int, (int subsample, int row)> FindSubsampleRows(IXLWorksheet sheet, int plot)
    {
        var result = new Dictionary<int, (int subsample, int row)>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? FirstDataRow;

        for (var row = FirstDataRow; row <= lastRow; row++)
        {
            var key = sheet.Cell(row, 2).GetValue<string>().Trim();
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            var parts = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out var keyPlot) ||
                !int.TryParse(parts[1], out var subsample))
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
