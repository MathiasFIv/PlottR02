using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Repository.Repositories;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

/// <summary>
/// Pure Excel tests for <see cref="ClosedXmlRatingRepository.WriteSingleCell"/> — the inline
/// "edit a sheet cell" write that addresses exactly ONE <c>(plot, subsample, traitOrdinal)</c>
/// cell under the locked-overwrite policy: an empty/"missing" cell is written (0 is a legal
/// value), an already-filled cell is REFUSED unless <c>allowOverwrite</c> is set, a missing row
/// is skipped, and a written cell exports a fresh <c>_out_</c> copy while never mutating the
/// input. These tests run WITHOUT Postgres/Docker — they build workbooks with
/// <see cref="RatingWorkbookFactory"/> and exercise the repository directly.
/// </summary>
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
                // Best-effort cleanup — temp folders may still be locked.
            }
        }
    }

    [Fact]
    public void WriteSingleCell_EmptyCell_WritesValueAndExportsCopy()
    {
        // Arrange — pristine workbook: every trait cell is empty.
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();
        var before = File.ReadAllBytes(inputPath);

        // Act — write 7 into plot 101 subsample 1 trait 2 (D22)
        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 2, value: 7, allowOverwrite: false);

        // Assert — written, no skip, no prior value, address reported
        result.Written.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.SkippedReason.Should().BeNull();
        result.PriorValue.Should().BeNull("an empty cell has no prior value");
        NormalizeAddress(result.CellAddress).Should().Be("D22");
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(1);
        result.TraitOrdinal.Should().Be(2);
        result.Value.Should().Be(7);

        // Assert — copy exported and holds the value
        var copy = FindOutputCopies(outputDir).Should().ContainSingle("a written cell ⇒ a copy is exported").Subject;
        using (var wb = new XLWorkbook(copy))
        {
            var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
            sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(2))
                .GetDouble().Should().Be(7);
        }

        // Assert — input workbook never mutated
        File.ReadAllBytes(inputPath).Should().Equal(before);
    }

    [Fact]
    public void WriteSingleCell_ZeroValue_IsWrittenNotTreatedAsEmpty()
    {
        // Arrange — pristine workbook; 0 is a legal rating, not "absent".
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act
        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 2, traitOrdinal: 1, value: 0, allowOverwrite: false);

        // Assert
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
        // Arrange — trait 1 of plot 101/subsample 1 holds the literal text "missing".
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        SetCellText(inputPath, 101, 1, 1, "missing");
        var repo = CreateRepository();

        // Act
        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 4, allowOverwrite: false);

        // Assert — "missing" is writable (it means the same as empty)
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
        // Arrange — trait 1 of plot 101/subsample 1 holds 3.
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 3,
        });
        var repo = CreateRepository();

        // Act — overwrite not requested
        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 8, allowOverwrite: false);

        // Assert — refused: not written, skipped with the filled-cell reason, prior value carried
        result.Written.Should().BeFalse();
        result.Skipped.Should().BeTrue();
        result.SkippedReason.Should().Be(SingleCellWriteResult.CellAlreadyFilled);
        result.PriorValue.Should().Be(3);
        NormalizeAddress(result.CellAddress).Should().Be("C22");
        result.Value.Should().Be(8);

        // Assert — NO copy is exported for a refused write (input not even opened for writing)
        FindOutputCopies(outputDir).Should().BeEmpty("a refused write must not export a copy");
    }

    [Fact]
    public void WriteSingleCell_FilledCell_ZeroValue_OverwriteOff_Refuses()
    {
        // Arrange — trait 1 of plot 101/subsample 1 holds a legitimate 0.
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 0,
        });
        var repo = CreateRepository();

        // Act
        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 5, allowOverwrite: false);

        // Assert — 0 is a PRESENT value: refused, prior value reported as 0 (not null)
        result.Written.Should().BeFalse();
        result.SkippedReason.Should().Be(SingleCellWriteResult.CellAlreadyFilled);
        result.PriorValue.Should().Be(0, "0 is a present value, never treated as empty");
        FindOutputCopies(outputDir).Should().BeEmpty();
    }

    [Fact]
    public void WriteSingleCell_FilledTextCell_OverwriteOff_RefusesWithNullPrior()
    {
        // Arrange — trait 1 of plot 101/subsample 1 holds the legacy text "n.a.".
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        SetCellText(inputPath, 101, 1, 1, "n.a.");
        var repo = CreateRepository();

        // Act
        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 6, allowOverwrite: false);

        // Assert — non-"missing" text is a present value: refused, prior value reads as null
        result.Written.Should().BeFalse();
        result.SkippedReason.Should().Be(SingleCellWriteResult.CellAlreadyFilled);
        result.PriorValue.Should().BeNull("arbitrary text reads as null, never throws");
        FindOutputCopies(outputDir).Should().BeEmpty();
    }

    [Fact]
    public void WriteSingleCell_FilledCell_OverwriteOn_OverwritesAndKeepsPrior()
    {
        // Arrange — trait 1 of plot 101/subsample 1 holds 3.
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 3,
        });
        var repo = CreateRepository();

        // Act — overwrite requested
        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 1, value: 9, allowOverwrite: true);

        // Assert — written; prior value retained for the audit/undo
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
        // Arrange — default layout only has subsamples 1..4 per plot.
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act — subsample 9 of plot 101 does not exist
        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 9, traitOrdinal: 1, value: 5, allowOverwrite: true);

        // Assert — skipped as row_not_found with no address; nothing written or exported
        result.Written.Should().BeFalse();
        result.Skipped.Should().BeTrue();
        result.SkippedReason.Should().Be(SingleCellWriteResult.RowNotFound);
        result.CellAddress.Should().BeEmpty("no cell is addressed when the row is missing");
        result.PriorValue.Should().BeNull();
        FindOutputCopies(outputDir).Should().BeEmpty("a skipped write must not export a copy");
    }

    [Fact]
    public void WriteSingleCell_UnknownPlot_SkippedRowNotFound()
    {
        // Arrange
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act — plot 999 has no rows in column B
        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 999, subsample: 1, traitOrdinal: 1, value: 5, allowOverwrite: true);

        // Assert
        result.Written.Should().BeFalse();
        result.SkippedReason.Should().Be(SingleCellWriteResult.RowNotFound);
        result.CellAddress.Should().BeEmpty();
        FindOutputCopies(outputDir).Should().BeEmpty();
    }

    [Fact]
    public void WriteSingleCell_TraitOrdinalOutsideRange_ThrowsInvalidData_NothingWritten()
    {
        // Arrange — traitOrdinal 0 would address column B, the row-key column.
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act & Assert — defense in depth before the workbook is even opened
        var act = () => repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 0, value: 5, allowOverwrite: true);

        act.Should().Throw<InvalidDataException>().WithMessage("*traitOrdinal*1..9*");
        FindOutputCopies(outputDir).Should().BeEmpty();
    }

    [Fact]
    public void WriteSingleCell_TraitOrdinalTen_ThrowsInvalidData()
    {
        // Arrange — traitOrdinal 10 would address column L, outside the rating region.
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act & Assert
        var act = () => repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 10, value: 5, allowOverwrite: true);

        act.Should().Throw<InvalidDataException>().WithMessage("*traitOrdinal*1..9*");
        FindOutputCopies(outputDir).Should().BeEmpty();
    }

    [Fact]
    public void WriteSingleCell_HighestTraitNine_WritesColumnK()
    {
        // Arrange — traitOrdinal 9 addresses column K.
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act
        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 101, subsample: 1, traitOrdinal: 9, value: 2, allowOverwrite: false);

        // Assert
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
        // Arrange
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();
        var before = File.ReadAllBytes(inputPath);

        // Act
        var result = repo.WriteSingleCell(
            inputPath, outputDir, plot: 102, subsample: 2, traitOrdinal: 1, value: 4, allowOverwrite: false);

        // Assert — copy follows "{sourceName}_out_{yyyyMMdd_HHmmss}.xlsx" inside outputDir
        result.Written.Should().BeTrue();
        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        copy.Should().NotBe(inputPath, "the input workbook must never be the write target");
        Path.GetDirectoryName(copy).Should().Be(Path.GetFullPath(outputDir));
        Path.GetFileName(copy).Should()
            .Match($"{Path.GetFileNameWithoutExtension(inputPath)}_out_*.xlsx");

        // Assert — input untouched
        File.ReadAllBytes(inputPath).Should().Equal(before);
    }

    [Fact]
    public void WriteSingleCell_MissingWorkbook_ThrowsFileNotFound()
    {
        // Arrange — an absolute path that does not exist
        var missingPath = Path.Combine(
            Path.GetTempPath(), "PlottR02Tests", Guid.NewGuid().ToString(), "DoesNotExist.xlsx");
        var repo = CreateRepository();

        // Act & Assert
        var act = () => repo.WriteSingleCell(
            missingPath,
            Path.Combine(Path.GetTempPath(), "PlottR02Tests", Guid.NewGuid().ToString()),
            plot: 101, subsample: 1, traitOrdinal: 1, value: 5, allowOverwrite: true);

        act.Should().Throw<FileNotFoundException>()
            .WithMessage("*Excel input workbook not found*")
            .Which.FileName.Should().Be(missingPath);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (string inputPath, string outputDir) CreateTempWorkspace()
    {
        var guid = Guid.NewGuid();
        var tempDir = Path.Combine(Path.GetTempPath(), "PlottR02Tests", guid.ToString());
        var inputPath = Path.Combine(tempDir, "TestWorkbook.xlsx");
        var outputDir = Path.Combine(tempDir, "Output");
        _tempDirs.Add(tempDir);
        return (inputPath, outputDir);
    }

    private static ClosedXmlRatingRepository CreateRepository() =>
        new(NullLogger<ClosedXmlRatingRepository>.Instance);

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
