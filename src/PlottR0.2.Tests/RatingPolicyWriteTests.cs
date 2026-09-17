using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlottR0._2.Repository;
using PlottR0._2.Repository.Repositories;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

/// <summary>
/// Pure Excel tests for <see cref="ClosedXmlRatingRepository.WritePlotColumnPreserving"/> —
/// the locked-overwrite policy write used by the voice sync pipeline. These tests run WITHOUT
/// Postgres/Docker: they build workbooks with <see cref="RatingWorkbookFactory"/> in temp
/// directories and exercise the repository directly with explicit paths (no appsettings /
/// <c>ExcelWorkbookOptions</c> defaults involved). The existing
/// <see cref="ClosedXmlRatingRepository.WritePlotColumn"/> behaviour is covered by
/// <see cref="RatingServiceTests"/> and is intentionally untouched here.
/// </summary>
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
                // Best-effort cleanup — temp folders may still be locked.
            }
        }
    }

    [Fact]
    public void WritePreserving_EmptyCells_WritesAllRequestedSubsamplesAndCreatesOutput()
    {
        // Arrange — pristine workbook: every trait cell is empty.
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();
        var before = File.ReadAllBytes(inputPath);
        var requested = new HashSet<int> { 1, 2, 3, 4 };

        // Act
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, value: 5, requested);

        // Assert — policy result: all requested cells written, none skipped, plot found
        result.PlotExists.Should().BeTrue();
        result.WrittenCells.Select(c => c.Subsample).Should().Equal(1, 2, 3, 4);
        result.WrittenCells.Should().AllSatisfy(c =>
        {
            c.Plot.Should().Be(101);
            c.TraitOrdinal.Should().Be(1);
            c.Value.Should().Be(5);
        });
        // Ascending iteration ⇒ first written cell lands at C22 (plot 101, subsample 1, trait 1)
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("C22");
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().BeEmpty();

        // Assert — output copy exists in the output directory and holds the value everywhere
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

        // Assert — input workbook is never mutated: byte-identical before/after
        File.ReadAllBytes(inputPath).Should().Equal(before);
    }

    [Fact]
    public void WritePreserving_FilledMissingZeroAndTextCells_RespectsLockedOverwrite()
    {
        // Arrange — plot 101, trait 1: [1]=3 (number), [2]="missing", [3]=0, [4]="n.a." (text)
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 3,
            [(101, 3, 1)] = 0,
        });
        SetCellText(inputPath, 101, 2, 1, "missing");
        SetCellText(inputPath, 101, 4, 1, "n.a.");
        var repo = CreateRepository();

        // Act — value 7 offered to every subsample
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, value: 7, new HashSet<int> { 1, 2, 3, 4 });

        // Assert — only the "missing" cell is written
        result.PlotExists.Should().BeTrue();
        result.WrittenCells.Should().ContainSingle();
        result.WrittenCells[0].Subsample.Should().Be(2);
        result.WrittenCells[0].Value.Should().Be(7);

        // Assert — every already-present cell is skipped with its CURRENT value reported
        result.SkippedPresentCells.Select(c => c.Subsample).Should().Equal(1, 3, 4);
        result.SkippedPresentCells.Single(c => c.Subsample == 1).Value.Should().Be(3);
        result.SkippedPresentCells.Single(c => c.Subsample == 3).Value.Should().Be(0, "0 is a present value, never overwritten");
        result.SkippedPresentCells.Single(c => c.Subsample == 4).Value.Should().BeNull("text cells must read as null, never throw");
        result.MissingSubsampleIndices.Should().BeEmpty();

        // Assert — persisted copy honours the policy: 3 stays 3, 0 stays 0, "missing" → 7,
        // legacy text "n.a." is preserved as-is
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
        // Arrange — default layout only has subsamples 1..4 per plot
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act — subsample 1 exists, subsample 9 does not
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 2, value: 6, new HashSet<int> { 1, 9 });

        // Assert
        result.PlotExists.Should().BeTrue();
        result.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(1);
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().Equal(9);

        // Output copy exists (one cell written) and holds only the matched subsample
        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
        sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(2)).GetDouble().Should().Be(6);
    }

    [Fact]
    public void WritePreserving_UnknownPlot_PlotExistsFalse_NoOutputCopy()
    {
        // Arrange
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act — plot 999 has no composite-key rows anywhere in column B
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 999, traitOrdinal: 1, value: 5, new HashSet<int> { 1, 2 });

        // Assert
        result.PlotExists.Should().BeFalse("no rows matched plot 999");
        result.WrittenCells.Should().BeEmpty();
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().Equal(1, 2);

        // 0 written cells ⇒ no copy is saved — the output directory is not even created
        Directory.Exists(outputDir).Should().BeFalse("0 written cells must not save a copy");
    }

    [Fact]
    public void WritePreserving_OutputCopy_UsesSourceNameConventionAndLeavesInputPristine()
    {
        // Arrange
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();
        var before = File.ReadAllBytes(inputPath);

        // Act
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 102, traitOrdinal: 1, value: 4, new HashSet<int> { 2 });

        // Assert — write happened
        result.WrittenCells.Should().ContainSingle();

        // Assert — copy follows "{sourceName}_out_{yyyyMMdd_HHmmss}.xlsx" inside outputDir
        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        copy.Should().NotBe(inputPath, "the input workbook must never be the write target");
        Path.GetDirectoryName(copy).Should().Be(Path.GetFullPath(outputDir));
        Path.GetFileName(copy).Should()
            .Match($"{Path.GetFileNameWithoutExtension(inputPath)}_out_*.xlsx");

        // Assert — input untouched: byte-identical before/after
        File.ReadAllBytes(inputPath).Should().Equal(before);
    }

    [Fact]
    public void WritePreserving_MissingWorkbook_ThrowsFileNotFound()
    {
        // Arrange — an absolute path that does not exist
        var missingPath = Path.Combine(
            Path.GetTempPath(), "PlottR02Tests", Guid.NewGuid().ToString(), "DoesNotExist.xlsx");
        var repo = CreateRepository();

        // Act & Assert — FileNotFoundException, same not-found style as the repository's other open
        var act = () => repo.WritePlotColumnPreserving(
            missingPath,
            Path.Combine(Path.GetTempPath(), "PlottR02Tests", Guid.NewGuid().ToString()),
            plot: 101, traitOrdinal: 1, value: 5, new HashSet<int> { 1 });

        act.Should().Throw<FileNotFoundException>()
            .WithMessage("*Excel input workbook not found*")
            .Which.FileName.Should().Be(missingPath);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a unique temp folder with input and output subdirectories.
    /// The folder is tracked for cleanup in <see cref="Dispose"/>.
    /// </summary>
    private (string inputPath, string outputDir) CreateTempWorkspace()
    {
        var guid = Guid.NewGuid();
        var tempDir = Path.Combine(Path.GetTempPath(), "PlottR02Tests", guid.ToString());
        var inputPath = Path.Combine(tempDir, "TestWorkbook.xlsx");
        var outputDir = Path.Combine(tempDir, "Output");
        _tempDirs.Add(tempDir);
        return (inputPath, outputDir);
    }

    /// <summary>
    /// The policy write takes explicit paths, so a bare repository with empty
    /// <see cref="ExcelWorkbookOptions"/> suffices (like <see cref="RatingRepositoryContractTests"/>).
    /// </summary>
    private static ClosedXmlRatingRepository CreateRepository() =>
        new(Options.Create(new ExcelWorkbookOptions()), NullLogger<ClosedXmlRatingRepository>.Instance);

    /// <summary>
    /// Returns the output copies in the given directory, or an empty array when the directory
    /// does not exist yet — callers must never assume a prior write created it.
    /// </summary>
    private static string[] FindOutputCopies(string outputDir)
        => Directory.Exists(outputDir) ? Directory.GetFiles(outputDir, "*_out_*.xlsx") : [];

    /// <summary>Sets a TEXT value on a trait cell of an existing workbook.</summary>
    private static void SetCellText(string workbookPath, int plot, int subsample, int traitOrdinal, string text)
    {
        using var wb = new XLWorkbook(workbookPath);
        var ws = wb.Worksheet(RatingWorkbookFactory.SheetName);
        ws.Cell(RatingWorkbookFactory.DataRowFor(plot, subsample), RatingWorkbookFactory.TraitColumnFor(traitOrdinal))
            .Value = text;
        wb.Save();
    }

    /// <summary>Strips <c>$</c> signs from a cell address for flexible assertion matching.</summary>
    private static string NormalizeAddress(string address) => address.Replace("$", "");
}