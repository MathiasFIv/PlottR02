using PlottR0._2.Repository.Repositories;
using PlottR0._2.Tests.Support;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

/// <summary>
/// Pure unit tests for the durable write-through / cold-cache rehydration behaviour of
/// <see cref="TrialWorkbookStore"/> when an <see cref="InMemoryObjectStore"/> is supplied.
/// No Postgres, no Docker: each test gets an isolated temp cache root under
/// <see cref="Path.GetTempPath"/> which is deleted on disposal. The in-memory fake records
/// the exact object keys and operation counts so both the durable contract and the
/// warm-cache fast path can be asserted.
/// </summary>
public sealed class TrialWorkbookStoreObjectStoreTests : IDisposable
{
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly string _tempRoot;
    private readonly InMemoryObjectStore _store = new();

    public TrialWorkbookStoreObjectStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "PlottrWorkbookObjStore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private TrialWorkbookStore CreateStore() => new(_tempRoot, _store);

    [Fact]
    public async Task SaveAsync_PersistsLocallyAndUploadsUnderWorkbooksKey()
    {
        // Arrange
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "workbook.xlsx";
        var bytes = TrialWorkbookFactory.CreateBytes();
        using var content = new MemoryStream(bytes);

        // Act
        var relativePath = await store.SaveAsync(trialId, fileName, content);

        // Assert — local cache
        relativePath.Should().Be(Path.Combine("Working", trialId.ToString(), fileName));
        var localPath = Path.Combine(_tempRoot, relativePath);
        File.Exists(localPath).Should().BeTrue("the workbook must be written to the local cache");
        File.ReadAllBytes(localPath).Should().Equal(bytes);

        // Assert — durable write-through, exact key + byte round-trip + content type
        var expectedKey = $"Workbooks/{trialId}/{fileName}";
        _store.PutKeys.Should().ContainSingle().Which.Should().Be(expectedKey);
        _store.Contains(expectedKey).Should().BeTrue();
        _store.GetBytes(expectedKey).Should().Equal(bytes, "the uploaded bytes must round-trip unchanged");
        _store.GetContentType(expectedKey).Should().Be(XlsxContentType);
    }

    [Fact]
    public async Task SaveAsync_WhenUploadFails_RollsBackLocalFileAndPropagates()
    {
        // Arrange
        var store = CreateStore();
        _store.FailOnPut = true;
        var trialId = Guid.NewGuid();
        var fileName = "rollback.xlsx";
        using var content = new MemoryStream(TrialWorkbookFactory.CreateBytes());

        // Act
        var act = () => store.SaveAsync(trialId, fileName, content);

        // Assert — the upload failure propagates and the local copy is rolled back
        await act.Should().ThrowAsync<InvalidOperationException>();

        var localPath = Path.Combine(_tempRoot, "Working", trialId.ToString(), fileName);
        File.Exists(localPath).Should().BeFalse("a failed upload must roll back the local copy");
        _store.Contains($"Workbooks/{trialId}/{fileName}").Should().BeFalse();
    }

    [Fact]
    public async Task PersistTrialFilesAsync_UploadsOnlyOutputCopiesWhenSourceAlreadyStored()
    {
        // Arrange — a local source plus one accumulated output copy; the durable store
        // already holds the pristine source, so only the output copy is new.
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var workingDir = Path.Combine(_tempRoot, "Working", trialId.ToString());

        var sourceName = "source.xlsx";
        var outputName = "source_out_20260101_120000.xlsx";
        TrialWorkbookFactory.Create(Path.Combine(workingDir, sourceName));
        TrialWorkbookFactory.Create(Path.Combine(workingDir, outputName));

        var sourceKey = $"Workbooks/{trialId}/{sourceName}";
        var outputKey = $"Working/{trialId}/{outputName}";
        _store.Seed(sourceKey, TrialWorkbookFactory.CreateBytes());

        // Act
        await store.PersistTrialFilesAsync(trialId);

        // Assert — exactly the output copy was uploaded; the already-present source was skipped.
        _store.PutKeys.Should().BeEquivalentTo(new[] { outputKey });
        _store.Keys.Should().BeEquivalentTo(new[] { sourceKey, outputKey });
    }

    [Fact]
    public async Task ResolveExportAsync_ColdCache_RehydratesAndReturnsNewestOutputCopy()
    {
        // Arrange — a cold cache (no local working dir) with a seeded source and two outputs.
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var sourceName = "source.xlsx";
        var olderName = "source_out_20260101_000001.xlsx";
        var newerName = "source_out_20260202_000002.xlsx";

        var sourceBytes = TrialWorkbookFactory.CreateBytes();
        _store.Seed($"Workbooks/{trialId}/{sourceName}", sourceBytes);
        _store.Seed($"Working/{trialId}/{olderName}", TrialWorkbookFactory.CreateBytes());
        _store.Seed($"Working/{trialId}/{newerName}", TrialWorkbookFactory.CreateBytes());

        var sourceRelativePath = Path.Combine("Working", trialId.ToString(), sourceName);

        // Act
        var resolved = await store.ResolveExportAsync(trialId, sourceRelativePath);

        // Assert — source and both outputs rehydrated, newest _out_ stamp wins.
        var workingDir = Path.Combine(_tempRoot, "Working", trialId.ToString());
        File.Exists(Path.Combine(workingDir, sourceName)).Should().BeTrue("the source should be rehydrated");
        File.Exists(Path.Combine(workingDir, olderName)).Should().BeTrue();
        File.Exists(Path.Combine(workingDir, newerName)).Should().BeTrue();
        File.ReadAllBytes(Path.Combine(workingDir, sourceName)).Should().Equal(sourceBytes);

        resolved.Should().NotBeNull();
        resolved!.FileName.Should().Be(newerName, "the greatest _out_ timestamp must win");
        resolved.FilePath.Should().Be(Path.Combine(workingDir, newerName));

        _store.ExistsCalls.Should().Be(1, "only the source existence is probed before listing outputs");
        _store.ListKeysCalls.Should().Be(1);
        _store.GetToFileCalls.Should().Be(3, "one source plus two output copies");
    }

    [Fact]
    public async Task ResolveExportAsync_WarmCache_DoesNotTouchObjectStore()
    {
        // Arrange — the local working directory already holds an accumulated output copy, which
        // is what makes a cache genuinely warm (a pristine source alone is not enough).
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var outputName = "warm_out_20260101_000000.xlsx";
        TrialWorkbookFactory.Create(Path.Combine(_tempRoot, "Working", trialId.ToString(), outputName));

        var sourceRelativePath = Path.Combine("Working", trialId.ToString(), "warm.xlsx");

        // Act
        var resolved = await store.ResolveExportAsync(trialId, sourceRelativePath);

        // Assert — no network calls at all on the fast path
        resolved.Should().NotBeNull();
        resolved!.FileName.Should().Be(outputName);
        _store.ExistsCalls.Should().Be(0);
        _store.ListKeysCalls.Should().Be(0);
        _store.GetToFileCalls.Should().Be(0);
        _store.PutCalls.Should().Be(0);
        _store.DeleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task ResolveExportAsync_SourceOnlyLocalCache_RehydratesRemoteOutputs()
    {
        // Arrange — a local working dir holding ONLY the pristine source (so NOT a warm cache);
        // durable storage holds that source plus a newer accumulated output copy.
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var sourceName = "source.xlsx";
        var outputName = "source_out_20260303_101010.xlsx";
        var workingDir = Path.Combine(_tempRoot, "Working", trialId.ToString());
        TrialWorkbookFactory.Create(Path.Combine(workingDir, sourceName));

        _store.Seed($"Workbooks/{trialId}/{sourceName}", TrialWorkbookFactory.CreateBytes());
        _store.Seed($"Working/{trialId}/{outputName}", TrialWorkbookFactory.CreateBytes());

        var sourceRelativePath = Path.Combine("Working", trialId.ToString(), sourceName);

        // Act
        var resolved = await store.ResolveExportAsync(trialId, sourceRelativePath);

        // Assert — the remote output copy was downloaded locally and wins over the pristine source.
        File.Exists(Path.Combine(workingDir, outputName)).Should()
            .BeTrue("a source-only local cache must rehydrate the accumulated remote output copy");
        resolved.Should().NotBeNull();
        resolved!.FileName.Should().Be(outputName, "the newest output copy must be served, not the pristine source");
        resolved.FilePath.Should().Be(Path.Combine(workingDir, outputName));

        _store.GetToFileKeys.Should().Contain($"Working/{trialId}/{outputName}");
    }

    [Fact]
    public async Task ResolveExportAsync_AfterColdRehydrate_SecondReadUsesMarkerAndTouchesNoObjectStore()
    {
        // Arrange — a cold cache with a seeded source and no output copies.
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var sourceName = "source.xlsx";
        _store.Seed($"Workbooks/{trialId}/{sourceName}", TrialWorkbookFactory.CreateBytes());
        var sourceRelativePath = Path.Combine("Working", trialId.ToString(), sourceName);

        // Act — the first read rehydrates cold (source probe + output listing) and writes the marker.
        var first = await store.ResolveExportAsync(trialId, sourceRelativePath);
        first.Should().NotBeNull();

        _store.ResetCounters();

        // Act — the second read hits the rehydration marker: zero object-store calls.
        var second = await store.ResolveExportAsync(trialId, sourceRelativePath);

        // Assert — the marker kept this filesystem warm and the same export is returned.
        second.Should().NotBeNull();
        second!.FileName.Should().Be(first!.FileName);
        second.FilePath.Should().Be(first.FilePath);
        _store.ExistsCalls.Should().Be(0, "the rehydration marker marks this filesystem warm");
        _store.ListKeysCalls.Should().Be(0);
        _store.GetToFileCalls.Should().Be(0);
    }

    [Fact]
    public async Task DeleteAsync_SourceFile_RemovesLocalFileAndSourceObject()
    {
        // Arrange
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var fileName = "to-delete.xlsx";
        using var content = new MemoryStream(TrialWorkbookFactory.CreateBytes());
        var relativePath = await store.SaveAsync(trialId, fileName, content);

        var localPath = Path.Combine(_tempRoot, relativePath);
        var key = $"Workbooks/{trialId}/{fileName}";
        File.Exists(localPath).Should().BeTrue();
        _store.Contains(key).Should().BeTrue();

        // Act
        await store.DeleteAsync(relativePath);

        // Assert — both the cache file and the mapped source object are gone
        File.Exists(localPath).Should().BeFalse("the local cache file must be removed");
        _store.Contains(key).Should().BeFalse("the durable source object must be removed");
        _store.DeleteKeys.Should().Contain(key);
    }

    [Fact]
    public async Task DeleteAsync_OutputCopy_RemovesLocalFileAndOutputObject()
    {
        // Arrange — an output copy mapped to the Working/ namespace.
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var outputName = "src_out_20260101_000001.xlsx";
        var localPath = TrialWorkbookFactory.Create(
            Path.Combine(_tempRoot, "Working", trialId.ToString(), outputName));

        var key = $"Working/{trialId}/{outputName}";
        _store.Seed(key, TrialWorkbookFactory.CreateBytes());
        var relativePath = Path.Combine("Working", trialId.ToString(), outputName);

        // Act
        await store.DeleteAsync(relativePath);

        // Assert
        File.Exists(localPath).Should().BeFalse("the local cache file must be removed");
        _store.Contains(key).Should().BeFalse("the durable output object must be removed");
        _store.DeleteKeys.Should().Contain(key);
    }
}
