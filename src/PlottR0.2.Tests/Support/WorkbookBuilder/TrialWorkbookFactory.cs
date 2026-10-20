using ClosedXML.Excel;

namespace PlottR0._2.Tests.Support.WorkbookBuilder;

public static class TrialWorkbookFactory
{
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

    public static Stream CreateStream()
    {
        var bytes = CreateBytes();
        return new MemoryStream(bytes);
    }
}
