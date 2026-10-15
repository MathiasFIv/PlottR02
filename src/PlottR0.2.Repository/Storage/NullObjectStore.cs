namespace PlottR0._2.Repository.Storage;

/// <summary>
/// No-op <see cref="IObjectStore"/> used when durable object storage is disabled.
/// Every write is discarded, nothing exists, listings are empty, and reads fail with
/// <see cref="FileNotFoundException"/> (mirroring a missing durable object). The
/// file-system stores short-circuit on this instance, so the network methods are
/// never exercised in the default configuration.
/// </summary>
public sealed class NullObjectStore : IObjectStore
{
    /// <summary>The shared singleton — the store carries no state.</summary>
    public static readonly NullObjectStore Instance = new();

    private NullObjectStore() { }

    /// <inheritdoc />
    public Task PutAsync(string key, Stream content, string? contentType, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <inheritdoc />
    public Task GetToFileAsync(string key, string destinationPath, CancellationToken ct = default)
        => Task.FromException(new FileNotFoundException(
            $"Object '{key}' was not found (durable object storage is disabled)."));

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken ct = default)
        => Task.CompletedTask;
}
