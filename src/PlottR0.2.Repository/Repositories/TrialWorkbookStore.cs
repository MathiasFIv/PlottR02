using ClosedXML.Excel;
using PlottR0._2.Contracts.Repositories;

namespace PlottR0._2.Repository.Repositories;

/// <summary>
/// File-system implementation of <see cref="ITrialWorkbookStore"/>.
/// Stores uploaded trial workbooks under the repo-root Service/Working/ directory.
/// Uses ClosedXML to validate that uploaded content is a real xlsx workbook before persisting.
/// </summary>
public sealed class TrialWorkbookStore : ITrialWorkbookStore
{
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

        // Resolve the Service/ directory using the same walk-up logic as ClosedXmlRatingRepository.
        var serviceDir = ResolveServiceDirectory();
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
            var serviceDir = ResolveServiceDirectory();
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
