using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using PlottR0._2.Repository.Repositories;
using Factory = PlottR0._2.Tests.Support.WorkbookBuilder.RatingWorkbookFactory;

namespace PlottR0._2.Tests;

public sealed class SheetRegionReadTests : IDisposable
{
    private readonly string _tempDir;

    public SheetRegionReadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PlottrSheetRegion_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static ClosedXmlRatingRepository CreateRepository() =>
        new(NullLogger<ClosedXmlRatingRepository>.Instance);

    private string Workbook(string name) => Path.Combine(_tempDir, name);

    [Fact]
    public void ReadSheetRegion_KeyedRows_ReturnsKeyPresentRowsInWorkbookOrder()
    {
        var path = Support.WorkbookBuilder.RatingWorkbookFactory.Create(Workbook("keys.xlsx"));

        var region = CreateRepository().ReadSheetRegion(path);

        region.Rows.Should().HaveCount(16, "4 plots × 4 subsamples are all key-present");
        region.Rows.Select(r => r.Row)
            .Should().BeInAscendingOrder("rows must come back in workbook order");
        region.Rows.Select(r => r.Row)
            .Should().Equal(Enumerable.Range(22, 16), "the keyed rows are exactly 22..37");

        var first = region.Rows[0];
        first.Plot.Should().Be(101);
        first.Subsample.Should().Be(1);
        var last = region.Rows[^1];
        last.Plot.Should().Be(104);
        last.Subsample.Should().Be(4);
    }

    [Fact]
    public void ReadSheetRegion_PestCodes_MapsRowThreeAndFallsBackWhenOmitted()
    {
        var withPests = Support.WorkbookBuilder.RatingWorkbookFactory.Create(
            Workbook("pests.xlsx"), writePestCodes: true);
        var withoutPests = Support.WorkbookBuilder.RatingWorkbookFactory.Create(
            Workbook("no pests.xlsx"));

        var columnsWith = CreateRepository().ReadSheetRegion(withPests).Columns;
        var columnsWithout = CreateRepository().ReadSheetRegion(withoutPests).Columns;

        columnsWith.Select(c => c.PestCode).Should().Equal(
            "SEPTTR", "ERYSGR", "PUCCST",
            "SEPTTR", "ERYSGR", "PUCCST",
            "SEPTTR", "ERYSGR", "PUCCST");
        columnsWith.Select(c => c.Label).Should().Equal(
            "SEPTTR × LEAF1", "ERYSGR × LEAF1", "PUCCST × LEAF1",
            "SEPTTR × LEAF2", "ERYSGR × LEAF2", "PUCCST × LEAF2",
            "SEPTTR × LEAF3", "ERYSGR × LEAF3", "PUCCST × LEAF3");

        columnsWithout.Should().AllSatisfy(c => c.PestCode.Should().BeNull());
        columnsWithout.Select(c => c.Label)
            .Should().Equal("LEAF1", "LEAF1", "LEAF1", "LEAF2", "LEAF2", "LEAF2", "LEAF3", "LEAF3", "LEAF3");
    }

    [Fact]
    public void ReadSheetRegion_RowEight_MapsPartRatedBlocksOrNullWhenOmitted()
    {
        var withParts = Support.WorkbookBuilder.RatingWorkbookFactory.Create(Workbook("parts.xlsx"));
        var withoutParts = Support.WorkbookBuilder.RatingWorkbookFactory.CreateWithoutPartRated(
            Workbook("no parts.xlsx"));

        var columnsWith = CreateRepository().ReadSheetRegion(withParts).Columns;
        var columnsWithout = CreateRepository().ReadSheetRegion(withoutParts).Columns;

        columnsWith.Select(c => c.PartRated).Should().Equal(
            "LEAF1", "LEAF1", "LEAF1",
            "LEAF2", "LEAF2", "LEAF2",
            "LEAF3", "LEAF3", "LEAF3");

        columnsWithout.Should().AllSatisfy(c => c.PartRated.Should().BeNull());
        columnsWithout.Select(c => c.Label)
            .Should().Equal("C", "D", "E", "F", "G", "H", "I", "J", "K");
    }

    [Fact]
    public void ReadSheetRegion_Values_ZeroIsRealAndEmptyAndTextAreNull()
    {
        var path = Support.WorkbookBuilder.RatingWorkbookFactory.Create(
            Workbook("values.xlsx"),
            prefilledCells: new Dictionary<(int plot, int subsample, int traitOrdinal), int>
            {
                [(101, 1, 1)] = 0,
                [(101, 1, 2)] = 7,
            });

        using (var wb = new XLWorkbook(path))
        {
            wb.Worksheet(Support.WorkbookBuilder.RatingWorkbookFactory.SheetName)
                .Cell(22, Support.WorkbookBuilder.RatingWorkbookFactory.TraitColumnFor(3))
                .Value = "n.a.";
            wb.Save();
        }

        var region = CreateRepository().ReadSheetRegion(path);
        var row22 = region.Rows.Single(r => r.Row == 22);

        row22.Values.Should().HaveCount(9);
        row22.Values[0].Should().Be(0, "0 is a real rating value (empty ≠ 0 — Rule 2)");
        row22.Values[1].Should().Be(7);
        row22.Values[2].Should().BeNull("non-numeric text collapses to null — never an exception");
        row22.Values[3].Should().BeNull("an untouched cell stays null — never fabricated as 0");
    }

    [Fact]
    public void ReadSheetRegion_ValuesAreIndexAlignedWithColumns()
    {
        var path = Factory.Create(
            Workbook("invariant.xlsx"),
            prefilledCells: new Dictionary<(int plot, int subsample, int traitOrdinal), int>
            {
                [(102, 3, 9)] = 4,
            },
            writePestCodes: true);

        var region = CreateRepository().ReadSheetRegion(path);

        region.Columns.Should().HaveCount(9);
        for (var i = 0; i < region.Columns.Count; i++)
        {
            region.Columns[i].TraitOrdinal.Should().Be(i + 1, "values[i] ↔ columns[i].traitOrdinal = i + 1");
            region.Columns[i].Column.Should().Be(
                XLHelper.GetColumnLetterFromNumber(region.Columns[i].TraitOrdinal + 2),
                "TraitColumn(t) = t + 2 (C..K ⇔ 1..9)");
        }

        foreach (var row in region.Rows)
        {
            row.Values.Should().HaveCount(region.Columns.Count, "every row carries one slot per column");
        }

        var target = region.Rows.Single(r => r.Plot == 102 && r.Subsample == 3);
        target.Values[8].Should().Be(4, "trait ordinal 9 lives at values[8] (column K)");
    }

    [Fact]
    public void ReadSheetRegion_RowNumbers_MatchWorkbookRows()
    {
        var path = Factory.Create(Workbook("row numbers.xlsx"));

        var region = CreateRepository().ReadSheetRegion(path);

        foreach (var row in region.Rows)
        {
            row.Row.Should().Be(Factory.DataRowFor(row.Plot, row.Subsample),
                "row must equal the workbook row the key was parsed from");
        }
    }

    [Fact]
    public void ReadSheetRegion_MissingSheet_ThrowsInvalidDataException()
    {
        var path = Workbook("wrong sheet.xlsx");
        using (var wb = new XLWorkbook())
        {
            wb.Worksheets.Add("Some Other Sheet").Cell(1, 1).Value = "not the ratings sheet";
            wb.SaveAs(path);
        }

        var act = () => CreateRepository().ReadSheetRegion(path);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("Sheet 'Subsample Plot Data' was not found in workbook 'wrong sheet.xlsx'.");
    }

    [Fact]
    public void ReadSheetRegion_Metadata_ReadsFirstNonEmptyCellScanningCToK()
    {
        var path = Support.WorkbookBuilder.RatingWorkbookFactory.Create(
            Workbook("metadata.xlsx"), writeArmMetadata: true);

        var latePath = Workbook("late metadata.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add(Support.WorkbookBuilder.RatingWorkbookFactory.SheetName);
            ws.Cell(5, 6).Value = "Only at F";
            ws.Cell(6, 11).Value = "Only at K";
            wb.SaveAs(latePath);
        }

        var metadata = CreateRepository().ReadSheetRegion(path).Metadata;
        var lateMetadata = CreateRepository().ReadSheetRegion(latePath).Metadata;

        metadata.Should().NotBeNull();
        metadata!.Crop.Should().Be("Winter wheat");
        metadata.RatingDate.Should().Be("16Jun2026");
        metadata.RatingType.Should().Be("PESSEV");
        metadata.Unit.Should().Be("%AREA");
        metadata.SampleSize.Should().Be("4");
        metadata.SubsampleCount.Should().Be("4");

        lateMetadata.Should().NotBeNull();
        lateMetadata!.Crop.Should().Be("Only at F", "C..E are empty; F is the first non-empty cell");
        lateMetadata.RatingDate.Should().Be("Only at K");
        lateMetadata.RatingType.Should().BeNull("row 10 is untouched in this workbook");
    }
}
