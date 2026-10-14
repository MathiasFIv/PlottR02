using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using PlottR0._2.Repository.Repositories;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

/// <summary>
/// Pure Excel tests for <see cref="ClosedXmlRatingRepository.WritePlotColumnPreserving"/> —
/// the locked-overwrite policy write used by the voice sync pipeline, in BOTH modes: the
/// fixed-column horizontal fill (<c>scanForFreeCells: false</c>) and the category-block
/// append (<c>scanForFreeCells: true</c>) that fills the next free cells inside the Part
/// Rated block derived from row 8 — one utterance never spans category blocks; values that
/// cannot fit the current block are dropped and counted. These tests run WITHOUT
/// Postgres/Docker: they build
/// workbooks with <see cref="RatingWorkbookFactory"/> in temp directories and exercise the
/// repository directly with explicit paths (no appsettings defaults involved).
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
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 5 }, requested,
            scanForFreeCells: false);

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
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 7 }, new HashSet<int> { 1, 2, 3, 4 },
            scanForFreeCells: false);

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
            inputPath, outputDir, plot: 101, traitOrdinal: 2, values: new[] { 6 }, new HashSet<int> { 1, 9 },
            scanForFreeCells: false);

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
            inputPath, outputDir, plot: 999, traitOrdinal: 1, values: new[] { 5 }, new HashSet<int> { 1, 2 },
            scanForFreeCells: false);

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
            inputPath, outputDir, plot: 102, traitOrdinal: 1, values: new[] { 4 }, new HashSet<int> { 2 },
            scanForFreeCells: false);

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
    public void WritePreserving_MultiValue_HorizontalFillAcrossTraitColumns()
    {
        // Arrange — pristine workbook; subsample 1 of plot 101 sits at row 22 (keys parsed from column B)
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act — values [3,6,1] fill trait columns left-to-right starting at traitOrdinal 1
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 3, 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: false);

        // Assert — three written cells land at C22, D22, E22 of the plot 101 subsample 1 row
        result.PlotExists.Should().BeTrue();
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => c.Subsample).Should().AllBeEquivalentTo(1);
        result.WrittenCells.Select(c => c.TraitOrdinal).Should().Equal(1, 2, 3);
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C22", "D22", "E22");
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().BeEmpty();

        // Assert — the exported copy physically holds the three values in the row
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
        // Arrange — the input workbook is never mutated, so a second write chains the FIRST
        // output copy as its input exactly like successive production syncs.
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act — first write fills C22/D22/E22
        var first = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 3, 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: false);
        first.WrittenCells.Should().HaveCount(3);

        // Second write against the first output copy: every cell already holds the value.
        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        var second = repo.WritePlotColumnPreserving(
            copy, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 3, 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: false);

        // Assert — nothing written a second time; all three reported present with current content
        second.WrittenCells.Should().BeEmpty();
        second.SkippedPresentCells.Select(c => c.TraitOrdinal).Should().Equal(1, 2, 3);
        second.SkippedPresentCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        second.MissingSubsampleIndices.Should().BeEmpty();

        // 0 written cells on the second pass ⇒ no additional copy is exported
        FindOutputCopies(outputDir).Should().ContainSingle();
    }

    [Fact]
    public void WritePreserving_MultiValue_PartialFill_SkipsFirstWritesRest()
    {
        // Arrange — trait 1 of plot 101/subsample 1 already holds 5; traits 2 and 3 are empty
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 5,
        });
        var repo = CreateRepository();

        // Act — values [3,6,1] offered to the row
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 3, 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: false);

        // Assert — C is skipped with its current 5; D and E are written
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
        // Arrange — pristine workbook
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act — traitOrdinal 9 (column K): value 3 lands in K, values 6 and 1 would fall
        // beyond trait 9 (column L / M are outside the C22:K245 region) — the drops must be
        // surfaced via OutOfTraitRangeValueCount so the sync can flag the recording.
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 9, values: new[] { 3, 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: false);

        // Assert — only the first value is written; nothing is reported skipped; two were dropped
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
        // Arrange — an absolute path that does not exist
        var missingPath = Path.Combine(
            Path.GetTempPath(), "PlottR02Tests", Guid.NewGuid().ToString(), "DoesNotExist.xlsx");
        var repo = CreateRepository();

        // Act & Assert — FileNotFoundException, same not-found style as the repository's other open
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
        // Arrange — pristine workbook; traitOrdinal 0 would address column B, the row-key column
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act & Assert — defense in depth: no caller may aim at a non-rating column,
        // and even a valid workbook path must not be opened for such a call
        var act = () => repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 0, values: new[] { 5 }, new HashSet<int> { 1 },
            scanForFreeCells: false);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*traitOrdinal*1..9*");

        // A pristine workbook must survive an invalid traitOrdinal call untouched
        FindOutputCopies(outputDir).Should().BeEmpty();
    }

    // ── Category-block append (scanForFreeCells) ────────────────────────────

    [Fact]
    public void WritePreserving_ScanForFreeCells_AppendsToNextCategoryBlock()
    {
        // Arrange — pristine workbook with the row-8 Part Rated metadata (C:E LEAF1, F:H LEAF2, I:K LEAF3)
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act — the first utterance fills LEAF1 (C22:E22); the second CHAINS the first
        // output copy as its input, exactly like successive production syncs.
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

        // Assert — LEAF2 (F22:G22:H22) is the next free category block; LEAF1 stays untouched
        second.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("F22", "G22", "H22");
        second.WrittenCells.Select(c => c.Value).Should().Equal(4, 2, 6);
        second.OutOfTraitRangeValueCount.Should().Be(0);
        second.SkippedPresentCells.Should().BeEmpty("append mode never reports occupies as skipped");
    }

    [Fact]
    public void WritePreserving_ScanForFreeCells_SingleValueAppendsToFirstFreeCell()
    {
        // Arrange — C22 already holds 5; the row's first free cell is D22
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 5,
        });
        var repo = CreateRepository();

        // Act — a single value appends at the first free cell instead of being dropped
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 9 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        // Assert — D22 holds the append; C22 keeps its 5; nothing skipped or dropped
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
        // Arrange — LEAF1 (C:E) holds a value at D22 ONLY; C22 and E22 are free. D22 is a
        // HOLE inside the block — the strict rule lets the same utterance skip it and fill
        // around it, but NEVER cross into LEAF2.
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 2)] = 4,
        });
        var repo = CreateRepository();

        // Act — values [4,2]: the first fills C22, the second jumps the D22 hole (still
        // inside LEAF1) and lands in E22.
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 4, 2 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        // Assert — C22 and E22 written around the hole, D22 survives untouched, nothing
        // dropped and nothing crossed the block boundary.
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
        // Arrange — LEAF1 (C:E) occupies C22 and E22; only D22 is free while F22/G22/H22
        // (LEAF2) are all free. The D22 hole lies inside the block — after filling it the
        // cursor has NO free cell left in LEAF1, so the strict rule KEEPS the second value
        // in the category and flags it instead of escaping into LEAF2.
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 8,
            [(101, 1, 3)] = 8,
        });
        var repo = CreateRepository();

        // Act — values [4,2]: 4 fills the D22 hole; 2 has no free cell left in LEAF1
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 4, 2 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        // Assert — ONLY D22 is written; the surplus is DROPPED and COUNTED (never silently
        // lost, never written into the next category); F22 must stay empty.
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
        // Arrange — pristine workbook; traitOrdinal 2 means the free-cell scan starts at D
        // (the trait-1 column C is out of scope for this utterance).
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);
        var repo = CreateRepository();

        // Act — values [3,6] append from the row's first free cell at/after traitOrdinal 2
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 2, values: new[] { 3, 6 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        // Assert — D22 (trait 2) and E22 (trait 3), both inside LEAF1's C:E block; C22 is
        // NOT touched by this utterance
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
        // Arrange — LEAF1 (C:E) occupies C22 and D22; only E22 is free — with NO hole to jump
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 8,
            [(101, 1, 2)] = 8,
        });
        var repo = CreateRepository();

        // Act — values [8,9,7]: 8 lands in E22, then the next free cell (F22) belongs to LEAF2
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 8, 9, 7 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        // Assert — ONLY E22 is written; 9 and 7 are dropped and COUNTED (never silently lost);
        // LEAF2 cells F22/G22/H22 must stay empty.
        result.WrittenCells.Should().ContainSingle();
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("E22");
        result.WrittenCells[0].Value.Should().Be(8);
        result.OutOfTraitRangeValueCount.Should().Be(2);
        result.SkippedPresentCells.Should().BeEmpty();

        var copy = FindOutputCopies(outputDir).Should().ContainSingle().Subject;
        using var wb = new XLWorkbook(copy);
        var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
        foreach (var trait in new[] { 4, 5, 6 }) // F, G, H
        {
            sheet.Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(trait))
                .IsEmpty().Should().BeTrue("an utterance never spans category blocks — the strict boundary holds");
        }
    }

    [Fact]
    public void WritePreserving_ScanForFreeCells_FullRegionOverflowFlags()
    {
        // Arrange — every trait cell C..K of plot 101 subsample 1 already holds a value
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, Enumerable.Range(1, 9).ToDictionary(t => (101, 1, t), t => t));
        var repo = CreateRepository();

        // Act — no free cell exists anywhere in the region
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 5 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        // Assert — nothing written, nothing skipped; the value is dropped and counted
        result.WrittenCells.Should().BeEmpty();
        result.SkippedPresentCells.Should().BeEmpty();
        result.OutOfTraitRangeValueCount.Should().Be(1);
        FindOutputCopies(outputDir).Should().BeEmpty("0 written cells ⇒ no copy is saved");
    }

    [Fact]
    public void WritePreserving_ScanForFreeCells_UnlabelledRegionActsAsOneBlock()
    {
        // Arrange — workbook WITHOUT the row-8 Part Rated metadata: the whole region acts as ONE block
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.CreateWithoutPartRated(inputPath, new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 6,
        });
        var repo = CreateRepository();

        // Act — values [6,1] append at the first free cell of the single region-wide block
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        // Assert — D22 and E22 both accept values; the missing metadata must not subdivide
        // the region into empty single-column "blocks" that would drop the second value.
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("D22", "E22");
        result.WrittenCells.Select(c => c.Value).Should().Equal(6, 1);
        result.OutOfTraitRangeValueCount.Should().Be(0);
        result.SkippedPresentCells.Should().BeEmpty();
    }

    [Fact]
    public void WritePreserving_ScanForFreeCells_LeadingEmptyLabelInheritsNextCategory()
    {
        // Arrange — hand-authored row 8: C8 is BLANK while D8:K8 keep LEAF1..LEAF3. The
        // leading blank must inherit the NEXT non-empty label (LEAF1) instead of forming a
        // singleton anonymous block at C that would drop a multi-value utterance.
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

        // Act — values [3,6,1] land at the first free cells of the C:E block inherited as LEAF1
        var result = repo.WritePlotColumnPreserving(
            inputPath, outputDir, plot: 101, traitOrdinal: 1, values: new[] { 3, 6, 1 }, new HashSet<int> { 1 },
            scanForFreeCells: true);

        // Assert — C22/D22/E22 all accept values; nothing flagged or skipped
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C22", "D22", "E22");
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        result.OutOfTraitRangeValueCount.Should().Be(0);
        result.SkippedPresentCells.Should().BeEmpty();
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
    /// The policy write takes explicit paths, so a bare repository suffices
    /// (like <see cref="RatingRepositoryContractTests"/>).
    /// </summary>
    private static ClosedXmlRatingRepository CreateRepository() =>
        new(NullLogger<ClosedXmlRatingRepository>.Instance);

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