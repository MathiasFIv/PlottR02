namespace PlottR0._2.Repository.Storage;

public sealed class NullObjectStore : IObjectStore
{
    public static readonly NullObjectStore Instance = new();

    private NullObjectStore() { }

    public Task PutAsync(string key, Stream content, string? contentType, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task GetToFileAsync(string key, string destinationPath, CancellationToken ct = default)
        => Task.FromException(new FileNotFoundException(
            $"Object '{key}' was not found (durable object storage is disabled)."));

    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task DeleteAsync(string key, CancellationToken ct = default)
        => Task.CompletedTask;
}
