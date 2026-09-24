using ClosedXML.Excel;

namespace PlottR0._2.Tests.Support.WorkbookBuilder;

/// <summary>
/// Generates pristine test workbooks for rating integration tests.
/// Creates a workbook with a <c>"Subsample Plot Data"</c> sheet matching the layout
/// expected by <see cref="PlottR0._2.Repository.Repositories.ClosedXmlRatingRepository"/>.
/// DO NOT read real <c>Service/*.xlsx</c> files (git-ignored / semi-confidential).
/// </summary>
public static class RatingWorkbookFactory
{
    public const string SheetName = "Subsample Plot Data";
    public const int FirstDataRow = 22;
    public const int HeaderRow = 21;
    public const int SubsamplesPerPlot = 4;

    /// <summary>
    /// Row 8 carries the Part Rated category metadata the repository's category-block append
    /// reads: B8 "Part Rated", C8:E8 "LEAF1", F8:H8 "LEAF2", I8:K8 "LEAF3".
    /// </summary>
    public const int PartRatedHeaderRow = 8;

    /// <summary>Plots covered by the default layout (101..104). Single source of truth for "which plots exist".</summary>
    public static readonly int[] Plots = [101, 102, 103, 104];

    /// <summary>Subsample indices the default layout emits for every plot (1..4).</summary>
    public static readonly int[] Subsamples = Enumerable.Range(1, SubsamplesPerPlot).ToArray();

    /// <summary>
    /// Creates a pristine workbook at the given absolute path.
    /// </summary>
    /// <param name="outputPath">Absolute path where the workbook will be saved.</param>
    /// <param name="prefilledCells">
    /// Optional dictionary of <c>(plot, subsample, traitOrdinal) → value</c> to pre-fill
    /// specific cells. Cells not listed here remain empty.
    /// </param>
    /// <param name="writePartRatedMetadata">
    /// When <see langword="false"/> the row-8 Part Rated labels are NOT emitted (see
    /// <see cref="CreateWithoutPartRated"/> for the dedicated convenience overload).
    /// </param>
    /// <returns>The absolute path of the created workbook.</returns>
    public static string Create(
        string outputPath,
        IReadOnlyDictionary<(int plot, int subsample, int traitOrdinal), int>? prefilledCells = null,
        bool writePartRatedMetadata = true)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SheetName);
        WriteHeaders(ws);
        if (writePartRatedMetadata)
        {
            WritePartRatedMetadata(ws);
        }

        // From row 22: column B = "{plot} {subsample}"
        // Plots 101..104 × subsamples 1..4 → rows 22..37
        var row = FirstDataRow;
        foreach (var plot in Plots)
        {
            foreach (var subsample in Subsamples)
            {
                ws.Cell(row, 2).Value = $"{plot} {subsample}";
                row++;
            }
        }

        // Pre-fill specific cells if requested
        if (prefilledCells is not null)
        {
            foreach (var ((plot, subsample, traitOrdinal), value) in prefilledCells)
            {
                var dataRow = DataRowFor(plot, subsample);
                var col = TraitColumnFor(traitOrdinal);
                ws.Cell(dataRow, col).Value = value;
            }
        }

        wb.SaveAs(outputPath);
        return outputPath;
    }

    /// <summary>
    /// <see cref="Create"/> without the row-8 Part Rated metadata — used by category-block
    /// tests that verify the unlabelled fallback (the whole trait region acts as ONE block).
    /// </summary>
    /// <param name="outputPath">Absolute path where the workbook will be saved.</param>
    /// <param name="prefilledCells">Optional <c>(plot, subsample, traitOrdinal) → value</c> pre-fills.</param>
    /// <returns>The absolute path of the created workbook.</returns>
    public static string CreateWithoutPartRated(
        string outputPath,
        IReadOnlyDictionary<(int plot, int subsample, int traitOrdinal), int>? prefilledCells = null)
        => Create(outputPath, prefilledCells, writePartRatedMetadata: false);

    /// <summary>
    /// Creates a self-contained pristine workbook whose subsample set includes a multi-digit
    /// index (&gt; 9) so the key parser's <c>Split(' ')</c>/<c>int.TryParse</c> path can be
    /// proven end-to-end (e.g. key <c>"101 10"</c>). Rows are emitted contiguously from
    /// <see cref="FirstDataRow"/> in <paramref name="subsamples"/> order — deliberately NOT the
    /// default plot-block layout — so the default layout helpers keep their documented meaning
    /// for <see cref="Create"/> workbooks and existing tests are unaffected.
    /// </summary>
    /// <param name="outputPath">Absolute path where the workbook will be saved.</param>
    /// <param name="plot">The single plot emitted (default 101).</param>
    /// <param name="subsamples">The subsample indices to emit (default 1,2,3,4,10 — includes a multi-digit key).</param>
    /// <returns>The created path plus the emitted <c>(subsample → worksheet row)</c> map.</returns>
    public static (string Path, IReadOnlyDictionary<int, int> RowBySubsample) CreateWithMultiDigitSubsample(
        string outputPath,
        int plot = 101,
        IReadOnlyList<int>? subsamples = null)
    {
        subsamples ??= [1, 2, 3, 4, 10];

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SheetName);
        WriteHeaders(ws);
        WritePartRatedMetadata(ws);

        var rowBySubsample = new Dictionary<int, int>();
        var row = FirstDataRow;
        foreach (var subsample in subsamples)
        {
            ws.Cell(row, 2).Value = $"{plot} {subsample}";
            rowBySubsample[subsample] = row;
            row++;
        }

        wb.SaveAs(outputPath);
        return (outputPath, rowBySubsample);
    }

    /// <summary>
    /// Computes the 1-based worksheet row for a given (plot, subsample) pair in the
    /// DEFAULT layout. Plots 101..104 × subsamples 1..4 → rows 22..37.
    /// </summary>
    public static int DataRowFor(int plot, int subsample)
        => FirstDataRow + (plot - Plots[0]) * SubsamplesPerPlot + (subsample - 1);

    /// <summary>
    /// Converts a trait ordinal (1..9) to a 1-based column number (C=3..K=11).
    /// </summary>
    public static int TraitColumnFor(int traitOrdinal)
        => traitOrdinal + 2;

    /// <summary>
    /// Writes the metadata/header region (row 21). Realism only — the parser reads
    /// column B keys from row 22 onwards.
    /// </summary>
    private static void WriteHeaders(IXLWorksheet ws)
    {
        ws.Cell(HeaderRow, 1).Value = "Plot";
        ws.Cell(HeaderRow, 2).Value = "Subsample Key";
        for (var t = 1; t <= 9; t++)
            ws.Cell(HeaderRow, t + 2).Value = $"Trait {t}";
    }

    /// <summary>
    /// Writes the Part Rated category metadata on row 8 — the ARM row the repository's
    /// category-block append derives its leaf runs from: B8 "Part Rated" header, then
    /// C:E "LEAF1", F:H "LEAF2", I:K "LEAF3" over the nine trait columns.
    /// </summary>
    private static void WritePartRatedMetadata(IXLWorksheet ws)
    {
        ws.Cell(PartRatedHeaderRow, 2).Value = "Part Rated";
        ws.Cell(PartRatedHeaderRow, 3).Value = "LEAF1"; // C:E
        ws.Cell(PartRatedHeaderRow, 6).Value = "LEAF2"; // F:H
        ws.Cell(PartRatedHeaderRow, 9).Value = "LEAF3"; // I:K
    }
}