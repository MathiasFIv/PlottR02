using System.Text;
using PlottR0._2.Repository.Storage;

namespace PlottR0._2.Tests.Support;

public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _contentTypes = new(StringComparer.Ordinal);
    private readonly List<string> _putKeys = [];
    private readonly List<string> _deleteKeys = [];
    private readonly List<string> _getToFileKeys = [];
    private readonly List<string> _listKeysPrefixes = [];

    public bool FailOnPut { get; set; }

    public Exception PutException { get; set; } =
        new InvalidOperationException("Simulated object-store upload failure.");

    public int PutCalls { get; private set; }

    public int ExistsCalls { get; private set; }

    public int GetToFileCalls { get; private set; }

    public int ListKeysCalls { get; private set; }

    public int DeleteCalls { get; private set; }

    public IReadOnlyList<string> PutKeys { get { lock (_gate) return _putKeys.ToArray(); } }

    public IReadOnlyList<string> DeleteKeys { get { lock (_gate) return _deleteKeys.ToArray(); } }

    public IReadOnlyList<string> GetToFileKeys { get { lock (_gate) return _getToFileKeys.ToArray(); } }

    public IReadOnlyList<string> ListKeysPrefixes { get { lock (_gate) return _listKeysPrefixes.ToArray(); } }

    public IReadOnlyCollection<string> Keys { get { lock (_gate) return _objects.Keys.ToArray(); } }

    public void Seed(string key, byte[] bytes)
    {
        lock (_gate)
        {
            _objects[key] = bytes;
        }
    }

    public void Seed(string key, string text) => Seed(key, Encoding.UTF8.GetBytes(text));

    public bool Contains(string key)
    {
        lock (_gate)
        {
            return _objects.ContainsKey(key);
        }
    }

    public byte[] GetBytes(string key)
    {
        lock (_gate)
        {
            return _objects[key];
        }
    }

    public string? GetContentType(string key)
    {
        lock (_gate)
        {
            return _contentTypes.TryGetValue(key, out var contentType) ? contentType : null;
        }
    }

    public void ResetCounters()
    {
        lock (_gate)
        {
            PutCalls = 0;
            ExistsCalls = 0;
            GetToFileCalls = 0;
            ListKeysCalls = 0;
            DeleteCalls = 0;
            _putKeys.Clear();
            _deleteKeys.Clear();
            _getToFileKeys.Clear();
            _listKeysPrefixes.Clear();
        }
    }

    public async Task PutAsync(string key, Stream content, string? contentType, CancellationToken ct = default)
    {
        if (FailOnPut)
        {
            lock (_gate)
            {
                PutCalls++;
                _putKeys.Add(key);
            }

            await Task.Yield();
            throw PutException;
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        lock (_gate)
        {
            PutCalls++;
            _putKeys.Add(key);
            _objects[key] = bytes;
            _contentTypes[key] = contentType;
        }
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ExistsCalls++;
            return Task.FromResult(_objects.ContainsKey(key));
        }
    }

    public async Task GetToFileAsync(string key, string destinationPath, CancellationToken ct = default)
    {
        byte[] bytes;
        lock (_gate)
        {
            GetToFileCalls++;
            _getToFileKeys.Add(key);
            if (!_objects.TryGetValue(key, out var found))
                throw new FileNotFoundException($"Object '{key}' was not found in the in-memory store.", key);

            bytes = found!;
        }

        var parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        await File.WriteAllBytesAsync(destinationPath, bytes, ct);
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ListKeysCalls++;
            _listKeysPrefixes.Add(prefix);

            IReadOnlyList<string> keys = _objects.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            return Task.FromResult(keys);
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            DeleteCalls++;
            _deleteKeys.Add(key);
            _objects.Remove(key);
            _contentTypes.Remove(key);
        }

        return Task.CompletedTask;
    }
}
