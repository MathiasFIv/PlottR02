namespace PlottR0._2.Repository.Storage;

/// <summary>
/// Abstraction over an S3-compatible object store used as the durable backend
/// behind the local file-system cache. Keys are forward-slash separated logical
/// paths (e.g. <c>Workbooks/{trialId}/doc.xlsx</c>) — never local filesystem paths.
/// The implementation is expected to be safe for concurrent use.
/// </summary>
public interface IObjectStore
{
    /// <summary>
    /// Uploads <paramref name="content"/> to <paramref name="key"/>, overwriting any
    /// existing object. <paramref name="contentType"/> is stored as the object's
    /// content type when non-null. The caller retains ownership of the stream.
    /// </summary>
    /// <param name="key">The logical object key.</param>
    /// <param name="content">The readable stream to upload.</param>
    /// <param name="contentType">The MIME type to record for the object, or <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PutAsync(string key, Stream content, string? contentType, CancellationToken ct = default);

    /// <summary>
    /// Returns <see langword="true"/> when an object exists at <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The logical object key.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Downloads the object at <paramref name="key"/> to <paramref name="destinationPath"/>,
    /// creating parent directories as needed. Throws <see cref="FileNotFoundException"/>
    /// when the object does not exist.
    /// </summary>
    /// <param name="key">The logical object key.</param>
    /// <param name="destinationPath">The absolute local path to write the bytes to.</param>
    /// <param name="ct">Cancellation token.</param>
    Task GetToFileAsync(string key, string destinationPath, CancellationToken ct = default);

    /// <summary>
    /// Lists every object key beginning with <paramref name="prefix"/>, following pagination.
    /// </summary>
    /// <param name="prefix">The key prefix to match.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct = default);

    /// <summary>
    /// Deletes the object at <paramref name="key"/>. Idempotent: deleting a key that
    /// does not exist is not an error.
    /// </summary>
    /// <param name="key">The logical object key.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string key, CancellationToken ct = default);
}
