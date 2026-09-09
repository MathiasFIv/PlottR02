using ClosedXML.Excel;

namespace PlottR0._2.Tests.Support.WorkbookBuilder;

/// <summary>
/// Generates a minimal valid xlsx workbook for trial upload tests.
/// The workbook is a simple valid file — no specific sheet layout is required
/// for the trial creation slice; the point is that ClosedXML can open it.
/// </summary>
public static class TrialWorkbookFactory
{
    /// <summary>
    /// Creates a minimal valid xlsx workbook at the given absolute path.
    /// </summary>
    /// <param name="outputPath">Absolute path where the workbook will be saved.</param>
    /// <returns>The absolute path of the created workbook.</returns>
    public static string Create(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "Trial Data";
        ws.Cell(2, 1).Value = "Sample Content";

        wb.SaveAs(outputPath);
        return outputPath;
    }

    /// <summary>
    /// Creates a minimal valid xlsx workbook as a byte array (for stream-based tests).
    /// </summary>
    /// <returns>A byte array containing a valid xlsx workbook.</returns>
    public static byte[] CreateBytes()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Sheet1");
            ws.Cell(1, 1).Value = "Trial Data";
            ws.Cell(2, 1).Value = "Sample Content";
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Creates a valid xlsx workbook and returns it as a readable stream.
    /// The caller is responsible for disposing the stream.
    /// </summary>
    public static Stream CreateStream()
    {
        var bytes = CreateBytes();
        return new MemoryStream(bytes);
    }
}
