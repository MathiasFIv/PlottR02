using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Repository.Storage;

namespace PlottR0._2.Repository.Repositories;

/// <summary>
/// File-system implementation of <see cref="ITrialWorkbookStore"/> with an optional
/// write-through durable object store. Workbooks are cached under the
/// <c>Working/</c> directory of the configured root (the repo-root Service/ directory
/// by default, or an overridden root supplied via the constructor); when an
/// <see cref="IObjectStore"/> is supplied every write is mirrored to durable storage
/// and a cold cache is rehydrated on read.
/// Uses ClosedXML to validate that uploaded content is a real xlsx workbook before persisting.
/// </summary>
public sealed class TrialWorkbookStore : ITrialWorkbookStore
{
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// Sentinel written into a trial's working directory after a successful rehydration so
    /// subsequent reads take the zero-network fast path. Not a <c>.xlsx</c>, so the workbook
    /// scans (<see cref="PersistTrialFilesAsync"/> and <see cref="ResolveExport"/>) ignore it.
    /// </summary>
    private const string RehydratedMarkerName = ".rehydrated";

    private readonly string? _baseDir;
    private readonly IObjectStore _objectStore;
    private readonly ILogger<TrialWorkbookStore>? _logger;

    /// <summary>
    /// Creates a store that uses the default repo-root <c>Service/</c> directory.
    /// </summary>
    public TrialWorkbookStore() : this(null) { }

    /// <summary>
    /// Creates a store rooted at <paramref name="baseDir"/> (used by tests to
    /// avoid touching the shared workspace).
    /// </summary>
    /// <param name="baseDir">
    /// The root directory under which <c>Working/</c> is created.
    /// Pass <c>null</c> to fall back to <see cref="ResolveServiceDirectory"/>.
    /// </param>
    public TrialWorkbookStore(string? baseDir) : this(baseDir, null) { }

    /// <summary>
    /// Creates a store rooted at <paramref name="baseDir"/> that mirrors its writes
    /// to <paramref name="objectStore"/> and rehydrates cold reads from it.
    /// </summary>
    /// <param name="baseDir">
    /// The root directory under which <c>Working/</c> is created.
    /// Pass <c>null</c> to fall back to <see cref="ResolveServiceDirectory"/>.
    /// </param>
    /// <param name="objectStore">
    /// The durable object store to write through, or <c>null</c> for local-only behaviour.
    /// </param>
    /// <param name="logger">Optional logger for write-through diagnostics.</param>
    public TrialWorkbookStore(
        string? baseDir,
        IObjectStore? objectStore,
        ILogger<TrialWorkbookStore>? logger = null)
    {
        _baseDir = baseDir;
        _objectStore = objectStore ?? NullObjectStore.Instance;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> SaveAsync(Guid trialId, string fileName, Stream content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (content is null)
            throw new ArgumentNullException(nameof(content));

        // Sanitize: extract only the file name component.
        var sanitized = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(sanitized))
            throw new ArgumentException("File name is empty after sanitization.", nameof(fileName));

        // Extension must be .xlsx (case-insensitive).
        var ext = Path.GetExtension(sanitized);
        if (!string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"File must have an .xlsx extension (got '{ext}').", nameof(fileName));

        // Copy to a MemoryStream so we can validate THEN persist without rewinding issues.
        using var memoryStream = new MemoryStream();
        await content.CopyToAsync(memoryStream, ct);

        if (memoryStream.Length == 0)
            throw new ArgumentException("File content is empty.", nameof(content));

        // Validate: opening with ClosedXML proves it's a real workbook.
        // Catch only plausible corruption exceptions — let OutOfMemoryException etc. propagate.
        memoryStream.Position = 0;
        try
        {
            using var _ = new XLWorkbook(memoryStream);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileFormatException)
        {
            throw new InvalidDataException("Not a valid xlsx workbook.", ex);
        }

        // Resolve the Service/ directory — either the injected test root or the real one.
        var serviceDir = ResolveRoot();
        var targetDir = Path.Combine(serviceDir, "Working", trialId.ToString());
        Directory.CreateDirectory(targetDir);

        var targetPath = Path.Combine(targetDir, sanitized);
        memoryStream.Position = 0;
        using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await memoryStream.CopyToAsync(fileStream, ct);
        }

        // Write-through: mirror the persisted workbook into durable object storage. The local
        // copy remains the read cache; a failed upload rolls the local file back so a failed
        // SaveAsync leaves no half-persisted artifact behind.
        try
        {
            await using var uploadStream = new FileStream(
                targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await _objectStore.PutAsync(ObjectKeys.WorkbookSourceKey(trialId, sanitized), uploadStream, XlsxContentType, ct);
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
                "Failed to upload workbook '{File}' for trial {TrialId} to durable storage; rolling back the local copy.",
                sanitized, trialId);

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
        return Path.Combine("Working", trialId.ToString(), sanitized);
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
            // the "Working/<trialId>/<file>" relative path and best-effort delete the object.
            var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3
                && segments[0].Equals("Working", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(segments[1], out var trialId))
            {
                var fileName = segments[^1];
                var key = ObjectKeys.IsOutputCopy(fileName)
                    ? ObjectKeys.WorkbookOutputKey(trialId, fileName)
                    : ObjectKeys.WorkbookSourceKey(trialId, fileName);
                await _objectStore.DeleteAsync(key, ct);
            }
        }
        catch
        {
            // Best-effort cleanup — never throw; the caller's exception is the important one.
        }
    }

    /// <inheritdoc />
    public ResolvedWorkbookExport? ResolveExport(Guid trialId, string sourceRelativePath)
    {
        var serviceDir = ResolveRoot();
        var workingDir = Path.Combine(serviceDir, "Working", trialId.ToString());

        // Prefer the newest accumulated sync output (if any): successive syncs chain on
        // earlier copies, so the newest *_out_*.xlsx is the most complete representation.
        var outputCopy = TryGetNewestOutputCopy(workingDir);

        string? filePath;
        if (outputCopy is not null)
        {
            filePath = outputCopy;
        }
        else
        {
            // No sync output yet — fall back to the pristine uploaded workbook.
            if (string.IsNullOrWhiteSpace(sourceRelativePath))
            {
                return null;
            }

            // Defense-in-depth: the persisted relative path must stay inside the Service/ root,
            // even if a crafted SourceWorkbookPath ever reaches this store.
            var fullSource = Path.GetFullPath(Path.Combine(serviceDir, sourceRelativePath));
            var fullBase = Path.GetFullPath(serviceDir);
            if (!fullSource.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            filePath = fullSource;
        }

        if (!File.Exists(filePath))
        {
            return null;
        }

        // Version computed in the SAME call as the path (no path/version TOCTOU) — FileInfo
        // stat only, the file is never opened here (plan §3).
        var version = ComputeVersion(filePath);
        return new ResolvedWorkbookExport(filePath, Path.GetFileName(filePath), version);
    }

    /// <inheritdoc />
    public async Task<ResolvedWorkbookExport?> ResolveExportAsync(
        Guid trialId,
        string sourceRelativePath,
        CancellationToken ct = default)
    {
        await EnsureRehydratedAsync(trialId, sourceRelativePath, ct);
        return ResolveExport(trialId, sourceRelativePath);
    }

    /// <inheritdoc />
    public string GetWorkingDirectory(Guid trialId)
        => Path.Combine(ResolveRoot(), "Working", trialId.ToString());

    /// <inheritdoc />
    public async Task PersistTrialFilesAsync(Guid trialId, CancellationToken ct = default)
    {
        // No durable backend: nothing to persist.
        if (ReferenceEquals(_objectStore, NullObjectStore.Instance))
            return;

        var workingDir = GetWorkingDirectory(trialId);
        if (!Directory.Exists(workingDir))
            return;

        foreach (var file in Directory.GetFiles(workingDir, "*.xlsx"))
        {
            var fileName = Path.GetFileName(file);
            var key = ObjectKeys.IsOutputCopy(fileName)
                ? ObjectKeys.WorkbookOutputKey(trialId, fileName)
                : ObjectKeys.WorkbookSourceKey(trialId, fileName);

            // Uploads every local trial workbook file whose durable key does not yet exist
            // (the source when it is not yet durable, and/or accumulated output copies) —
            // successive syncs re-run this and must not re-upload unchanged artifacts.
            if (await _objectStore.ExistsAsync(key, ct))
                continue;

            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            await _objectStore.PutAsync(key, stream, XlsxContentType, ct);
        }
    }

    /// <summary>
    /// Ensures the trial's working directory is populated before a read. A warm cache is served
    /// with ZERO network calls: either a rehydration already ran on this filesystem (the
    /// <see cref="RehydratedMarkerName"/> marker is present) or at least one accumulated output
    /// copy is already cached locally (proof a sync ran here). A pristine source alone does NOT
    /// count as warm — such a cache rehydrates the pristine source (when present remotely) and
    /// every accumulated output copy so a subsequent sync chains on the newest output. After a
    /// successful rehydration a best-effort marker file is written so the fast path holds for
    /// later reads. No-op when durable storage is disabled (and no marker is ever written).
    /// </summary>
    private async Task EnsureRehydratedAsync(Guid trialId, string sourceRelativePath, CancellationToken ct)
    {
        if (ReferenceEquals(_objectStore, NullObjectStore.Instance))
            return;

        var workingDir = GetWorkingDirectory(trialId);

        // Warm cache — already hydrated on this filesystem, or a synced output copy is present.
        // A pristine source alone is not proof of a sync, so it must not short-circuit here.
        if (File.Exists(Path.Combine(workingDir, RehydratedMarkerName))
            || (Directory.Exists(workingDir)
                && Directory.GetFiles(workingDir, "*.xlsx").Any(f => ObjectKeys.IsOutputCopy(Path.GetFileName(f)))))
        {
            return;
        }

        var sourceName = string.IsNullOrWhiteSpace(sourceRelativePath)
            ? string.Empty
            : Path.GetFileName(sourceRelativePath);

        Directory.CreateDirectory(workingDir);

        // Rehydrate the pristine source workbook when it is missing locally but durable remotely.
        if (!string.IsNullOrEmpty(sourceName))
        {
            var localSource = Path.Combine(workingDir, sourceName);
            if (!File.Exists(localSource))
            {
                var sourceKey = ObjectKeys.WorkbookSourceKey(trialId, sourceName);
                if (await _objectStore.ExistsAsync(sourceKey, ct))
                    await _objectStore.GetToFileAsync(sourceKey, localSource, ct);
            }
        }

        // Rehydrate every accumulated output copy so the policy write chains on prior state.
        var outputKeys = await _objectStore.ListKeysAsync(ObjectKeys.WorkbookOutputPrefix(trialId), ct);
        foreach (var key in outputKeys)
        {
            var fileName = key[(key.LastIndexOf('/') + 1)..];
            if (string.IsNullOrEmpty(fileName))
                continue;

            var destination = Path.Combine(workingDir, fileName);
            if (File.Exists(destination))
                continue;

            await _objectStore.GetToFileAsync(key, destination, ct);
        }

        // Best-effort marker: marks this filesystem warm so later reads skip all network I/O.
        // A failed marker write only costs an extra rehydration on the next read.
        try
        {
            File.WriteAllBytes(Path.Combine(workingDir, RehydratedMarkerName), []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Non-fatal — the rehydrated files are what matter.
        }
    }

    /// <summary>
    /// Opaque version token: <c>{sha256hex(absolutePath)[..8]}-{lastWriteTimeUtc.Ticks:x}-{length}</c>.
    /// Derived from <see cref="FileInfo"/> only — resolution stays a stat (the workbook is
    /// never opened), and any mtime or length change flips the token.
    /// </summary>
    private static string ComputeVersion(string absolutePath)
    {
        var info = new FileInfo(absolutePath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(absolutePath));
        var prefix = Convert.ToHexString(hash).ToLowerInvariant()[..8];
        return $"{prefix}-{info.LastWriteTimeUtc.Ticks:x}-{info.Length}";
    }

    /// <summary>
    /// Returns the newest existing output copy in the trial's working directory — the file
    /// whose name contains <c>_out_yyyyMMdd_HHmmss.xlsx</c> with the greatest timestamp — or
    /// <see langword="null"/> when no copy exists yet. The <see langword="last"/> <c>_out_</c>
    /// marker is used, so a chained copy (one created from another output copy) parses to its
    /// own newest stamp. Ported from <c>RecordingSyncService.TryGetNewestOutputCopy</c> so the
    /// exported workbook matches the copy a subsequent sync would read from.
    /// </summary>
    private static string? TryGetNewestOutputCopy(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
        {
            return null;
        }

        string? newestPath = null;
        DateTime newestStamp = default;
        const string marker = "_out_";
        foreach (var file in Directory.GetFiles(outputDirectory, "*.xlsx"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var markerIndex = name.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var stamp = name[(markerIndex + marker.Length)..];
            if (DateTime.TryParseExact(
                    stamp,
                    "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedStamp)
                && (newestPath is null || parsedStamp > newestStamp))
            {
                newestStamp = parsedStamp;
                newestPath = file;
            }
        }

        return newestPath;
    }

    /// <summary>
    /// Resolves the local cache root: the injected <c>baseDir</c> when supplied, otherwise the
    /// repo-root <c>Service/</c> directory via <see cref="ResolveServiceDirectory"/>.
    /// </summary>
    private string ResolveRoot() => _baseDir ?? ResolveServiceDirectory();

    /// <summary>
    /// Resolves the repo-root Service/ directory by walking up from AppContext.BaseDirectory,
    /// mirroring the approach in <see cref="ClosedXmlRatingRepository"/>.
    /// </summary>
    public static string ResolveServiceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Service")))
            {
                return Path.Combine(dir.FullName, "Service");
            }

            dir = dir.Parent;
        }

        // Fallback: resolve relative to the current working directory.
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Service"));
    }
}
