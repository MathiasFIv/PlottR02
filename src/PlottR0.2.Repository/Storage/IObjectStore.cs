namespace PlottR0._2.Repository.Storage;

public interface IObjectStore
{
    Task PutAsync(string key, Stream content, string? contentType, CancellationToken ct = default);

    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    Task GetToFileAsync(string key, string destinationPath, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}
