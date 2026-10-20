using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Repository.Storage;

namespace PlottR0._2.Repository.Repositories;

public sealed class VoiceBlobStore : IVoiceBlobStore
{
    private readonly string? _baseDir;
    private readonly IObjectStore _objectStore;
    private readonly ILogger<VoiceBlobStore>? _logger;

    public VoiceBlobStore(
        string? baseDir,
        IObjectStore? objectStore,
        ILogger<VoiceBlobStore>? logger = null)
    {
        _baseDir = baseDir;
        _objectStore = objectStore ?? NullObjectStore.Instance;
        _logger = logger;
    }

    /// <summary>
    /// Stores the audio under Recordings/{trialId} and mirrors it to object storage; a cancelled
    /// upload keeps the local file (a valid, complete cache entry) while any other failure rolls it back.
    /// </summary>
    public async Task<string> SaveAsync(Guid trialId, string clientRecordingId, string mimeType, Stream content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientRecordingId);

        if (content is null)
            throw new ArgumentNullException(nameof(content));

        var sanitized = Path.GetFileName(clientRecordingId);
        if (string.IsNullOrWhiteSpace(sanitized))
            throw new ArgumentException("Client recording id is empty after sanitization.", nameof(clientRecordingId));

        var ext = mimeType switch
        {
            "audio/webm" => "webm",
            "audio/wav" => "wav",
            "audio/mp4" => "mp4",
            _ => "bin",
        };

        var fileName = $"{sanitized}.{ext}";

        var serviceDir = ResolveRoot();
        var targetDir = Path.Combine(serviceDir, "Recordings", trialId.ToString());
        Directory.CreateDirectory(targetDir);

        var targetPath = Path.Combine(targetDir, fileName);
        using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(fileStream, ct);
        }

        try
        {
            await using var uploadStream = new FileStream(
                targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await _objectStore.PutAsync(ObjectKeys.RecordingKey(trialId, fileName), uploadStream, mimeType, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Failed to upload recording '{File}' for trial {TrialId} to durable storage; rolling back the local copy.",
                fileName, trialId);

            try
            {
                if (File.Exists(targetPath))
                    File.Delete(targetPath);
            }
            catch
            {
            }

            throw;
        }

        return Path.Combine("Recordings", trialId.ToString(), fileName);
    }

    public async Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            var serviceDir = ResolveRoot();
            var absolutePath = Path.Combine(serviceDir, relativePath);

            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);

                var parentDir = Path.GetDirectoryName(absolutePath);
                if (parentDir is not null && Directory.Exists(parentDir) && !Directory.EnumerateFileSystemEntries(parentDir).Any())
                {
                    Directory.Delete(parentDir);
                }
            }

            var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3
                && segments[0].Equals("Recordings", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(segments[1], out var trialId))
            {
                await _objectStore.DeleteAsync(ObjectKeys.RecordingKey(trialId, segments[^1]), ct);
            }
        }
        catch
        {
        }
    }

    private string ResolveRoot()
        => _baseDir ?? TrialWorkbookStore.ResolveServiceDirectory();
}
