using System.Text;
using PlottR0._2.Repository.Storage;

namespace PlottR0._2.Tests.Support;

/// <summary>
/// Test-local, in-memory <see cref="IObjectStore"/> used to exercise the durable
/// write-through/rehydration behaviour of the file-system stores without S3 or Docker.
/// It records the exact keys written and the per-operation call counts so tests can
/// assert both the data that was written and whether the object store was touched at all
/// (the warm-cache fast path). An injectable failure mode (<see cref="FailOnPut"/>)
/// simulates an upload failure so write-through rollback can be asserted.
/// Not thread-safe by intent — each test owns its own instance.
/// </summary>
public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _contentTypes = new(StringComparer.Ordinal);
    private readonly List<string> _putKeys = [];
    private readonly List<string> _deleteKeys = [];
    private readonly List<string> _getToFileKeys = [];
    private readonly List<string> _listKeysPrefixes = [];

    /// <summary>When <see langword="true"/>, every <see cref="PutAsync"/> call throws <see cref="PutException"/>.</summary>
    public bool FailOnPut { get; set; }

    /// <summary>The exception thrown by <see cref="PutAsync"/> when <see cref="FailOnPut"/> is set.</summary>
    public Exception PutException { get; set; } =
        new InvalidOperationException("Simulated object-store upload failure.");

    /// <summary>Number of <see cref="PutAsync"/> attempts (including failed ones).</summary>
    public int PutCalls { get; private set; }

    /// <summary>Number of <see cref="ExistsAsync"/> calls.</summary>
    public int ExistsCalls { get; private set; }

    /// <summary>Number of <see cref="GetToFileAsync"/> calls.</summary>
    public int GetToFileCalls { get; private set; }

    /// <summary>Number of <see cref="ListKeysAsync"/> calls.</summary>
    public int ListKeysCalls { get; private set; }

    /// <summary>Number of <see cref="DeleteAsync"/> calls.</summary>
    public int DeleteCalls { get; private set; }

    /// <summary>The keys passed to <see cref="PutAsync"/>, in call order (including failed attempts).</summary>
    public IReadOnlyList<string> PutKeys { get { lock (_gate) return _putKeys.ToArray(); } }

    /// <summary>The keys passed to <see cref="DeleteAsync"/>, in call order.</summary>
    public IReadOnlyList<string> DeleteKeys { get { lock (_gate) return _deleteKeys.ToArray(); } }

    /// <summary>The keys passed to <see cref="GetToFileAsync"/>, in call order.</summary>
    public IReadOnlyList<string> GetToFileKeys { get { lock (_gate) return _getToFileKeys.ToArray(); } }

    /// <summary>The prefixes passed to <see cref="ListKeysAsync"/>, in call order.</summary>
    public IReadOnlyList<string> ListKeysPrefixes { get { lock (_gate) return _listKeysPrefixes.ToArray(); } }

    /// <summary>A snapshot of every key currently stored.</summary>
    public IReadOnlyCollection<string> Keys { get { lock (_gate) return _objects.Keys.ToArray(); } }

    /// <summary>Stores <paramref name="bytes"/> under <paramref name="key"/> without recording a call.</summary>
    public void Seed(string key, byte[] bytes)
    {
        lock (_gate)
        {
            _objects[key] = bytes;
        }
    }

    /// <summary>Stores the UTF-8 encoding of <paramref name="text"/> under <paramref name="key"/>.</summary>
    public void Seed(string key, string text) => Seed(key, Encoding.UTF8.GetBytes(text));

    /// <summary>True when an object exists at <paramref name="key"/>.</summary>
    public bool Contains(string key)
    {
        lock (_gate)
        {
            return _objects.ContainsKey(key);
        }
    }

    /// <summary>Returns the bytes stored at <paramref name="key"/> (throws when absent).</summary>
    public byte[] GetBytes(string key)
    {
        lock (_gate)
        {
            return _objects[key];
        }
    }

    /// <summary>Returns the content type recorded for <paramref name="key"/>, or <c>null</c>.</summary>
    public string? GetContentType(string key)
    {
        lock (_gate)
        {
            return _contentTypes.TryGetValue(key, out var contentType) ? contentType : null;
        }
    }

    /// <summary>Zeroes the call counters and clears the recorded key/prefix lists.</summary>
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

    /// <inheritdoc />
    public async Task PutAsync(string key, Stream content, string? contentType, CancellationToken ct = default)
    {
        if (FailOnPut)
        {
            lock (_gate)
            {
                PutCalls++;
                _putKeys.Add(key);
            }

            // Yield so the failure surfaces from the awaited task, like a real network store.
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

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ExistsCalls++;
            return Task.FromResult(_objects.ContainsKey(key));
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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
