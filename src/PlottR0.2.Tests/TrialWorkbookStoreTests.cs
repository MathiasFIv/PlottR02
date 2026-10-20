using PlottR0._2.Repository.Repositories;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

public sealed class TrialWorkbookStoreTests : IAsyncLifetime
{
    private readonly string _tempRoot;
    private readonly List<(Guid TrialId, string RelativePath)> _createdFiles = [];

    public TrialWorkbookStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "PlottrTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var cleanedDirs = new HashSet<string>();

        foreach (var (_, relativePath) in _createdFiles)
        {
            try
            {
                var absolutePath = Path.Combine(_tempRoot, relativePath);
                if (File.Exists(absolutePath))
                    File.Delete(absolutePath);

                var parentDir = Path.GetDirectoryName(absolutePath);
                if (parentDir is not null)
                    cleanedDirs.Add(parentDir);
            }
            catch
            {
            }
        }

        foreach (var dir in cleanedDirs)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch
            {
            }
        }

        try
        {
            if (Directory.Exists(_tempRoot) && !Directory.EnumerateFileSystemEntries(_tempRoot).Any())
                Directory.Delete(_tempRoot);
        }
        catch
        {
        }

        await Task.CompletedTask;
    }

    private TrialWorkbookStore CreateStore() => new(_tempRoot, null);

    [Fact]
    public async Task SaveAsync_ValidXlsx_SavesFileAndReturnsRelativePath()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "valid trial.xlsx";
        using var stream = TrialWorkbookFactory.CreateStream();

        var relativePath = await store.SaveAsync(trialId, fileName, stream);
        _createdFiles.Add((trialId, relativePath));

        relativePath.Should().StartWith($"Working{Path.DirectorySeparatorChar}");
        relativePath.Should().Contain(trialId.ToString());
        relativePath.Should().EndWith("valid trial.xlsx");

        var expectedPath = Path.Combine(_tempRoot, relativePath);
        File.Exists(expectedPath).Should().BeTrue("the workbook file should exist on disk");
    }

    [Fact]
    public async Task SaveAsync_InvalidContent_ThrowsInvalidDataException()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "garbage.xlsx";
        var garbageBytes = "this is not a workbook"u8.ToArray();
        using var stream = new MemoryStream(garbageBytes);

        var act = () => store.SaveAsync(trialId, fileName, stream);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task SaveAsync_WrongExtension_ThrowsArgumentException()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "document.txt";
        using var stream = TrialWorkbookFactory.CreateStream();

        var act = () => store.SaveAsync(trialId, fileName, stream);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*xlsx*");
    }

    [Fact]
    public async Task SaveAsync_EmptyFileName_ThrowsArgumentException()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        using var stream = TrialWorkbookFactory.CreateStream();

        var act = () => store.SaveAsync(trialId, "", stream);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SaveAsync_NullStream_ThrowsArgumentNull()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();

        var act = () => store.SaveAsync(trialId, "file.xlsx", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SaveAsync_EmptyStream_ThrowsArgumentException()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        using var stream = new MemoryStream();

        var act = () => store.SaveAsync(trialId, "file.xlsx", stream);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DeleteAsync_ExistingFile_RemovesIt()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        using var stream = TrialWorkbookFactory.CreateStream();
        var relativePath = await store.SaveAsync(trialId, "delete me.xlsx", stream);

        var absolutePath = Path.Combine(_tempRoot, relativePath);
        File.Exists(absolutePath).Should().BeTrue("file should exist after save");

        await store.DeleteAsync(relativePath);

        File.Exists(absolutePath).Should().BeFalse("file should be deleted");
    }

    [Fact]
    public async Task DeleteAsync_NonExistentFile_DoesNotThrow()
    {
        var store = CreateStore();
        var fakePath = Path.Combine("Working", Guid.NewGuid().ToString(), "nope.xlsx");

        var act = () => store.DeleteAsync(fakePath);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ResolveExport_NoOutputCopy_ReturnsPristineSource()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "pristine source.xlsx";
        using var stream = TrialWorkbookFactory.CreateStream();
        var relativePath = await store.SaveAsync(trialId, fileName, stream);
        _createdFiles.Add((trialId, relativePath));

        var resolved = await store.ResolveExportAsync(trialId, relativePath);

        resolved.Should().NotBeNull();
        resolved!.FilePath.Should().Be(
            Path.Combine(_tempRoot, relativePath),
            "with no _out_ copies the pristine uploaded source should be resolved");
        resolved.FileName.Should().Be(fileName);
        File.Exists(resolved.FilePath).Should().BeTrue("the resolved export path should exist on disk");
    }

    [Fact]
    public async Task ResolveExport_WithOutputCopies_ReturnsNewest()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "trial source.xlsx";
        using var stream = TrialWorkbookFactory.CreateStream();
        var relativePath = await store.SaveAsync(trialId, fileName, stream);
        _createdFiles.Add((trialId, relativePath));

        var workingDir = Path.Combine(_tempRoot, "Working", trialId.ToString());
        var older = TrialWorkbookFactory.Create(
            Path.Combine(workingDir, "trial source_out_20260101_000001.xlsx"));
        var newer = TrialWorkbookFactory.Create(
            Path.Combine(workingDir, "trial source_out_20260102_000002.xlsx"));
        TrackDirectFile(trialId, older);
        TrackDirectFile(trialId, newer);

        var resolved = await store.ResolveExportAsync(trialId, relativePath);

        resolved.Should().NotBeNull();
        resolved!.FilePath.Should().Be(newer, "the copy with the greatest _out_ timestamp should win");
        resolved.FileName.Should().Be(Path.GetFileName(newer));
    }

    [Fact]
    public async Task ResolveExport_MissingDirAndSource_ReturnsNull()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var bogusRelativePath = Path.Combine("Working", trialId.ToString(), "never uploaded.xlsx");

        var resolved = await store.ResolveExportAsync(trialId, bogusRelativePath);

        resolved.Should().BeNull("neither an output copy nor the source workbook exists");
    }

    [Fact]
    public async Task ResolveExport_ChainedOutputCopy_UsesLastMarker()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var workingDir = Path.Combine(_tempRoot, "Working", trialId.ToString());
        var first = TrialWorkbookFactory.Create(
            Path.Combine(workingDir, "chain_out_20260101_010203.xlsx"));
        var chained = TrialWorkbookFactory.Create(
            Path.Combine(workingDir, "chain_out_20260101_010203_out_20260102_020304.xlsx"));
        TrackDirectFile(trialId, first);
        TrackDirectFile(trialId, chained);

        var resolved = await store.ResolveExportAsync(
            trialId, Path.Combine("Working", trialId.ToString(), "chain.xlsx"));

        resolved.Should().NotBeNull();
        resolved!.FilePath.Should().Be(chained, "the last _out_ marker should be authoritative for chained copies");
    }

    [Fact]
    public async Task ResolveExport_MillisecondStamp_BeatsSameSecondSecondPrecisionCopy()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var workingDir = Path.Combine(_tempRoot, "Working", trialId.ToString());
        var secondPrecision = TrialWorkbookFactory.Create(
            Path.Combine(workingDir, "trial source_out_20260101_120000.xlsx"));
        var millisecond = TrialWorkbookFactory.Create(
            Path.Combine(workingDir, "trial source_out_20260101_120000_001.xlsx"));
        TrackDirectFile(trialId, secondPrecision);
        TrackDirectFile(trialId, millisecond);

        var resolved = await store.ResolveExportAsync(
            trialId, Path.Combine("Working", trialId.ToString(), "trial source.xlsx"));

        resolved.Should().NotBeNull();
        resolved!.FilePath.Should().Be(
            millisecond, "the millisecond copy inside the same second must outrank its neighbour");
    }

    [Fact]
    public async Task ResolveExport_MillisecondStampOfEarlierSecond_LosesToLaterStamp()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var workingDir = Path.Combine(_tempRoot, "Working", trialId.ToString());
        var laterSecond = TrialWorkbookFactory.Create(
            Path.Combine(workingDir, "trial source_out_20260102_000000.xlsx"));
        var earlierMillisecond = TrialWorkbookFactory.Create(
            Path.Combine(workingDir, "trial source_out_20260101_120000_999.xlsx"));
        TrackDirectFile(trialId, laterSecond);
        TrackDirectFile(trialId, earlierMillisecond);

        var resolved = await store.ResolveExportAsync(
            trialId, Path.Combine("Working", trialId.ToString(), "trial source.xlsx"));

        resolved.Should().NotBeNull();
        resolved!.FilePath.Should().Be(
            laterSecond, "a millisecond copy only wins inside its own second — the newest stamp decides");
    }

    [Fact]
    public async Task ResolveExport_OutputCopyWithUnparseableStamp_IsIgnored()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "unparseable source.xlsx";
        using var stream = TrialWorkbookFactory.CreateStream();
        var relativePath = await store.SaveAsync(trialId, fileName, stream);
        _createdFiles.Add((trialId, relativePath));

        var workingDir = Path.Combine(_tempRoot, "Working", trialId.ToString());
        var unparseable = TrialWorkbookFactory.Create(
            Path.Combine(workingDir, "unparseable source_out_soon.xlsx"));
        TrackDirectFile(trialId, unparseable);

        var resolved = await store.ResolveExportAsync(trialId, relativePath);

        resolved.Should().NotBeNull();
        resolved!.FilePath.Should().Be(
            Path.Combine(_tempRoot, relativePath),
            "copies with unparseable stamps should be silently ignored");
    }

    [Fact]
    public async Task ResolveExport_WorkingDirExistsButEmpty_ReturnsPristineSource()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "empty working dir source.xlsx";
        using var stream = TrialWorkbookFactory.CreateStream();
        var relativePath = await store.SaveAsync(trialId, fileName, stream);
        _createdFiles.Add((trialId, relativePath));

        Directory.Exists(Path.Combine(_tempRoot, "Working", trialId.ToString()))
            .Should().BeTrue("SaveAsync should create the trial working directory");

        var resolved = await store.ResolveExportAsync(trialId, relativePath);

        resolved.Should().NotBeNull();
        resolved!.FilePath.Should().Be(Path.Combine(_tempRoot, relativePath));
    }

    [Fact]
    public async Task ResolveExport_SourceEscapingServiceRoot_ReturnsNull()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var escapeName = "PlottrTests_escape_" + Guid.NewGuid().ToString("N") + ".xlsx";
        var escapeFile = TrialWorkbookFactory.Create(Path.Combine(Path.GetTempPath(), escapeName));
        TrackDirectFile(trialId, escapeFile);

        var escapingRelativePath = Path.Combine("..", escapeName);

        var resolved = await store.ResolveExportAsync(trialId, escapingRelativePath);

        resolved.Should().BeNull("relative source paths must not escape the Service/ directory");
    }

    [Fact]
    public async Task ResolveExport_Version_HasOpaqueFormatShapeAndIsDeterministic()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        using var stream = TrialWorkbookFactory.CreateStream();
        var relativePath = await store.SaveAsync(trialId, "version shape.xlsx", stream);
        _createdFiles.Add((trialId, relativePath));

        var first = await store.ResolveExportAsync(trialId, relativePath);
        var second = await store.ResolveExportAsync(trialId, relativePath);

        first.Should().NotBeNull();
        first!.Version.Should().MatchRegex(
            "^[0-9a-f]{8}-[0-9a-f]+-\\d+$",
            "the opaque version is 8 lowercase hex chars, hex ticks, then decimal length");
        second!.Version.Should().Be(first.Version, "an unchanged file must produce an identical version");
    }

    [Fact]
    public async Task ResolveExport_FileChanged_VersionChangesWithMtimeAndLength()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        using var stream = TrialWorkbookFactory.CreateStream();
        var relativePath = await store.SaveAsync(trialId, "version changes.xlsx", stream);
        _createdFiles.Add((trialId, relativePath));
        var absolutePath = Path.Combine(_tempRoot, relativePath);

        var v1 = (await store.ResolveExportAsync(trialId, relativePath))!.Version;

        File.SetLastWriteTimeUtc(absolutePath, DateTime.UtcNow.AddMinutes(3));
        var v2 = (await store.ResolveExportAsync(trialId, relativePath))!.Version;

        var touchedMtime = File.GetLastWriteTimeUtc(absolutePath);
        using (var fs = new FileStream(absolutePath, FileMode.Append, FileAccess.Write))
        {
            fs.WriteByte(0);
        }
        File.SetLastWriteTimeUtc(absolutePath, touchedMtime);
        var v3 = (await store.ResolveExportAsync(trialId, relativePath))!.Version;

        v2.Should().NotBe(v1, "the version embeds lastWriteTimeUtc.Ticks");
        v3.Should().NotBe(v2, "the version embeds the file length");
    }

    private void TrackDirectFile(Guid trialId, string absolutePath)
    {
        _createdFiles.Add((trialId, Path.GetRelativePath(_tempRoot, absolutePath)));
    }
}
