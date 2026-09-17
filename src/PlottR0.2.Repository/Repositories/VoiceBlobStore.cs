using PlottR0._2.Contracts.Repositories;

namespace PlottR0._2.Repository.Repositories;

/// <summary>
/// File-system implementation of <see cref="IVoiceBlobStore"/>.
/// Stores uploaded voice recording blobs under the repo-root Service/Recordings/ directory
/// (or an overridden root supplied via the constructor).
/// </summary>
public sealed class VoiceBlobStore : IVoiceBlobStore
{
    private readonly string? _baseDir;

    /// <summary>
    /// Creates a store that uses the default repo-root <c>Service/</c> directory.
    /// </summary>
    public VoiceBlobStore() : this(null) { }

    /// <summary>
    /// Creates a store rooted at <paramref name="baseDir"/> (used by tests to
    /// avoid touching the shared workspace).
    /// </summary>
    /// <param name="baseDir">
    /// The root directory under which <c>Recordings/</c> is created.
    /// Pass <c>null</c> to fall back to <see cref="TrialWorkbookStore.ResolveServiceDirectory"/>.
    /// </param>
    public VoiceBlobStore(string? baseDir) => _baseDir = baseDir;

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
        var serviceDir = _baseDir ?? TrialWorkbookStore.ResolveServiceDirectory();
        var targetDir = Path.Combine(serviceDir, "Recordings", trialId.ToString());
        Directory.CreateDirectory(targetDir);

        var targetPath = Path.Combine(targetDir, fileName);
        using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(fileStream, ct);
        }

        // Return the relative path from the Service/ directory.
        return Path.Combine("Recordings", trialId.ToString(), fileName);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            var serviceDir = _baseDir ?? TrialWorkbookStore.ResolveServiceDirectory();
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
        }
        catch
        {
            // Best-effort cleanup — never throw; the caller's exception is the important one.
        }

        return Task.CompletedTask;
    }
}
