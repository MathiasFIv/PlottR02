namespace PlottR0._2.Contracts.Repositories;

/// <summary>
/// Stores uploaded voice recording blobs on disk. Resides in the Repository layer so
/// Services/Api stay file-system-agnostic. Relative paths are relative to the
/// repo-root Service/ directory.
/// </summary>
public interface IVoiceBlobStore
{
    /// <summary>
    /// Saves recording bytes for a trial under Service/Recordings/trialId/ and
    /// returns the RELATIVE path (e.g. "Recordings/trialId/clientId.webm").
    /// </summary>
    Task<string> SaveAsync(Guid trialId, string clientRecordingId, string mimeType, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Best-effort deletes a previously saved recording blob file.
    /// Used for cleanup when a downstream operation (e.g. DB insert) fails
    /// after the file was already written.
    /// </summary>
    /// <param name="relativePath">The relative path returned by <see cref="SaveAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string relativePath, CancellationToken ct = default);
}
