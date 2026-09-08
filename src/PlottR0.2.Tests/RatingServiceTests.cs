using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Repository;
using PlottR0._2.Repository.Repositories;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Implementations;
using PlottR0._2.Tests.Support;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

/// <summary>
/// Integration tests for <see cref="RecordRatingsService"/> and
/// <see cref="ClosedXmlRatingRepository"/> against real ClosedXML workbook
/// operations and a real PostgreSQL (via Testcontainers) for audit trail.
/// Each method is covered with one valid, one invalid, and one edge scenario.
/// All tests run inside Docker.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RatingServiceTests : IAsyncLifetime
{
    /// <summary>
    /// The fixed default actor identity reset at the start of every test. The shared fixture's
    /// <see cref="PlottR0._2.Contracts.ICurrentUser"/> is mutable, and other classes in the same
    /// collection leave it nulled without restoring it (see
    /// <c>UserServiceTests.DeleteAsync_IsolatedFromActorIdentity_StillDeletes</c>). Because the
    /// collection is serialized and the inter-class order is undefined, asserting the audit actor
    /// against a LIVE read of <c>_fixture.CurrentUser</c> could silently become <c>null == null</c>.
    /// </summary>
    private static readonly Guid DefaultActorUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly PostgresFixture _fixture;
    private readonly List<string> _tempDirs = [];

    public RatingServiceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Resets the shared mutable current user to the default test identity (the same values
    /// <see cref="Support.TestCurrentUser"/> starts with) so every test begins from a known
    /// actor — never from whatever a previously-run class left behind.
    /// </summary>
    public Task InitializeAsync()
    {
        _fixture.CurrentUser.UserId = DefaultActorUserId;
        _fixture.CurrentUser.Email = "test@plottr02.local";
        _fixture.CurrentUser.Name = "Test Actor";
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fixture.ResetDatabaseAsync();

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

    // ── RecordSubsampleRatingsAsync ──────────────────────────────────────────

    [Fact]
    public async Task RecordSubsampleRatingsAsync_Valid_WritesAllCellsAndPersistsOutput()
    {
        // Arrange
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);

        using var scope = _fixture.CreateScope();
        var repo = CreateRepository(inputPath, outputDir);
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var service = new RecordRatingsService(
            repo, NullLogger<RecordRatingsService>.Instance, auditService, _fixture.CurrentUser);

        // The command is driven by the seeded faker, which consumes the factory's own
        // plot/subsample pools — single source of truth, and every generated value is legal.
        var command = RatingFaker.RecordCommand().Generate();

        // Act
        var result = await service.RecordSubsampleRatingsAsync(command);

        // Assert — result echoes the command
        result.Plot.Should().Be(command.Plot);
        result.TraitOrdinal.Should().Be(command.TraitOrdinal);
        result.Value.Should().Be(command.Value);
        result.EmptyPreserved.Should().BeTrue();
        result.UntouchedSubsamples.Should().BeEmpty();
        result.WrittenCells.Should().HaveCount(command.SubsampleIndices.Count);

        // Every written cell carries the command's value and the subsample set matches the
        // command. There is NO ordering contract on WrittenCells — assert by content, no re-sort.
        result.WrittenCells.Select(wc => wc.Subsample)
            .Should().BeEquivalentTo(command.SubsampleIndices);
        result.WrittenCells.Should().AllSatisfy(wc => wc.Value.Should().Be(command.Value));

        // Cell addresses correspond to (plot, subsample) × trait column.
        var expectedAddresses = command.SubsampleIndices
            .Select(sub => ExpectedAddress(command.Plot, sub, command.TraitOrdinal));
        result.WrittenCells.Select(wc => NormalizeAddress(wc.CellAddress))
            .Should().BeEquivalentTo(expectedAddresses);

        // Assert — PERSISTENCE: output copy exists and holds the command value in every addressed cell
        var outputPath = FindNewestOutput(outputDir);
        outputPath.Should().NotBeNull("output copy should have been created");
        using (var outputWb = new XLWorkbook(outputPath!))
        {
            var sheet = outputWb.Worksheet(RatingWorkbookFactory.SheetName);
            foreach (var subsample in command.SubsampleIndices)
            {
                var cell = sheet.Cell(
                    RatingWorkbookFactory.DataRowFor(command.Plot, subsample),
                    RatingWorkbookFactory.TraitColumnFor(command.TraitOrdinal));
                cell.IsEmpty().Should().BeFalse("cell should have a value");
                ((int)cell.GetDouble()).Should().Be(command.Value);
            }
        }

        // Assert — PRISTINE: input workbook still has empty cells at those coordinates (never mutated)
        using (var pristineWb = new XLWorkbook(inputPath))
        {
            var sheet = pristineWb.Worksheet(RatingWorkbookFactory.SheetName);
            foreach (var subsample in command.SubsampleIndices)
            {
                sheet.Cell(
                        RatingWorkbookFactory.DataRowFor(command.Plot, subsample),
                        RatingWorkbookFactory.TraitColumnFor(command.TraitOrdinal))
                    .IsEmpty().Should().BeTrue("pristine input must never be mutated");
            }
        }

        // Assert — AUDIT: "Rating.Recorded" entry persisted to Postgres with the fixed test actor
        var auditEntries = await auditService.GetRecentAsync();
        auditEntries.Should().Contain(e => e.Action == "Rating.Recorded");
        var entry = auditEntries.First(e => e.Action == "Rating.Recorded");
        entry.EntityType.Should().Be("Rating");
        entry.ActorUserId.Should().Be(DefaultActorUserId);
    }

    [Fact]
    public async Task RecordSubsampleRatingsAsync_InvalidInputs_Throws()
    {
        // Arrange
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);

        using var scope = _fixture.CreateScope();
        var repo = CreateRepository(inputPath, outputDir);
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var service = new RecordRatingsService(
            repo, NullLogger<RecordRatingsService>.Instance, auditService, _fixture.CurrentUser);

        // Act & Assert — value above max (11)
        var actAbove = () => service.RecordSubsampleRatingsAsync(new RecordSubsampleRatingsCommand
        {
            Plot = 101, TraitOrdinal = 1, Value = 11,
            SubsampleIndices = new[] { 1 },
        });
        await actAbove.Should().ThrowAsync<ArgumentOutOfRangeException>();

        // Act & Assert — value below min (-1)
        var actBelow = () => service.RecordSubsampleRatingsAsync(new RecordSubsampleRatingsCommand
        {
            Plot = 101, TraitOrdinal = 1, Value = -1,
            SubsampleIndices = new[] { 1 },
        });
        await actBelow.Should().ThrowAsync<ArgumentOutOfRangeException>();

        // Act & Assert — duplicate subsample indices
        var actDuplicate = () => service.RecordSubsampleRatingsAsync(new RecordSubsampleRatingsCommand
        {
            Plot = 101, TraitOrdinal = 1, Value = 5,
            SubsampleIndices = new[] { 1, 1 },
        });
        await actDuplicate.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RecordSubsampleRatingsAsync_Invalid_NullCommandOrSubsampleIndices_ThrowsArgumentNull()
    {
        // Arrange
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);

        using var scope = _fixture.CreateScope();
        var repo = CreateRepository(inputPath, outputDir);
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var service = new RecordRatingsService(
            repo, NullLogger<RecordRatingsService>.Instance, auditService, _fixture.CurrentUser);

        // Act & Assert — null command
        var actNullCommand = () => service.RecordSubsampleRatingsAsync(null!);
        await actNullCommand.Should().ThrowAsync<ArgumentNullException>();

        // Act & Assert — null SubsampleIndices. RecordSubsampleRatingsCommand is a CLASS, so a
        // caller can legally pass a null list; the service must reject it before any workbook access.
        var actNullIndices = () => service.RecordSubsampleRatingsAsync(new RecordSubsampleRatingsCommand
        {
            Plot = 101, TraitOrdinal = 1, Value = 5, SubsampleIndices = null!,
        });
        await actNullIndices.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RecordSubsampleRatingsAsync_Edge_EmptySubsampleIndices_ThrowsArgumentException()
    {
        // Arrange
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);

        using var scope = _fixture.CreateScope();
        var repo = CreateRepository(inputPath, outputDir);
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var service = new RecordRatingsService(
            repo, NullLogger<RecordRatingsService>.Instance, auditService, _fixture.CurrentUser);

        // Act & Assert — empty SubsampleIndices: a command with nothing to write is a protocol error
        var act = () => service.RecordSubsampleRatingsAsync(new RecordSubsampleRatingsCommand
        {
            Plot = 101, TraitOrdinal = 1, Value = 5, SubsampleIndices = [],
        });
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RecordSubsampleRatingsAsync_Edge_UnmatchedSubsamplesAndDeterministicReWrite()
    {
        // Arrange
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);

        using var scope = _fixture.CreateScope();
        var repo = CreateRepository(inputPath, outputDir);
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var service = new RecordRatingsService(
            repo, NullLogger<RecordRatingsService>.Instance, auditService, _fixture.CurrentUser);

        // Act — partially matched: subsample 1 exists, subsample 5 does not
        var partialCommand = new RecordSubsampleRatingsCommand
        {
            Plot = 101, TraitOrdinal = 1, Value = 5,
            SubsampleIndices = new[] { 1, 5 },
        };
        var partialResult = await service.RecordSubsampleRatingsAsync(partialCommand);

        // Assert — partial match
        partialResult.WrittenCells.Should().HaveCount(1);
        partialResult.WrittenCells.First().Subsample.Should().Be(1);
        partialResult.WrittenCells.First().Value.Should().Be(5);
        partialResult.UntouchedSubsamples.Should().BeEquivalentTo(new[] { 5 });
        partialResult.EmptyPreserved.Should().BeFalse(
            "a partially matched write is not fully preserved — subsample 5 did not exist");

        // Output IS created because at least one cell was written
        FindOutputCopies(outputDir).Should().NotBeEmpty(
            "output copy should exist when SOME cells matched");

        // Act — fully unmatched: subsample 5 does not exist for any plot
        // Capture the output-file count BEFORE the write to prove no new copy is made.
        var outputCountBefore = FindOutputCopies(outputDir).Length;

        var unmatchedCommand = new RecordSubsampleRatingsCommand
        {
            Plot = 101, TraitOrdinal = 1, Value = 3,
            SubsampleIndices = new[] { 5, 6 },
        };
        var unmatchedResult = await service.RecordSubsampleRatingsAsync(unmatchedCommand);

        // Assert — fully unmatched
        unmatchedResult.WrittenCells.Should().BeEmpty();
        unmatchedResult.UntouchedSubsamples.Should().BeEquivalentTo(new[] { 5, 6 });
        unmatchedResult.EmptyPreserved.Should().BeFalse(
            "a fully unmatched write preserves nothing of the request");

        // Fully unmatched write must NOT create a new output copy
        var outputCountAfter = FindOutputCopies(outputDir).Length;
        outputCountAfter.Should().Be(outputCountBefore,
            "fully unmatched command must not create an output copy");

        // Act — deterministic re-write: record the same value to the same cells twice.
        // This is NOT a "true no-op": WritePlotColumn re-assigns and re-saves on every call (the
        // repository's no-op comment is aspirational), so the second write either appends a new
        // copy or overwrites the same-second file. What we can promise is that a deterministic
        // re-write persists — the newest output copy must still hold the value.
        var rewriteCommand = new RecordSubsampleRatingsCommand
        {
            Plot = 102, TraitOrdinal = 2, Value = 9,
            SubsampleIndices = new[] { 2, 3 },
        };
        var first = await service.RecordSubsampleRatingsAsync(rewriteCommand);
        var second = await service.RecordSubsampleRatingsAsync(rewriteCommand);

        // Assert — both writes report identical results
        second.Plot.Should().Be(first.Plot);
        second.TraitOrdinal.Should().Be(first.TraitOrdinal);
        second.Value.Should().Be(first.Value);
        second.WrittenCells.Should().HaveCount(first.WrittenCells.Count);
        second.WrittenCells.Should().BeEquivalentTo(first.WrittenCells);

        // Assert — the second write PERSISTED: the newest output copy still contains the value
        var newest = FindNewestOutput(outputDir);
        newest.Should().NotBeNull("a re-write must still persist the value to an output copy");
        using (var wb = new XLWorkbook(newest!))
        {
            var sheet = wb.Worksheet(RatingWorkbookFactory.SheetName);
            foreach (var subsample in rewriteCommand.SubsampleIndices)
            {
                var cell = sheet.Cell(
                    RatingWorkbookFactory.DataRowFor(rewriteCommand.Plot, subsample),
                    RatingWorkbookFactory.TraitColumnFor(rewriteCommand.TraitOrdinal));
                cell.IsEmpty().Should().BeFalse();
                ((int)cell.GetDouble()).Should().Be(9);
            }
        }
    }

    [Fact]
    public async Task RecordSubsampleRatingsAsync_Edge_ValueBoundsZeroAndTen_AreValid()
    {
        // Arrange
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);

        using var scope = _fixture.CreateScope();
        var repo = CreateRepository(inputPath, outputDir);
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var service = new RecordRatingsService(
            repo, NullLogger<RecordRatingsService>.Instance, auditService, _fixture.CurrentUser);

        var plot = RatingWorkbookFactory.Plots[0]; // 101

        // Act — value 0 is the domain minimum and a LEGAL rating: writing it must produce a
        // PRESENT cell reading 0, never an empty cell (Rule 2: empty ≠ 0).
        var zeroCommand = new RecordSubsampleRatingsCommand
        {
            Plot = plot, TraitOrdinal = 1, Value = 0, SubsampleIndices = new[] { 1 },
        };
        var zeroResult = await service.RecordSubsampleRatingsAsync(zeroCommand);

        // Assert — result reports value 0 with a written cell
        zeroResult.Value.Should().Be(0);
        zeroResult.WrittenCells.Should().ContainSingle().Which.Value.Should().Be(0);

        // Persistence — the cell is PRESENT and reads 0, not null/empty
        var zeroOutput = FindNewestOutput(outputDir);
        zeroOutput.Should().NotBeNull("value 0 must still persist a present cell");
        using (var wb = new XLWorkbook(zeroOutput!))
        {
            var cell = wb.Worksheet(RatingWorkbookFactory.SheetName)
                .Cell(RatingWorkbookFactory.DataRowFor(plot, 1), RatingWorkbookFactory.TraitColumnFor(1));
            cell.IsEmpty().Should().BeFalse("rating 0 is a value, not an empty cell");
            cell.GetDouble().Should().Be(0);
        }

        // Act — value 10 is the domain maximum and also a legal rating
        var tenCommand = new RecordSubsampleRatingsCommand
        {
            Plot = plot, TraitOrdinal = 1, Value = 10, SubsampleIndices = new[] { 1 },
        };
        var tenResult = await service.RecordSubsampleRatingsAsync(tenCommand);

        // Assert
        tenResult.Value.Should().Be(10);
        tenResult.WrittenCells.Should().ContainSingle().Which.Value.Should().Be(10);

        var tenOutput = FindNewestOutput(outputDir);
        tenOutput.Should().NotBeNull("value 10 must persist");
        using (var wb = new XLWorkbook(tenOutput!))
        {
            var cell = wb.Worksheet(RatingWorkbookFactory.SheetName)
                .Cell(RatingWorkbookFactory.DataRowFor(plot, 1), RatingWorkbookFactory.TraitColumnFor(1));
            cell.IsEmpty().Should().BeFalse();
            ((int)cell.GetDouble()).Should().Be(10);
        }
    }

    [Fact]
    public async Task RecordSubsampleRatingsAsync_TraitNine_MapsToColumnK()
    {
        // Arrange
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);

        using var scope = _fixture.CreateScope();
        var repo = CreateRepository(inputPath, outputDir);
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var service = new RecordRatingsService(
            repo, NullLogger<RecordRatingsService>.Instance, auditService, _fixture.CurrentUser);

        var command = new RecordSubsampleRatingsCommand
        {
            Plot = 101, TraitOrdinal = 9, Value = 8, SubsampleIndices = new[] { 1 },
        };

        // Act
        var result = await service.RecordSubsampleRatingsAsync(command);

        // Assert — the written cell address sits in column K (trait 9 = column 11 = K)
        var written = result.WrittenCells.Should().ContainSingle().Subject;
        written.Subsample.Should().Be(1);
        written.Value.Should().Be(8);
        NormalizeAddress(written.CellAddress).Should().Be(
            $"K{RatingWorkbookFactory.DataRowFor(101, 1)}");

        // Assert — ClosedXML re-read lands on the same column-K cell
        var outputPath = FindNewestOutput(outputDir);
        outputPath.Should().NotBeNull("output copy should exist");
        using (var wb = new XLWorkbook(outputPath!))
        {
            var cell = wb.Worksheet(RatingWorkbookFactory.SheetName)
                .Cell(RatingWorkbookFactory.DataRowFor(101, 1), RatingWorkbookFactory.TraitColumnFor(9));
            cell.IsEmpty().Should().BeFalse();
            ((int)cell.GetDouble()).Should().Be(8);
        }
    }

    [Fact]
    public async Task RecordSubsampleRatingsAsync_Edge_MultiDigitSubsampleKey_ParsesAndWrites()
    {
        // Arrange — workbook whose subsample set includes 10 → key "101 10" (multi-digit RHS).
        var (inputPath, outputDir) = CreateTempWorkspace();
        var created = RatingWorkbookFactory.CreateWithMultiDigitSubsample(inputPath, plot: 101);
        var rowForSubsample10 = created.RowBySubsample[10];

        using var scope = _fixture.CreateScope();
        var repo = CreateRepository(inputPath, outputDir);
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var service = new RecordRatingsService(
            repo, NullLogger<RecordRatingsService>.Instance, auditService, _fixture.CurrentUser);

        var command = new RecordSubsampleRatingsCommand
        {
            Plot = 101, TraitOrdinal = 3, Value = 6, SubsampleIndices = new[] { 10 },
        };

        // Act
        var result = await service.RecordSubsampleRatingsAsync(command);

        // Assert — the multi-digit key was parsed ("101 10" → subsample 10) and matched
        result.WrittenCells.Should().ContainSingle();
        result.WrittenCells[0].Subsample.Should().Be(10);
        result.WrittenCells[0].Value.Should().Be(6);
        result.UntouchedSubsamples.Should().BeEmpty();

        // Assert — persistence lands on the exact row the factory emitted for "101 10"
        var outputPath = FindNewestOutput(outputDir);
        outputPath.Should().NotBeNull("output copy should exist");
        using (var wb = new XLWorkbook(outputPath!))
        {
            var cell = wb.Worksheet(RatingWorkbookFactory.SheetName)
                .Cell(rowForSubsample10, RatingWorkbookFactory.TraitColumnFor(3));
            cell.IsEmpty().Should().BeFalse();
            ((int)cell.GetDouble()).Should().Be(6);
        }
    }

    // ── ReadPlotColumnAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ReadPlotColumnAsync_Valid_ReturnsPrefilledCellsInSubsampleOrder()
    {
        // Arrange — pre-fill plot 101, trait 1 with values equal to their subsample index
        var prefilled = new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 1,
            [(101, 2, 1)] = 2,
            [(101, 3, 1)] = 3,
            [(101, 4, 1)] = 4,
        };
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath, prefilled);

        using var scope = _fixture.CreateScope();
        var repo = CreateRepository(inputPath, outputDir);
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var service = new RecordRatingsService(
            repo, NullLogger<RecordRatingsService>.Instance, auditService, _fixture.CurrentUser);

        // Act
        var cells = await service.ReadPlotColumnAsync(plot: 101, traitOrdinal: 1);

        // Assert — CONTRACT: the repository returns the cells in subsample order. Assert the
        // RAW result — re-sorting here would mask the contract (IRatingRepository promises it).
        cells.Should().HaveCount(4);
        cells.Select(c => c.Subsample).Should().Equal(1, 2, 3, 4);

        // Values are seeded equal to their subsample, so raw order is observable.
        cells.Select(c => c.Value).Should().Equal(1, 2, 3, 4);

        cells.Should().AllSatisfy(c =>
        {
            c.Plot.Should().Be(101);
            c.TraitOrdinal.Should().Be(1);
        });

        // Addresses follow the same raw order: C22, C23, C24, C25
        cells.Select(c => NormalizeAddress(c.CellAddress))
            .Should().Equal("C22", "C23", "C24", "C25");
    }

    [Fact]
    public async Task ReadPlotColumnAsync_InvalidTraitOrdinal_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);

        using var scope = _fixture.CreateScope();
        var repo = CreateRepository(inputPath, outputDir);
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var service = new RecordRatingsService(
            repo, NullLogger<RecordRatingsService>.Instance, auditService, _fixture.CurrentUser);

        // Act & Assert — traitOrdinal 10 (above max)
        var actAbove = () => service.ReadPlotColumnAsync(plot: 101, traitOrdinal: 10);
        await actAbove.Should().ThrowAsync<ArgumentOutOfRangeException>();

        // Act & Assert — traitOrdinal 0 (below min)
        var actBelow = () => service.ReadPlotColumnAsync(plot: 101, traitOrdinal: 0);
        await actBelow.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ReadPlotColumnAsync_Edge_NoPrefill_ReturnsNullValues()
    {
        // Arrange — empty workbook (no prefilled cells)
        var (inputPath, outputDir) = CreateTempWorkspace();
        RatingWorkbookFactory.Create(inputPath);

        using var scope = _fixture.CreateScope();
        var repo = CreateRepository(inputPath, outputDir);
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var service = new RecordRatingsService(
            repo, NullLogger<RecordRatingsService>.Instance, auditService, _fixture.CurrentUser);

        // Act — no pre-fill: all values should be null (NEVER 0)
        var cells = await service.ReadPlotColumnAsync(plot: 101, traitOrdinal: 1);

        // Assert
        cells.Should().HaveCount(4);
        cells.Should().AllSatisfy(c =>
        {
            c.Value.Should().BeNull("empty cells must be null, never 0 (Rule 2: empty ≠ 0)");
            c.Plot.Should().Be(101);
        });

        // Act — plot with no matching rows → empty list
        var noMatch = await service.ReadPlotColumnAsync(plot: 999, traitOrdinal: 1);

        // Assert
        noMatch.Should().BeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a unique temp folder with input and output subdirectories.
    /// The folder is tracked for cleanup in <see cref="DisposeAsync"/>.
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
    /// Composes a <see cref="ClosedXmlRatingRepository"/> with per-test absolute temp paths.
    /// NOT resolved from DI — <c>ExcelWorkbookOptions</c> is not registered in the fixture.
    /// </summary>
    private static ClosedXmlRatingRepository CreateRepository(string inputPath, string outputDir)
    {
        var options = Options.Create(new ExcelWorkbookOptions
        {
            InputPath = inputPath,
            OutputDirectory = outputDir,
        });
        return new ClosedXmlRatingRepository(options, NullLogger<ClosedXmlRatingRepository>.Instance);
    }

    /// <summary>
    /// Strips <c>$</c> signs from a cell address for flexible assertion matching.
    /// ClosedXML may return <c>$C$22</c>; tests assert against <c>C22</c>.
    /// </summary>
    private static string NormalizeAddress(string address) => address.Replace("$", "");

    /// <summary>
    /// Returns the output copies in the given directory, or an empty array when the directory
    /// does not exist yet — callers must never assume a prior write created it.
    /// </summary>
    private static string[] FindOutputCopies(string outputDir)
        => Directory.Exists(outputDir) ? Directory.GetFiles(outputDir, "*_out_*.xlsx") : [];

    /// <summary>
    /// Finds the newest output copy in the given directory, or <c>null</c> when the directory
    /// does not exist or contains no output copies yet.
    /// </summary>
    private static string? FindNewestOutput(string outputDir)
    {
        var files = FindOutputCopies(outputDir);
        return files.Length == 0 ? null : files.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
    }

    /// <summary>
    /// Computes the absolute cell address (e.g. <c>C22</c>) that a write to
    /// <c>(plot, subsample, traitOrdinal)</c> must land on.
    /// </summary>
    private static string ExpectedAddress(int plot, int subsample, int traitOrdinal)
        => $"{ColumnLetters(RatingWorkbookFactory.TraitColumnFor(traitOrdinal))}{RatingWorkbookFactory.DataRowFor(plot, subsample)}";

    /// <summary>Converts a 1-based column number to Excel column letters (1→A, 3→C, 11→K, …).</summary>
    private static string ColumnLetters(int columnNumber)
    {
        var letters = string.Empty;
        while (columnNumber > 0)
        {
            var rem = (columnNumber - 1) % 26;
            letters = (char)('A' + rem) + letters;
            columnNumber = (columnNumber - 1) / 26;
        }
        return letters;
    }
}