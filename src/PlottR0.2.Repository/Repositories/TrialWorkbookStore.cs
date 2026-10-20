using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Repository.Storage;

namespace PlottR0._2.Repository.Repositories;

public sealed class TrialWorkbookStore : ITrialWorkbookStore
{
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // Sentinel whose name carries no ".xlsx" extension, so output-copy scans ignore it.
    private const string RehydratedMarkerName = ".rehydrated";

    private readonly string? _baseDir;
    private readonly IObjectStore _objectStore;
    private readonly ILogger<TrialWorkbookStore>? _logger;

    public TrialWorkbookStore() : this(null, null) { }

    public TrialWorkbookStore(
        string? baseDir,
        IObjectStore? objectStore,
        ILogger<TrialWorkbookStore>? logger = null)
    {
        _baseDir = baseDir;
        _objectStore = objectStore ?? NullObjectStore.Instance;
        _logger = logger;
    }

    /// <summary>
    /// Validates the upload, stores it under Working/{trialId} and mirrors it to object storage. A
    /// cancelled upload keeps the local file — it is a valid, complete cache entry, so it must not
    /// be rolled back; any other upload failure does roll the local copy back.
    /// </summary>
    public async Task<string> SaveAsync(Guid trialId, string fileName, Stream content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (content is null)
            throw new ArgumentNullException(nameof(content));

        var sanitized = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(sanitized))
            throw new ArgumentException("File name is empty after sanitization.", nameof(fileName));

        var ext = Path.GetExtension(sanitized);
        if (!string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"File must have an .xlsx extension (got '{ext}').", nameof(fileName));

        using var memoryStream = new MemoryStream();
        await content.CopyToAsync(memoryStream, ct);

        if (memoryStream.Length == 0)
            throw new ArgumentException("File content is empty.", nameof(content));

        memoryStream.Position = 0;
        try
        {
            using var _ = new XLWorkbook(memoryStream);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileFormatException)
        {
            throw new InvalidDataException("Not a valid xlsx workbook.", ex);
        }

        var serviceDir = ResolveRoot();
        var targetDir = Path.Combine(serviceDir, "Working", trialId.ToString());
        Directory.CreateDirectory(targetDir);

        var targetPath = Path.Combine(targetDir, sanitized);
        memoryStream.Position = 0;
        using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await memoryStream.CopyToAsync(fileStream, ct);
        }

        try
        {
            await using var uploadStream = new FileStream(
                targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await _objectStore.PutAsync(ObjectKeys.WorkbookSourceKey(trialId, sanitized), uploadStream, XlsxContentType, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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
            }

            throw;
        }

        return Path.Combine("Working", trialId.ToString(), sanitized);
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
        }
    }

    // Prefers the newest accumulated output copy (every write starts from it, so it holds the most
    // edits) and falls back to the pristine source. The relative source path must resolve
    // inside the Service/ root, even if a crafted SourceWorkbookPath ever reaches this store.
    private ResolvedWorkbookExport? ResolveExport(Guid trialId, string sourceRelativePath)
    {
        var serviceDir = ResolveRoot();
        var workingDir = Path.Combine(serviceDir, "Working", trialId.ToString());

        var outputCopy = TryGetNewestOutputCopy(workingDir);

        string? filePath;
        if (outputCopy is not null)
        {
            filePath = outputCopy;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(sourceRelativePath))
            {
                return null;
            }

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

        var version = ComputeVersion(filePath);
        return new ResolvedWorkbookExport(filePath, Path.GetFileName(filePath), version);
    }

    public async Task<ResolvedWorkbookExport?> ResolveExportAsync(
        Guid trialId,
        string sourceRelativePath,
        CancellationToken ct = default)
    {
        await EnsureRehydratedAsync(trialId, sourceRelativePath, ct);
        return ResolveExport(trialId, sourceRelativePath);
    }

    public string GetWorkingDirectory(Guid trialId)
        => Path.Combine(ResolveRoot(), "Working", trialId.ToString());

    public async Task PersistTrialFilesAsync(Guid trialId, CancellationToken ct = default)
    {
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

            if (await _objectStore.ExistsAsync(key, ct))
                continue;

            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            await _objectStore.PutAsync(key, stream, XlsxContentType, ct);
        }
    }

    // Warm cache served with zero network calls: only the marker or a cached output copy short-circuits
    // (a pristine source alone is not proof a sync ran). Otherwise the source and every output copy are
    // pulled once and a best-effort marker is written. No-op when durable storage is disabled.
    private async Task EnsureRehydratedAsync(Guid trialId, string sourceRelativePath, CancellationToken ct)
    {
        if (ReferenceEquals(_objectStore, NullObjectStore.Instance))
            return;

        var workingDir = GetWorkingDirectory(trialId);

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

        try
        {
            File.WriteAllBytes(Path.Combine(workingDir, RehydratedMarkerName), []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Opaque version string derived from a stat (path hash, write time, length); the file itself is
    // never opened here.
    private static string ComputeVersion(string absolutePath)
    {
        var info = new FileInfo(absolutePath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(absolutePath));
        var prefix = Convert.ToHexString(hash).ToLowerInvariant()[..8];
        return $"{prefix}-{info.LastWriteTimeUtc.Ticks:x}-{info.Length}";
    }

    // Ranked by the stamp embedded after the LAST "_out_" marker — second precision for names
    // written before millisecond stamps existed (including chained ones), millisecond precision
    // for current copies — so a same-second millisecond copy outranks its second-precision
    // neighbour. A tie is broken by the longer name, i.e. an older chained copy that carried more
    // stamps.
    private static string? TryGetNewestOutputCopy(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
        {
            return null;
        }

        string? newestPath = null;
        DateTime newestStamp = default;
        var marker = ObjectKeys.OutputMarker;
        foreach (var file in Directory.GetFiles(outputDirectory, "*.xlsx"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var markerIndex = name.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var stamp = name[(markerIndex + marker.Length)..];
            if (!DateTime.TryParseExact(
                    stamp,
                    ObjectKeys.OutputStampFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedStamp))
            {
                continue;
            }

            var isNewest = newestPath is null
                || parsedStamp > newestStamp
                || (parsedStamp == newestStamp
                    && name.Length > Path.GetFileNameWithoutExtension(newestPath).Length);

            if (isNewest)
            {
                newestStamp = parsedStamp;
                newestPath = file;
            }
        }

        return newestPath;
    }

    private string ResolveRoot() => _baseDir ?? ResolveServiceDirectory();

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

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Service"));
    }
}
