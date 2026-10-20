using System.Reflection;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Repository.Repositories;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

public sealed class SingleCellWriteTests : IDisposable
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
    public void WriteSingleCell_EmptyCell_WritesValueAndExportsCopy()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();
        var before = File.ReadAllBytes(inputPath);

        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 2, value: 7, allowOverwrite: false);

        result.Written.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.SkippedReason.Should().BeNull();
        result.PriorValue.Should().BeNull("an empty cell has no prior value");
        NormalizeAddress(result.CellAddress).Should().Be("D22");
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(1);
        result.TraitOrdinal.Should().Be(2);
        result.Value.Should().Be(7);

        var copy = FindOutputCopies(outputDir).Should().ContainSingle("a written cell ⇒ a copy is exported").Subject;
        using (var wb = new XLWorkbook(copy))
        {
            var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
            sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(2))
                .GetDouble().Should().Be(7);
        }

        File.ReadAllBytes(inputPath).Should().Equal(before);
    }

    [Fact]
    public void WriteSingleCell_ZeroValue_IsWrittenNotTreatedAsEmpty()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 2, traitOrdinal: 1, value: 0, allowOverwrite: false);

        result.Written.Should().BeTrue();
        result.SkippedReason.Should().BeNull();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        wb.Worksheet(RatingWorkbookFactory.SheetName)
            .Cell(RatingWorkbookFactory.DataRowFor(101, 2), RatingWorkbookFactory.TraitColumnFor(1))
            .GetDouble().Should().Be(0);
    }

    [Fact]
    public void WriteSingleCell_MissingTextCell_TreatedAsWritable()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        SetCellText(inputPath, 101, 1, 1, "missing");
        var repo = CreateRepository();

        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 4, allowOverwrite: false);

        result.Written.Should().BeTrue();
        result.SkippedReason.Should().BeNull();
        result.PriorValue.Should().BeNull("text reads as null, never throws");

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        wb.Worksheet(RatingWorkbookFactory.SheetName)
            .Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(1))
            .GetDouble().Should().Be(4);
    }

    [Fact]
    public void WriteSingleCell_FilledCell_OverwriteOff_RefusesAndReportsPriorValue()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 3,
        });
        var repo = CreateRepository();

        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 8, allowOverwrite: false);

        result.Written.Should().BeFalse();
        result.Skipped.Should().BeTrue();
        result.SkippedReason.Should().Be(SingleCellWriteResult.CellAlreadyFilled);
        result.PriorValue.Should().Be(3);
        NormalizeAddress(result.CellAddress).Should().Be("C22");
        result.Value.Should().Be(8);
        result.OutputPath.Should().BeNull("a refused write exports no copy");

        FindOutputCopies(outputDir).Should().BeEmpty("a refused write must not export a copy");
    }

    [Fact]
    public void WriteSingleCell_FilledCell_ZeroValue_OverwriteOff_Refuses()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 0,
        });
        var repo = CreateRepository();

        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 5, allowOverwrite: false);

        result.Written.Should().BeFalse();
        result.SkippedReason.Should().Be(SingleCellWriteResult.CellAlreadyFilled);
        result.PriorValue.Should().Be(0, "0 is a present value, never treated as empty");
        FindOutputCopies(outputDir).Should().BeEmpty();
    }

    [Fact]
    public void WriteSingleCell_FilledTextCell_OverwriteOff_RefusesWithNullPrior()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        SetCellText(inputPath, 101, 1, 1, "n.a.");
        var repo = CreateRepository();

        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 6, allowOverwrite: false);

        result.Written.Should().BeFalse();
        result.SkippedReason.Should().Be(SingleCellWriteResult.CellAlreadyFilled);
        result.PriorValue.Should().BeNull("arbitrary text reads as null, never throws");
        FindOutputCopies(outputDir).Should().BeEmpty();
    }

    [Fact]
    public void WriteSingleCell_FilledCell_OverwriteOn_OverwritesAndKeepsPrior()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 3,
        });
        var repo = CreateRepository();

        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 9, allowOverwrite: true);

        result.Written.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.SkippedReason.Should().BeNull();
        result.PriorValue.Should().Be(3);
        NormalizeAddress(result.CellAddress).Should().Be("C22");

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        wb.Worksheet(RatingWorkbookFactory.SheetName)
            .Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(1))
            .GetDouble().Should().Be(9);
    }

    [Fact]
    public void WriteSingleCell_UnknownRow_SkippedRowNotFound_NoCopy()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 9, traitOrdinal: 1, value: 5, allowOverwrite: true);

        result.Written.Should().BeFalse();
        result.Skipped.Should().BeTrue();
        result.SkippedReason.Should().Be(SingleCellWriteResult.RowNotFound);
        result.CellAddress.Should().BeEmpty("no cell is addressed when the row is missing");
        result.PriorValue.Should().BeNull();
        result.OutputPath.Should().BeNull("a skipped write exports no copy");
        FindOutputCopies(outputDir).Should().BeEmpty("a skipped write must not export a copy");
    }

    [Fact]
    public void WriteSingleCell_UnknownPlot_SkippedRowNotFound()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 999, subsample: 1, traitOrdinal: 1, value: 5, allowOverwrite: true);

        result.Written.Should().BeFalse();
        result.SkippedReason.Should().Be(SingleCellWriteResult.RowNotFound);
        result.CellAddress.Should().BeEmpty();
        FindOutputCopies(outputDir).Should().BeEmpty();
    }

    [Fact]
    public void WriteSingleCell_TraitOrdinalOutsideRange_ThrowsInvalidData_NothingWritten()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var act = () => repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 0, value: 5, allowOverwrite: true);

        act.Should().Throw<InvalidDataException>().WithMessage("*traitOrdinal*1..9*");
        FindOutputCopies(outputDir).Should().BeEmpty();
    }

    [Fact]
    public void WriteSingleCell_TraitOrdinalTen_ThrowsInvalidData()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var act = () => repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 10, value: 5, allowOverwrite: true);

        act.Should().Throw<InvalidDataException>().WithMessage("*traitOrdinal*1..9*");
        FindOutputCopies(outputDir).Should().BeEmpty();
    }

    [Fact]
    public void WriteSingleCell_HighestTraitNine_WritesColumnK()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 9, value: 2, allowOverwrite: false);

        result.Written.Should().BeTrue();
        NormalizeAddress(result.CellAddress).Should().Be("K22");

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        wb.Worksheet(RatingWorkbookFactory.SheetName)
            .Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(9))
            .GetDouble().Should().Be(2);
    }

    [Fact]
    public void WriteSingleCell_OutputCopy_UsesSourceNameConventionAndLeavesInputPristine()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();
        var before = File.ReadAllBytes(inputPath);

        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 102, subsample: 2, traitOrdinal: 1, value: 4, allowOverwrite: false);

        result.Written.Should().BeTrue();
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
    public void WriteSingleCell_LongSourceFileName_OutputCopyStaysLengthSafe()
    {
        var (inputPath, outputDir) = CreateTempWorkspace(new string('q', 150) + ".xlsx");
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 7, allowOverwrite: false);

        result.Written.Should().BeTrue();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        Path.GetFileName(copy).Length.Should().BeLessThanOrEqualTo(255, "Linux NAME_MAX is 255 characters");

        var stem = Path.GetFileNameWithoutExtension(copy);
        stem[..stem.LastIndexOf("_out_", StringComparison.Ordinal)].Should().HaveLength(
            100, "the long source base is truncated to MaxOutputBaseLength before the stamp is appended");
        result.OutputPath.Should().Be(copy);
    }

    [Fact]
    public void WriteSingleCell_SourceNamedWithMarker_KeepsUserBaseNameIntact()
    {
        var (inputPath, outputDir) = CreateTempWorkspace("my_out_file.xlsx");
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 4, allowOverwrite: false);

        result.Written.Should().BeTrue();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        var stem = Path.GetFileNameWithoutExtension(copy);
        stem[..stem.LastIndexOf("_out_", StringComparison.Ordinal)].Should().Be(
            "my_out_file", "only a trailing generated chain is stripped — a user's own _out_ survives");
    }

    [Fact]
    public void WriteSingleCell_ConsecutiveWrites_ProduceTwoDistinctOutputCopies()
    {
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        var first = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 7, allowOverwrite: true);
        var second = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 9, allowOverwrite: true);

        first.Written.Should().BeTrue();
        second.Written.Should().BeTrue();

        var copies = FindOutputCopies(outputDir);
        copies.Should().HaveCount(2, "back-to-back writes must never overwrite each other's copy");
        first.OutputPath.Should().NotBeNull();
        second.OutputPath.Should().NotBeNull();
        first.OutputPath.Should().NotBe(second.OutputPath);
        File.Exists(first.OutputPath).Should().BeTrue();
        File.Exists(second.OutputPath).Should().BeTrue();
    }

    [Fact]
    public void WriteSingleCell_MissingWorkbook_ThrowsFileNotFound()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(), "PlottR02Tests", Guid.NewGuid().ToString(), "DoesNotExist.xlsx");
        var repo = CreateRepository();

        var act = () => repo.WriteSingleCell(
            missingPath,
            Path.Combine(Path.GetTempPath(), "PlottR02Tests", Guid.NewGuid().ToString()),
            plot: 101, subsample: 1, traitOrdinal: 1, value: 5, allowOverwrite: true);

        act.Should().Throw<FileNotFoundException>()
            .WithMessage("*Excel input workbook not found*")
            .Which.FileName.Should().Be(missingPath);
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
