using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Repository.Storage;

namespace PlottR0._2.Repository.Repositories;

/// <summary>
/// File-system implementation of <see cref="IVoiceBlobStore"/> with an optional
/// write-through durable object store. Blobs are cached under the <c>Recordings/</c>
/// directory of the configured root (the repo-root Service/ directory by default, or an
/// overridden root supplied via the constructor); when an <see cref="IObjectStore"/> is
/// supplied every write is mirrored to durable storage.
/// </summary>
public sealed class VoiceBlobStore : IVoiceBlobStore
{
    private readonly string? _baseDir;
    private readonly IObjectStore _objectStore;
    private readonly ILogger<VoiceBlobStore>? _logger;

    /// <summary>
    /// Creates a store rooted at <paramref name="baseDir"/> that mirrors its writes
    /// to <paramref name="objectStore"/>.
    /// </summary>
    /// <param name="baseDir">
    /// The root directory under which <c>Recordings/</c> is created.
    /// Pass <c>null</c> to fall back to <see cref="TrialWorkbookStore.ResolveServiceDirectory"/>.
    /// </param>
    /// <param name="objectStore">
    /// The durable object store to write through, or <c>null</c> for local-only behaviour.
    /// </param>
    /// <param name="logger">Optional logger for write-through diagnostics.</param>
    public VoiceBlobStore(
        string? baseDir,
        IObjectStore? objectStore,
        ILogger<VoiceBlobStore>? logger = null)
    {
        _baseDir = baseDir;
        _objectStore = objectStore ?? NullObjectStore.Instance;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> SaveAsync(Guid trialId, string clientRecordingId, string mimeType, Stream content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientRecordingId);

        if (content is null)
            throw new ArgumentNullException(nameof(content));

        // Sanitize: extract only the file-name component.
        var sanitized = Path.GetFileName(clientRecordingId);
        if (string.IsNullOrWhiteSpace(sanitized))
            throw new ArgumentException("Client recording id is empty after sanitization.", nameof(clientRecordingId));

        // Extension derived from the MIME type; unknown types fall back to .bin.
        var ext = mimeType switch
        {
            "audio/webm" => "webm",
            "audio/wav" => "wav",
            "audio/mp4" => "mp4",
            _ => "bin",
        };

        var fileName = $"{sanitized}.{ext}";

        // Resolve the Service/ directory — either the injected test root or the real one.
        var serviceDir = ResolveRoot();
        var targetDir = Path.Combine(serviceDir, "Recordings", trialId.ToString());
        Directory.CreateDirectory(targetDir);

        var targetPath = Path.Combine(targetDir, fileName);
        using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(fileStream, ct);
        }

        // Write-through: mirror the blob into durable object storage. A failed upload rolls the
        // local file back so a failed SaveAsync leaves no half-persisted artifact behind.
        try
        {
            await using var uploadStream = new FileStream(
                targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await _objectStore.PutAsync(ObjectKeys.RecordingKey(trialId, fileName), uploadStream, mimeType, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled: the local file is a valid, complete cache entry and must not
            // be rolled back, and a cancellation is not an upload failure worth logging.
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
                // Best-effort rollback — the upload failure is the important one.
            }

            throw;
        }

        // Return the relative path from the Service/ directory.
        return Path.Combine("Recordings", trialId.ToString(), fileName);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            var serviceDir = ResolveRoot();
            var absolutePath = Path.Combine(serviceDir, relativePath);

            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);

                // Also remove the parent directory if it's now empty.
                var parentDir = Path.GetDirectoryName(absolutePath);
                if (parentDir is not null && Directory.Exists(parentDir) && !Directory.EnumerateFileSystemEntries(parentDir).Any())
                {
                    Directory.Delete(parentDir);
                }
            }

            // Mirror the cleanup into durable storage: derive the trial id and file name from
            // the "Recordings/<trialId>/<file>" relative path and best-effort delete the object.
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
            // Best-effort cleanup — never throw; the caller's exception is the important one.
        }
    }

    /// <summary>
    /// Resolves the local cache root: the injected <c>baseDir</c> when supplied, otherwise the
    /// repo-root <c>Service/</c> directory via <see cref="TrialWorkbookStore.ResolveServiceDirectory"/>.
    /// </summary>
    private string ResolveRoot()
        => _baseDir ?? TrialWorkbookStore.ResolveServiceDirectory();
}
