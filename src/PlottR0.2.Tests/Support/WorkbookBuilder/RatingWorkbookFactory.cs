using ClosedXML.Excel;

namespace PlottR0._2.Tests.Support.WorkbookBuilder;

public static class RatingWorkbookFactory
{
    public const string SheetName = "Subsample Plot Data";
    public const int FirstDataRow = 22;
    public const int HeaderRow = 21;
    public const int SubsamplesPerPlot = 4;

    public const int PartRatedHeaderRow = 8;

    public const int PestHeaderRow = 3;

    public static readonly int[] Plots = [101, 102, 103, 104];

    public static readonly int[] Subsamples = Enumerable.Range(1, SubsamplesPerPlot).ToArray();

    public static Stream CreateStream()
    {
        var temp = Path.Combine(
            Path.GetTempPath(), "PlottrRatings_" + Guid.NewGuid().ToString("N") + ".xlsx");
        Create(temp);
        return new FileStream(
            temp, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.DeleteOnClose);
    }

    public static string Create(
        string outputPath,
        IReadOnlyDictionary<(int plot, int subsample, int traitOrdinal), int>? prefilledCells = null,
        bool writePartRatedMetadata = true,
        bool writePestCodes = false,
        bool writeArmMetadata = false)
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
        if (writePestCodes)
        {
            WritePestCodes(ws);
        }
        if (writeArmMetadata)
        {
            WriteArmMetadata(ws);
        }

        var row = FirstDataRow;
        foreach (var plot in Plots)
        {
            foreach (var subsample in Subsamples)
            {
                ws.Cell(row, 2).Value = $"{plot} {subsample}";
                row++;
            }
        }

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

    public static string CreateWithoutPartRated(
        string outputPath,
        IReadOnlyDictionary<(int plot, int subsample, int traitOrdinal), int>? prefilledCells = null)
        => Create(outputPath, prefilledCells, writePartRatedMetadata: false);

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

    public static int DataRowFor(int plot, int subsample)
        => FirstDataRow + (plot - Plots[0]) * SubsamplesPerPlot + (subsample - 1);

    public static int TraitColumnFor(int traitOrdinal)
        => traitOrdinal + 2;

    private static void WriteHeaders(IXLWorksheet ws)
    {
        ws.Cell(HeaderRow, 1).Value = "Plot";
        ws.Cell(HeaderRow, 2).Value = "Subsample Key";
        for (var t = 1; t <= 9; t++)
            ws.Cell(HeaderRow, t + 2).Value = $"Trait {t}";
    }

    private static void WritePartRatedMetadata(IXLWorksheet ws)
    {
        ws.Cell(PartRatedHeaderRow, 2).Value = "Part Rated";
        ws.Cell(PartRatedHeaderRow, 3).Value = "LEAF1";
        ws.Cell(PartRatedHeaderRow, 6).Value = "LEAF2";
        ws.Cell(PartRatedHeaderRow, 9).Value = "LEAF3";
    }

    private static void WritePestCodes(IXLWorksheet ws)
    {
        string[] pests = ["SEPTTR", "ERYSGR", "PUCCST"];
        for (var col = 3; col <= 11; col++)
        {
            ws.Cell(PestHeaderRow, col).Value = pests[(col - 3) % pests.Length];
        }
    }

    private static void WriteArmMetadata(IXLWorksheet ws)
    {
        for (var col = 3; col <= 11; col++)
        {
            ws.Cell(5, col).Value = "Winter wheat";
        }

        ws.Cell(6, 3).Value = "16Jun2026";

        for (var col = 3; col <= 11; col++)
        {
            ws.Cell(10, col).Value = "PESSEV";
            ws.Cell(11, col).Value = "%AREA";
            ws.Cell(12, col).Value = "4";
            ws.Cell(20, col).Value = "4";
        }
    }
}