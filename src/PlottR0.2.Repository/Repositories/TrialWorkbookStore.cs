using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Repositories;

namespace PlottR0._2.Repository.Repositories;

/// <summary>
/// File-system implementation of <see cref="ITrialWorkbookStore"/>.
/// Stores uploaded trial workbooks under the repo-root Service/Working/ directory
/// (or an overridden root supplied via the constructor).
/// Uses ClosedXML to validate that uploaded content is a real xlsx workbook before persisting.
/// </summary>
public sealed class TrialWorkbookStore : ITrialWorkbookStore
{
    private readonly string? _baseDir;

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
    public TrialWorkbookStore(string? baseDir) => _baseDir = baseDir;

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
        var serviceDir = _baseDir ?? ResolveServiceDirectory();
        var targetDir = Path.Combine(serviceDir, "Working", trialId.ToString());
        Directory.CreateDirectory(targetDir);

        var targetPath = Path.Combine(targetDir, sanitized);
        memoryStream.Position = 0;
        using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await memoryStream.CopyToAsync(fileStream, ct);
        }

        // Return the relative path from the Service/ directory.
        return Path.Combine("Working", trialId.ToString(), sanitized);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            var serviceDir = _baseDir ?? ResolveServiceDirectory();
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

    /// <inheritdoc />
    public ResolvedWorkbookExport? ResolveExport(Guid trialId, string sourceRelativePath)
    {
        var serviceDir = _baseDir ?? ResolveServiceDirectory();
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
