using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using PlottR0._2.Repository.Repositories;
using Factory = PlottR0._2.Tests.Support.WorkbookBuilder.RatingWorkbookFactory;

namespace PlottR0._2.Tests;

/// <summary>
/// Pure file-I/O tests for <see cref="ClosedXmlRatingRepository.ReadSheetRegion"/> — the
/// region reader behind <c>GET /api/Trials/{trialId}/sheet</c>. Builds throwaway workbooks
/// with <see cref="Support.WorkbookBuilder.RatingWorkbookFactory"/> in a per-test temp dir;
/// no Postgres (plain xUnit class, deliberately outside the Postgres collection).
/// </summary>
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
            // Best-effort cleanup.
        }
    }

    private static ClosedXmlRatingRepository CreateRepository() =>
        new(NullLogger<ClosedXmlRatingRepository>.Instance);

    private string Workbook(string name) => Path.Combine(_tempDir, name);

    [Fact]
    public void ReadSheetRegion_KeyedRows_ReturnsKeyPresentRowsInWorkbookOrder()
    {
        // Arrange — the default factory emits 16 keyed rows (plots 101..104 × 4 subsamples),
        // rows 22..37; no spare/empty rows exist to confuse the scan.
        var path = Support.WorkbookBuilder.RatingWorkbookFactory.Create(Workbook("keys.xlsx"));

        // Act
        var region = CreateRepository().ReadSheetRegion(path);

        // Assert — key-present rows only, workbook order, row numbers kept (decision A4)
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
        // Arrange — one workbook WITH row 3 (pest codes opted in) and one WITHOUT (the
        // default factory), proving the null-pest fallback both paths can rot.
        var withPests = Support.WorkbookBuilder.RatingWorkbookFactory.Create(
            Workbook("pests.xlsx"), writePestCodes: true);
        var withoutPests = Support.WorkbookBuilder.RatingWorkbookFactory.Create(
            Workbook("no pests.xlsx"));

        // Act
        var columnsWith = CreateRepository().ReadSheetRegion(withPests).Columns;
        var columnsWithout = CreateRepository().ReadSheetRegion(withoutPests).Columns;

        // Assert — row 3 maps per column: SEPTTR/ERYSGR/PUCCST repeated per LEAF block
        columnsWith.Select(c => c.PestCode).Should().Equal(
            "SEPTTR", "ERYSGR", "PUCCST",
            "SEPTTR", "ERYSGR", "PUCCST",
            "SEPTTR", "ERYSGR", "PUCCST");
        columnsWith.Select(c => c.Label).Should().Equal(
            "SEPTTR × LEAF1", "ERYSGR × LEAF1", "PUCCST × LEAF1",
            "SEPTTR × LEAF2", "ERYSGR × LEAF2", "PUCCST × LEAF2",
            "SEPTTR × LEAF3", "ERYSGR × LEAF3", "PUCCST × LEAF3");

        // Assert — factory without row 3 ⇒ pest null everywhere, part still drives the label
        columnsWithout.Should().AllSatisfy(c => c.PestCode.Should().BeNull());
        columnsWithout.Select(c => c.Label)
            .Should().Equal("LEAF1", "LEAF1", "LEAF1", "LEAF2", "LEAF2", "LEAF2", "LEAF3", "LEAF3", "LEAF3");
    }

    [Fact]
    public void ReadSheetRegion_RowEight_MapsPartRatedBlocksOrNullWhenOmitted()
    {
        // Arrange — row 8 present (default) vs absent (CreateWithoutPartRated)
        var withParts = Support.WorkbookBuilder.RatingWorkbookFactory.Create(Workbook("parts.xlsx"));
        var withoutParts = Support.WorkbookBuilder.RatingWorkbookFactory.CreateWithoutPartRated(
            Workbook("no parts.xlsx"));

        // Act
        var columnsWith = CreateRepository().ReadSheetRegion(withParts).Columns;
        var columnsWithout = CreateRepository().ReadSheetRegion(withoutParts).Columns;

        // Assert — C:E LEAF1, F:H LEAF2, I:K LEAF3 (empty cells inherit to the left)
        columnsWith.Select(c => c.PartRated).Should().Equal(
            "LEAF1", "LEAF1", "LEAF1",
            "LEAF2", "LEAF2", "LEAF2",
            "LEAF3", "LEAF3", "LEAF3");

        // Assert — no row 8 and no row 3 ⇒ both halves null, label degrades to the column letter
        columnsWithout.Should().AllSatisfy(c => c.PartRated.Should().BeNull());
        columnsWithout.Select(c => c.Label)
            .Should().Equal("C", "D", "E", "F", "G", "H", "I", "J", "K");
    }

    [Fact]
    public void ReadSheetRegion_Values_ZeroIsRealAndEmptyAndTextAreNull()
    {
        // Arrange — 0 and 7 pre-filled, plus a non-numeric text cell injected directly.
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

        // Act
        var region = CreateRepository().ReadSheetRegion(path);
        var row22 = region.Rows.Single(r => r.Row == 22);

        // Assert — ReadIntSafe semantics: int → value, 0 preserved, empty → null, text → null
        row22.Values.Should().HaveCount(9);
        row22.Values[0].Should().Be(0, "0 is a real rating value (empty ≠ 0 — Rule 2)");
        row22.Values[1].Should().Be(7);
        row22.Values[2].Should().BeNull("non-numeric text collapses to null — never an exception");
        row22.Values[3].Should().BeNull("an untouched cell stays null — never fabricated as 0");
    }

    [Fact]
    public void ReadSheetRegion_ValuesAreIndexAlignedWithColumns()
    {
        // Arrange — one value at the LAST trait ordinal (9 → column K) pins the far end.
        var path = Factory.Create(
            Workbook("invariant.xlsx"),
            prefilledCells: new Dictionary<(int plot, int subsample, int traitOrdinal), int>
            {
                [(102, 3, 9)] = 4,
            },
            writePestCodes: true);

        // Act
        var region = CreateRepository().ReadSheetRegion(path);

        // Assert — index = ordinal − 1 and column = TraitColumn(t) = t + 2, both directions
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
        // Arrange
        var path = Factory.Create(Workbook("row numbers.xlsx"));

        // Act
        var region = CreateRepository().ReadSheetRegion(path);

        // Assert — the row number keeps the Excel cross-reference (C22 ↔ row 22)
        foreach (var row in region.Rows)
        {
            row.Row.Should().Be(Factory.DataRowFor(row.Plot, row.Subsample),
                "row must equal the workbook row the key was parsed from");
        }
    }

    [Fact]
    public void ReadSheetRegion_MissingSheet_ThrowsInvalidDataException()
    {
        // Arrange — a valid workbook that is not ARM-shaped (no "Subsample Plot Data")
        var path = Workbook("wrong sheet.xlsx");
        using (var wb = new XLWorkbook())
        {
            wb.Worksheets.Add("Some Other Sheet").Cell(1, 1).Value = "not the ratings sheet";
            wb.SaveAs(path);
        }

        // Act & Assert — maps to 400 with this exact body (plan §3, frozen)
        var act = () => CreateRepository().ReadSheetRegion(path);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("Sheet 'Subsample Plot Data' was not found in workbook 'wrong sheet.xlsx'.");
    }

    [Fact]
    public void ReadSheetRegion_Metadata_ReadsFirstNonEmptyCellScanningCToK()
    {
        // Arrange — the factory's metadata rows all start at C (like the real template)…
        var path = Support.WorkbookBuilder.RatingWorkbookFactory.Create(
            Workbook("metadata.xlsx"), writeArmMetadata: true);

        // …plus a hand-built workbook where C..E are EMPTY and the value sits later,
        // so the first-non-empty scan itself is under test rather than "C always wins".
        var latePath = Workbook("late metadata.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add(Support.WorkbookBuilder.RatingWorkbookFactory.SheetName);
            ws.Cell(5, 6).Value = "Only at F";
            ws.Cell(6, 11).Value = "Only at K";
            wb.SaveAs(latePath);
        }

        // Act
        var metadata = CreateRepository().ReadSheetRegion(path).Metadata;
        var lateMetadata = CreateRepository().ReadSheetRegion(latePath).Metadata;

        // Assert — display-only strings, verbatim (16Jun2026 is not ISO; that is fine)
        metadata.Should().NotBeNull();
        metadata!.Crop.Should().Be("Winter wheat");
        metadata.RatingDate.Should().Be("16Jun2026");
        metadata.RatingType.Should().Be("PESSEV");
        metadata.Unit.Should().Be("%AREA");
        metadata.SampleSize.Should().Be("4");
        metadata.SubsampleCount.Should().Be("4");

        // Assert — scan skips empty C..K cells to the first non-empty one; absent rows → null
        lateMetadata.Should().NotBeNull();
        lateMetadata!.Crop.Should().Be("Only at F", "C..E are empty; F is the first non-empty cell");
        lateMetadata.RatingDate.Should().Be("Only at K");
        lateMetadata.RatingType.Should().BeNull("row 10 is untouched in this workbook");
    }
}
