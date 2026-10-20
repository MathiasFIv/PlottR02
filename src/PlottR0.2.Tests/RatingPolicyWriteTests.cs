using System.Reflection;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using PlottR0._2.Repository.Repositories;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

public sealed class RatingPolicyWriteTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void WritePreserving_EmptyCells_WritesAllRequestedSubsamplesAndCreatesOutput()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();
        var before = File.ReadAllBytes(inputPath);
        var requested = new HashSet<int> { 1, 2, 3, 4 };

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 5 }, requested,
            scanForFreeCells: false);

        result.PlotExists.Should().BeTrue();
        result.WrittenCells.Select(c => c.Subsample).Should().Equal(1, 2, 3, 4);
        result.WrittenCells.Should().AllSatisfy(c =>
        {
            c.Plot.Should().Be(101);
            c.TraitOrdinal.Should().Be(1);
            c.Value.Should().Be(5);
        });

        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("C22");
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().BeEmpty();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle("≥1 written cell ⇒ copy is exported").Subject;
        using (var wb = new XLWorkbook(copy))
        {
            var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
            foreach (var subsample in requested)
            {
                var cell = sheet.Cell(
                    RatingWorkbookFactory.DataRowFor(101, subsample),
                    RatingWorkbookFactory.TraitColumnFor(1));
                cell.IsEmpty().Should().BeFalse("empty cells must now hold the value");
                ((int)cell.GetDouble()).Should().Be(5);
            }
        }

        File.ReadAllBytes(inputPath).Should().Equal(before);
    }

    [Fact]
    public void WritePreserving_FilledMissingZeroAndTextCells_RespectsLockedOverwrite()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 3,
            [(101, 3, 1)] = 0,
        });
        SetCellText(inputPath, 101, 2, 1, "missing");
        SetCellText(inputPath, 101, 4, 1, "n.a.");
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 7 }, new HashSet<int> { 1, 2, 3, 4 },
            scanForFreeCells: false);

        result.PlotExists.Should().BeTrue();
        result.WrittenCells.Should().ContainSingle();
        result.WrittenCells[0].Subsample.Should().Be(2);
        result.WrittenCells[0].Value.Should().Be(7);

        result.SkippedPresentCells.Select(c => c.Subsample).Should().Equal(1, 3, 4);
        result.SkippedPresentCells.Single(c => c.Subsample == 1).Value.Should().Be(3);
        result.SkippedPresentCells.Single(c => c.Subsample == 3).Value.Should().Be(0, "0 is a present value, never overwritten");
        result.SkippedPresentCells.Single(c => c.Subsample == 4).Value.Should().BeNull("text cells must read as null, never throw");
        result.MissingSubsampleIndices.Should().BeEmpty();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using (var wb = new XLWorkbook(copy))
        {
            var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
            sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(1)).GetDouble().Should().Be(3);
            sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 2), RatingWorkbookFactory.TraitColumnFor(1)).GetDouble().Should().Be(7);
            sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 3), RatingWorkbookFactory.TraitColumnFor(1)).GetDouble().Should().Be(0);
            sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 4), RatingWorkbookFactory.TraitColumnFor(1)).GetValue<string>().Should().Be("n.a.");
        }
    }

    [Fact]
    public void WritePreserving_MissingSubsample_TrackedAndNeverWritten()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 2, values: new[] { 6 }, new HashSet<int> { 1, 9 },
            scanForFreeCells: false);

        result.PlotExists.Should().BeTrue();
        result.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(1);
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().Equal(9);

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(2)).GetDouble().Should().Be(6);
    }

    [Fact]
    public void WritePreserving_UnknownPlot_PlotExistsFalse_NoOutputCopy()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 999, traitOrdinal: 1, values: new[] { 5 }, new HashSet<int> { 1, 2 },
            scanForFreeCells: false);

        result.PlotExists.Should().BeFalse("no rows matched plot 999");
        result.WrittenCells.Should().BeEmpty();
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().Equal(1, 2);
        result.OutputPath.Should().BeNull("nothing was written, so there is no copy to report");

        Directory.Exists(outputDir).Should().BeFalse("0 written cells must not save a copy");
    }

    [Fact]
    public void WritePreserving_OutputCopy_UsesSourceNameConventionAndLeavesInputPristine()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();
        var before = File.ReadAllBytes(inputPath);

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 102, traitOrdinal: 1, values: new[] { 4 }, new HashSet<int> { 2 },
            scanForFreeCells: false);

        result.WrittenCells.Should().ContainSingle();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        copy.Should().NotBe(inputPath, "the input workbook must never be the write target");
        Path.GetDirectoryName(copy).Should().Be(Path.GetFullPath(outputDir));
        Path.GetFileName(copy).Should()
            .Match($"{Path.GetFileNameWithoutExtension(inputPath)}_out_*.xlsx");
        result.OutputPath.Should().Be(copy, "the written path must be threaded through the result");

        File.ReadAllBytes(inputPath).Should().Equal(before);
    }

    // NTFS caps one path component at 255 chars, so a source name longer than 300 chars can never
    // exist as a real file on Windows; the length guarantee is pinned on the naming helper itself.
    [Fact]
    public void OutputNaming_SourceNameOver300Chars_IsTruncatedToLengthSafeName()
    {
        var sourceName = new string('a', 300) + "_final report";

        var fileName = BuildOutputFileName(sourceName, new DateTime(2026, 1, 2, 3, 4, 5, 6));

        fileName.Should().EndWith("_out_20260102_030405_006.xlsx");
        fileName.Length.Should().BeLessThanOrEqualTo(255, "Linux NAME_MAX is 255 characters");

        var stem = Path.GetFileNameWithoutExtension(fileName);
        stem[..stem.LastIndexOf("_out_", StringComparison.Ordinal)].Length.Should().BeLessThanOrEqualTo(
            100, "the generated base is capped at MaxOutputBaseLength");
    }

    [Fact]
    public void WritePreserving_LongSourceFileName_OutputCopyStaysLengthSafe()
    {
        var (inputPath, outputDir) = CreateTempWorkspace(new string('q', 150) + ".xlsx");
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 102, traitOrdinal: 1, values: new[] { 4 }, new HashSet<int> { 2 },
            scanForFreeCells: false);

        result.WrittenCells.Should().ContainSingle();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        Path.GetFileName(copy).Length.Should().BeLessThanOrEqualTo(255, "Linux NAME_MAX is 255 characters");

        var stem = Path.GetFileNameWithoutExtension(copy);
        stem[..stem.LastIndexOf("_out_", StringComparison.Ordinal)].Should().HaveLength(
            100, "the long source base is truncated to MaxOutputBaseLength before the stamp is appended");
        result.OutputPath.Should().Be(copy);
    }

    [Fact]
    public void WritePreserving_SourceNamedWithMarker_KeepsUserBaseNameIntact()
    {
        var (inputPath, outputDir) = CreateTempWorkspace("my_out_file.xlsx");
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 102, traitOrdinal: 1, values: new[] { 4 }, new HashSet<int> { 2 },
            scanForFreeCells: false);

        result.WrittenCells.Should().ContainSingle();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        var stem = Path.GetFileNameWithoutExtension(copy);
        stem[..stem.LastIndexOf("_out_", StringComparison.Ordinal)].Should().Be(
            "my_out_file", "only a trailing generated chain is stripped — a user's own _out_ survives");
    }

    [Fact]
    public void WritePreserving_ConsecutiveWrites_ProduceTwoDistinctOutputCopies()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var first = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 4 }, new HashSet<int> { 1 },
            scanForFreeCells: false);
        var second = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 5 }, new HashSet<int> { 2 },
            scanForFreeCells: false);

        first.WrittenCells.Should().ContainSingle();
        second.WrittenCells.Should().ContainSingle();

        var copies = FindOutputCopies(outputDir);
        copies.Should().HaveCount(2, "back-to-back writes must never overwrite each other's copy");
        first.OutputPath.Should().NotBeNull();
        second.OutputPath.Should().NotBeNull();
        first.OutputPath.Should().NotBe(second.OutputPath);
        File.Exists(first.OutputPath).Should().BeTrue();
        File.Exists(second.OutputPath).Should().BeTrue();
    }

    [Fact]
    public void WritePreserving_MultiValue_HorizontalFillAcrossTraitColumns()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 3, 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: false);

        result.PlotExists.Should().BeTrue();
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => c.Subsample).Should().AllBeEquivalentTo(1);
        result.WrittenCells.Select(c => c.TraitOrdinal).Should().Equal(1, 2, 3);
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C22", "D22", "E22");
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().BeEmpty();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(1)).GetDouble().Should().Be(3);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(2)).GetDouble().Should().Be(6);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(3)).GetDouble().Should().Be(1);
    }

    [Fact]
    public void WritePreserving_MultiValue_IdempotentSecondWrite_SkipsAll()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var first = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 3, 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: false);
        first.WrittenCells.Should().HaveCount(3);

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        var second = repo.WritePlotColumnPreserving(
            copy, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 3, 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: false);

        second.WrittenCells.Should().BeEmpty();
        second.SkippedPresentCells.Select(c => c.TraitOrdinal).Should().Equal(1, 2, 3);
        second.SkippedPresentCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        second.MissingSubsampleIndices.Should().BeEmpty();
        second.OutputPath.Should().BeNull("an all-skipped write exports no new copy");

        FindOutputCopies(outputDir).Should().ContainSingle();
    }

    [Fact]
    public void WritePreserving_MultiValue_PartialFill_SkipsFirstWritesRest()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 5,
        });
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 3, 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: false);

        result.WrittenCells.Should().HaveCount(2);
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("D22", "E22");
        result.WrittenCells.Select(c => c.Value).Should().Equal(6, 1);
        result.SkippedPresentCells.Should().ContainSingle();
        result.SkippedPresentCells[0].TraitOrdinal.Should().Be(1);
        result.SkippedPresentCells[0].Value.Should().Be(5);

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(1)).GetDouble().Should().Be(5, "existing value must survive");
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(2)).GetDouble().Should().Be(6);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(3)).GetDouble().Should().Be(1);
    }

    [Fact]
    public void WritePreserving_MultiValue_BeyondTraitNine_IgnoredValuesAreCounted()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 9, values: new[] { 3, 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: false);

        result.WrittenCells.Should().ContainSingle();
        result.WrittenCells[0].TraitOrdinal.Should().Be(9);
        result.WrittenCells[0].Value.Should().Be(3);
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("K22");
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().BeEmpty();
        result.OutOfTraitRangeValueCount.Should().Be(2);

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(9)).GetDouble().Should().Be(3);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), 12).IsEmpty().Should().BeTrue("trait 10 (column L) is outside the rating region");
    }

    [Fact]
    public void WritePreserving_MissingWorkbook_ThrowsFileNotFound()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(), "PlottR02Tests", Guid.NewGuid().ToString(), "DoesNotExist.xlsx");
        var repo = CreateRepository();

        var act = () => repo.WritePlotColumnPreserving(
            missingPath,
            Path.Combine(Path.GetTempPath(), "PlottR02Tests", Guid.NewGuid().ToString()),
            plot: 101, traitOrdinal: 1, values: new[] { 5 }, new HashSet<int> { 1 },
            scanForFreeCells: false);

        act.Should().Throw<FileNotFoundException>()
            .WithMessage("*Excel input workbook not found*")
            .Which.FileName.Should().Be(missingPath);
    }

    [Fact]
    public void WritePreserving_TraitOrdinalOutsideRange_ThrowsInvalidData()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var act = () => repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 0, values: new[] { 5 }, new HashSet<int> { 1 },
            scanForFreeCells: false);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*traitOrdinal*1..9*");

        FindOutputCopies(outputDir).Should().BeEmpty();
    }

    [Fact]
    public void WritePreserving_ScanForFreeCells_AppendsToNextCategoryBlock()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var first = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 3, 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: true);
        first.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C22", "D22", "E22");
        first.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        first.OutOfTraitRangeValueCount.Should().Be(0);

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        var second = repo.WritePlotColumnPreserving(
            copy, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 4, 2, 6 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        second.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("F22", "G22", "H22");
        second.WrittenCells.Select(c => c.Value).Should().Equal(4, 2, 6);
        second.OutOfTraitRangeValueCount.Should().Be(0);
        second.SkippedPresentCells.Should().BeEmpty("append mode never reports occupies as skipped");
    }

    [Fact]
    public void WritePreserving_ScanForFreeCells_SingleValueAppendsToFirstFreeCell()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 5,
        });
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 9 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        result.WrittenCells.Should().ContainSingle();
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("D22");
        result.WrittenCells[0].Value.Should().Be(9);
        result.SkippedPresentCells.Should().BeEmpty();
        result.OutOfTraitRangeValueCount.Should().Be(0);

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(1)).GetDouble().Should().Be(5);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(2)).GetDouble().Should().Be(9);
    }

    [Fact]
    public void WritePreserving_ScanForFreeCells_ScansPastHolesWithinBlock()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 2)] = 4,
        });
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 4, 2 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C22", "E22");
        result.WrittenCells.Select(c => c.Value).Should().Equal(4, 2);
        result.OutOfTraitRangeValueCount.Should().Be(0);
        result.SkippedPresentCells.Should().BeEmpty();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(2)).GetDouble().Should().Be(4, "the occupied D22 hole must survive");
    }

    [Fact]
    public void WritePreserving_ScanForFreeCells_BlockBoundaryIsStrict_ExtraValueFlagged()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 8,
            [(101, 1, 3)] = 8,
        });
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 4, 2 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        result.WrittenCells.Should().ContainSingle();
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("D22");
        result.WrittenCells[0].Value.Should().Be(4);
        result.OutOfTraitRangeValueCount.Should().Be(1);
        result.SkippedPresentCells.Should().BeEmpty();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(4))
            .IsEmpty().Should().BeTrue("an utterance never spans category blocks — F22 stays empty");
    }

    [Fact]
    public void WritePreserving_ScanForFreeCells_TraitOrdinalSetsRegionStart()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 2, values: new[] { 3, 6 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("D22", "E22");
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6);
        result.OutOfTraitRangeValueCount.Should().Be(0);
        result.SkippedPresentCells.Should().BeEmpty();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(1))
            .IsEmpty().Should().BeTrue("traitOrdinal 2 leaves C untouched");
    }

    [Fact]
    public void WritePreserving_ScanForFreeCells_SameCategoryBoundaryEnforced()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 8,
            [(101, 1, 2)] = 8,
        });
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 8, 9, 7 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        result.WrittenCells.Should().ContainSingle();
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("E22");
        result.WrittenCells[0].Value.Should().Be(8);
        result.OutOfTraitRangeValueCount.Should().Be(2);
        result.SkippedPresentCells.Should().BeEmpty();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
        foreach (var trait in new[] { 4, 5, 6 })
        {
            sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(trait))
                .IsEmpty().Should().BeTrue("an utterance never spans category blocks — the strict boundary holds");
        }
    }

    [Fact]
    public void WritePreserving_ScanForFreeCells_FullRegionOverflowFlags()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, Enumerable.Range(1, 9).ToDictionary(t => (101, 1, t), t => t));
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 5 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        result.WrittenCells.Should().BeEmpty();
        result.SkippedPresentCells.Should().BeEmpty();
        result.OutOfTraitRangeValueCount.Should().Be(1);
        FindOutputCopies(outputDir).Should().BeEmpty("0 written cells ⇒ no copy is saved");
    }

    [Fact]
    public void WritePreserving_ScanForFreeCells_UnlabelledRegionActsAsOneBlock()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.CreateWithoutPartRated(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 6,
        });
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("D22", "E22");
        result.WrittenCells.Select(c => c.Value).Should().Equal(6, 1);
        result.OutOfTraitRangeValueCount.Should().Be(0);
        result.SkippedPresentCells.Should().BeEmpty();
    }

    [Fact]
    public void WritePreserving_ScanForFreeCells_LeadingEmptyLabelInheritsNextCategory()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        using (var wb = new XLWorkbook(inputPath))
        {
            wb.Worksheet(RatingWorkbookFactory.SheetName)
                .Cell(RatingWorkbookFactory.PartRatedHeaderRow, 3)
                .Clear();
            wb.Save();
        }
        var repo = CreateRepository();

        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 3, 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C22", "D22", "E22");
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        result.OutOfTraitRangeValueCount.Should().Be(0);
        result.SkippedPresentCells.Should().BeEmpty();
    }

    private (string inputPath, string outputDir) CreateTempWorkspace(string fileName = "TestWorkbook.xlsx")
    {
        var guid = Guid.NewGuid();
        var tempDir = Path.Combine(Path.GetTempPath(), "PlottR02Tests", guid.ToString());
        var inputPath = Path.Combine(tempDir, fileName);
        var outputDir = Path.Combine(tempDir, "Output");
        _tempDirs.Add(tempDir);
        return (inputPath, outputDir);
    }

    private static ClosedXmlRatingRepository CreateRepository() =>
        new(NullLogger<ClosedXmlRatingRepository>.Instance);

    // ObjectKeys is internal to the Repository assembly, so its naming helpers are reached the
    // same way ObjectKeysTests reaches them.
    private static string BuildOutputFileName(string sourceNameWithoutExtension, DateTime stamp)
        => (string)typeof(TrialWorkbookStore).Assembly
            .GetType("PlottR0._2.Repository.Storage.ObjectKeys", throwOnError: true)!
            .GetMethod("BuildOutputFileName", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [sourceNameWithoutExtension, stamp])!;

    private static string[] FindOutputCopies(string outputDir)
        => Directory.Exists(outputDir) ? Directory.GetFiles(outputDir, "*_out_*.xlsx") : [];

    private static void SetCellText(string workbookPath, int plot, int subsample, int traitOrdinal, string text)
    {
        using var wb = new XLWorkbook(workbookPath);
        var ws = wb.Worksheet(RatingWorkbookFactory.SheetName);
        ws.Cell(RatingWorkbookFactory.DataRowFor(plot, subsample), RatingWorkbookFactory.TraitColumnFor(traitOrdinal))
            .Value = text;
        wb.Save();
    }

    private static string NormalizeAddress(string address) => address.Replace("$", "");
}