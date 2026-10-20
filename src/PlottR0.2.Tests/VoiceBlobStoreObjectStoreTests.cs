using PlottR0._2.Repository.Repositories;
using PlottR0._2.Tests.Support;

namespace PlottR0._2.Tests;

public sealed class VoiceBlobStoreObjectStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly InMemoryObjectStore _store = new();

    public VoiceBlobStoreObjectStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "PlottrVoiceObjStore_" + Guid.NewGuid().ToString("N"));
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
        }
    }

    private VoiceBlobStore CreateStore() => new(_tempRoot, _store);

    [Fact]
    public async Task SaveAsync_PersistsLocallyAndUploadsUnderRecordingsKey()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        var bytes = "voice-bytes"u8.ToArray();
        using var content = new MemoryStream(bytes);

        var relativePath = await store.SaveAsync(trialId, "rec-1", "audio/webm", content);

        var expectedName = "rec-1.webm";
        relativePath.Should().Be(Path.Combine("Recordings", trialId.ToString(), expectedName));

        var localPath = Path.Combine(_tempRoot, relativePath);
        File.Exists(localPath).Should().BeTrue("the blob must be written to the local cache");
        File.ReadAllBytes(localPath).Should().Equal(bytes);

        var expectedKey = $"recordings/{trialId}/{expectedName}";
        _store.PutKeys.Should().ContainSingle().Which.Should().Be(expectedKey);
        _store.GetBytes(expectedKey).Should().Equal(bytes);
        _store.GetContentType(expectedKey).Should().Be("audio/webm");
    }

    [Theory]
    [InlineData("audio/webm", "webm")]
    [InlineData("audio/wav", "wav")]
    [InlineData("audio/mp4", "mp4")]
    [InlineData("audio/ogg", "bin")]
    [InlineData("", "bin")]
    public async Task SaveAsync_DerivesFileNameExtensionFromMimeType(string mimeType, string expectedExtension)
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        using var content = new MemoryStream("x"u8.ToArray());

        var relativePath = await store.SaveAsync(trialId, "rec-mime", mimeType, content);

        var expectedName = $"rec-mime.{expectedExtension}";
        relativePath.Should().EndWith(expectedName);
        _store.Contains($"recordings/{trialId}/{expectedName}").Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_WhenUploadFails_RollsBackLocalFileAndPropagates()
    {
        var store = CreateStore();
        _store.FailOnPut = true;
        var trialId = Guid.NewGuid();
        using var content = new MemoryStream("voice"u8.ToArray());

        var act = () => store.SaveAsync(trialId, "rec-rollback", "audio/webm", content);

        await act.Should().ThrowAsync<InvalidOperationException>();

        var localPath = Path.Combine(_tempRoot, "Recordings", trialId.ToString(), "rec-rollback.webm");
        File.Exists(localPath).Should().BeFalse("a failed upload must roll back the local blob");
        _store.Contains($"recordings/{trialId}/rec-rollback.webm").Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_RemovesLocalFileAndRecordingObject()
    {
        var store = CreateStore();
        var trialId = Guid.NewGuid();
        using var content = new MemoryStream("voice"u8.ToArray());
        var relativePath = await store.SaveAsync(trialId, "rec-del", "audio/wav", content);

        var localPath = Path.Combine(_tempRoot, relativePath);
        var key = $"recordings/{trialId}/rec-del.wav";
        File.Exists(localPath).Should().BeTrue();
        _store.Contains(key).Should().BeTrue();

        await store.DeleteAsync(relativePath);

        File.Exists(localPath).Should().BeFalse("the local cache file must be removed");
        _store.Contains(key).Should().BeFalse("the durable recording object must be removed");
        _store.DeleteKeys.Should().Contain(key);
    }
}
