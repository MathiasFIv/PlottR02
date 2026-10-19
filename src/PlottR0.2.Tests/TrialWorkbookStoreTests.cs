using PlottR0._2.Repository.Repositories;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

/// <summary>
/// Pure unit tests for <see cref="TrialWorkbookStore"/>.
/// Tests file I/O and workbook validation without touching Postgres.
/// Uses a per-test isolated temp directory to avoid interfering with
/// concurrent tests that share the repo-root Service/Working/ root.
/// </summary>
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
        // Clean up every file (and its parent Working/<id> dir) created by the store.
        var cleanedDirs = new HashSet<string>();

        foreach (var (_, relativePath) in _createdFiles)
        {
            try
            {
                var absolutePath = Path.Combine(_tempRoot, relativePath);
                if (File.Exists(absolutePath))
                    File.Delete(absolutePath);

                // Track the parent dir for removal if empty.
                var parentDir = Path.GetDirectoryName(absolutePath);
                if (parentDir is not null)
                    cleanedDirs.Add(parentDir);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        // Remove empty Working/<id>/ directories.
        foreach (var dir in cleanedDirs)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        // Remove the temp root itself if it's empty.
        try
        {
            if (Directory.Exists(_tempRoot) && !Directory.EnumerateFileSystemEntries(_tempRoot).Any())
                Directory.Delete(_tempRoot);
        }
        catch
        {
            // Best-effort cleanup.
        }

        await Task.CompletedTask;
    }

    private TrialWorkbookStore CreateStore() => new(_tempRoot, null);

    [Fact]
    public async Task SaveAsync_ValidXlsx_SavesFileAndReturnsRelativePath()
    {
        // Arrange
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "valid trial.xlsx";
        using var stream = TrialWorkbookFactory.CreateStream();

        // Act
        var relativePath = await store.SaveAsync(trialId, fileName, stream);
        _createdFiles.Add((trialId, relativePath));

        // Assert
        relativePath.Should().StartWith($"Working{Path.DirectorySeparatorChar}");
        relativePath.Should().Contain(trialId.ToString());
        relativePath.Should().EndWith("valid trial.xlsx");

        // Verify file exists on disk
        var expectedPath = Path.Combine(_tempRoot, relativePath);
        File.Exists(expectedPath).Should().BeTrue("the workbook file should exist on disk");
    }

    [Fact]
    public async Task SaveAsync_InvalidContent_ThrowsInvalidDataException()
    {
        // Arrange
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "garbage.xlsx";
        var garbageBytes = "this is not a workbook"u8.ToArray();
        using var stream = new MemoryStream(garbageBytes);

        // Act & Assert
        var act = () => store.SaveAsync(trialId, fileName, stream);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task SaveAsync_WrongExtension_ThrowsArgumentException()
    {
        // Arrange
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "document.txt";
        using var stream = TrialWorkbookFactory.CreateStream();

        // Act & Assert
        var act = () => store.SaveAsync(trialId, fileName, stream);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*xlsx*");
    }

    [Fact]
    public async Task SaveAsync_EmptyFileName_ThrowsArgumentException()
    {
        // Arrange
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        using var stream = TrialWorkbookFactory.CreateStream();

        // Act & Assert
        var act = () => store.SaveAsync(trialId, "", stream);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SaveAsync_NullStream_ThrowsArgumentNull()
    {
        // Arrange
        var store = CreateStore();
        var trialId = Guid.NewGuid();

        // Act & Assert
        var act = () => store.SaveAsync(trialId, "file.xlsx", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SaveAsync_EmptyStream_ThrowsArgumentException()
    {
        // Arrange
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        using var stream = new MemoryStream();

        // Act & Assert
        var act = () => store.SaveAsync(trialId, "file.xlsx", stream);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DeleteAsync_ExistingFile_RemovesIt()
    {
        // Arrange
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        using var stream = TrialWorkbookFactory.CreateStream();
        var relativePath = await store.SaveAsync(trialId, "delete me.xlsx", stream);

        var absolutePath = Path.Combine(_tempRoot, relativePath);
        File.Exists(absolutePath).Should().BeTrue("file should exist after save");

        // Act
        await store.DeleteAsync(relativePath);

        // Assert
        File.Exists(absolutePath).Should().BeFalse("file should be deleted");
    }

    [Fact]
    public async Task DeleteAsync_NonExistentFile_DoesNotThrow()
    {
        // Arrange
        var store = CreateStore();
        var fakePath = Path.Combine("Working", Guid.NewGuid().ToString(), "nope.xlsx");

        // Act & Assert — should not throw
        var act = () => store.DeleteAsync(fakePath);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ResolveExport_NoOutputCopy_ReturnsPristineSource()
    {
        // Arrange — save a trial workbook but write no *_out_*.xlsx copies.
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "pristine source.xlsx";
        using var stream = TrialWorkbookFactory.CreateStream();
        var relativePath = await store.SaveAsync(trialId, fileName, stream);
        _createdFiles.Add((trialId, relativePath));

        // Act
        var resolved = await store.ResolveExportAsync(trialId, relativePath);

        // Assert — the pristine uploaded source is served as the absolute path
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
        // Arrange — trial source plus two sync output copies; the newer stamp must win.
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

        // Act
        var resolved = await store.ResolveExportAsync(trialId, relativePath);

        // Assert — the copy with the greatest _out_ timestamp is served
        resolved.Should().NotBeNull();
        resolved!.FilePath.Should().Be(newer, "the copy with the greatest _out_ timestamp should win");
        resolved.FileName.Should().Be(Path.GetFileName(newer));
    }

    [Fact]
    public async Task ResolveExport_MissingDirAndSource_ReturnsNull()
    {
        // Arrange — a random trial with no working dir and a source path that never existed.
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var bogusRelativePath = Path.Combine("Working", trialId.ToString(), "never uploaded.xlsx");

        // Act
        var resolved = await store.ResolveExportAsync(trialId, bogusRelativePath);

        // Assert
        resolved.Should().BeNull("neither an output copy nor the source workbook exists");
    }

    [Fact]
    public async Task ResolveExport_ChainedOutputCopy_UsesLastMarker()
    {
        // Arrange — a copy created from another output copy carries TWO _out_ markers.
        // The LAST marker must be parsed, so the chained copy beats its own parent.
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var workingDir = Path.Combine(_tempRoot, "Working", trialId.ToString());
        var first = TrialWorkbookFactory.Create(
            Path.Combine(workingDir, "chain_out_20260101_010203.xlsx"));
        var chained = TrialWorkbookFactory.Create(
            Path.Combine(workingDir, "chain_out_20260101_010203_out_20260102_020304.xlsx"));
        TrackDirectFile(trialId, first);
        TrackDirectFile(trialId, chained);

        // Act
        var resolved = await store.ResolveExportAsync(
            trialId, Path.Combine("Working", trialId.ToString(), "chain.xlsx"));

        // Assert — the last `_out_` marker is authoritative when names are chained
        resolved.Should().NotBeNull();
        resolved!.FilePath.Should().Be(chained, "the last _out_ marker should be authoritative for chained copies");
    }

    [Fact]
    public async Task ResolveExport_OutputCopyWithUnparseableStamp_IsIgnored()
    {
        // Arrange — an _out_ name whose trailing stamp cannot parse as yyyyMMdd_HHmmss
        // must be silently ignored, so resolution falls back to the pristine source.
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

        // Act
        var resolved = await store.ResolveExportAsync(trialId, relativePath);

        // Assert — the garbage stamp does not match, so the pristine upload is served.
        resolved.Should().NotBeNull();
        resolved!.FilePath.Should().Be(
            Path.Combine(_tempRoot, relativePath),
            "copies with unparseable stamps should be silently ignored");
    }

    [Fact]
    public async Task ResolveExport_WorkingDirExistsButEmpty_ReturnsPristineSource()
    {
        // Arrange — SaveAsync creates the working directory; it holds no _out_ copies.
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "empty working dir source.xlsx";
        using var stream = TrialWorkbookFactory.CreateStream();
        var relativePath = await store.SaveAsync(trialId, fileName, stream);
        _createdFiles.Add((trialId, relativePath));

        Directory.Exists(Path.Combine(_tempRoot, "Working", trialId.ToString()))
            .Should().BeTrue("SaveAsync should create the trial working directory");

        // Act
        var resolved = await store.ResolveExportAsync(trialId, relativePath);

        // Assert — empty working dir is not an error; the pristine source wins.
        resolved.Should().NotBeNull();
        resolved!.FilePath.Should().Be(Path.Combine(_tempRoot, relativePath));
    }

    [Fact]
    public async Task ResolveExport_SourceEscapingServiceRoot_ReturnsNull()
    {
        // Arrange — a source relative path that climbs out of the Service/ root via `..`.
        // The anchor file EXISTS outside the root, so a null result can only come from
        // the containment guard, not from File.Exists failing.
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var escapeName = "PlottrTests_escape_" + Guid.NewGuid().ToString("N") + ".xlsx";
        var escapeFile = TrialWorkbookFactory.Create(Path.Combine(Path.GetTempPath(), escapeName));
        TrackDirectFile(trialId, escapeFile);

        var escapingRelativePath = Path.Combine("..", escapeName);

        // Act
        var resolved = await store.ResolveExportAsync(trialId, escapingRelativePath);

        // Assert — resolved path escapes the Service/ root and must yield null.
        resolved.Should().BeNull("relative source paths must not escape the Service/ directory");
    }

    [Fact]
    public async Task ResolveExport_Version_HasOpaqueFormatShapeAndIsDeterministic()
    {
        // Arrange — a resolvable workbook (pristine source, no _out_ copies)
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        using var stream = TrialWorkbookFactory.CreateStream();
        var relativePath = await store.SaveAsync(trialId, "version shape.xlsx", stream);
        _createdFiles.Add((trialId, relativePath));

        // Act
        var first = await store.ResolveExportAsync(trialId, relativePath);
        var second = await store.ResolveExportAsync(trialId, relativePath);

        // Assert — {sha256hex(absolutePath)[..8]}-{lastWriteTimeUtc.Ticks:x}-{length}
        first.Should().NotBeNull();
        first!.Version.Should().MatchRegex(
            "^[0-9a-f]{8}-[0-9a-f]+-\\d+$",
            "the opaque version is 8 lowercase hex chars, hex ticks, then decimal length");
        second!.Version.Should().Be(first.Version, "an unchanged file must produce an identical version");
    }

    [Fact]
    public async Task ResolveExport_FileChanged_VersionChangesWithMtimeAndLength()
    {
        // Arrange — a resolvable workbook plus its initial version.
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        using var stream = TrialWorkbookFactory.CreateStream();
        var relativePath = await store.SaveAsync(trialId, "version changes.xlsx", stream);
        _createdFiles.Add((trialId, relativePath));
        var absolutePath = Path.Combine(_tempRoot, relativePath);

        var v1 = (await store.ResolveExportAsync(trialId, relativePath))!.Version;

        // Act 1 — touch only the mtime.
        File.SetLastWriteTimeUtc(absolutePath, DateTime.UtcNow.AddMinutes(3));
        var v2 = (await store.ResolveExportAsync(trialId, relativePath))!.Version;

        // Act 2 — grow the file by one byte, then RESTORE the mtime so ONLY length differs.
        var touchedMtime = File.GetLastWriteTimeUtc(absolutePath);
        using (var fs = new FileStream(absolutePath, FileMode.Append, FileAccess.Write))
        {
            fs.WriteByte(0);
        }
        File.SetLastWriteTimeUtc(absolutePath, touchedMtime);
        var v3 = (await store.ResolveExportAsync(trialId, relativePath))!.Version;

        // Assert — both stat components are part of the token (FileInfo only, no open needed)
        v2.Should().NotBe(v1, "the version embeds lastWriteTimeUtc.Ticks");
        v3.Should().NotBe(v2, "the version embeds the file length");
    }

    /// <summary>
    /// Registers a file written directly into the temp root (not via <see cref="SaveAsync"/>)
    /// so <see cref="DisposeAsync"/> removes it and any now-empty parent directories.
    /// </summary>
    private void TrackDirectFile(Guid trialId, string absolutePath)
    {
        _createdFiles.Add((trialId, Path.GetRelativePath(_tempRoot, absolutePath)));
    }
}
