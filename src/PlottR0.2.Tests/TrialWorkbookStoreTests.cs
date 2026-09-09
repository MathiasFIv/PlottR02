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

    private TrialWorkbookStore CreateStore() => new(_tempRoot);

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
}
