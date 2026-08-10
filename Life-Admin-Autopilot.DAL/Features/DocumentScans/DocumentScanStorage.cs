namespace Life_Admin_Autopilot.DAL.Features.DocumentScans;

/// <summary>
/// Where the original scanned bytes live. Port of
/// <c>server/src/lib/documentScanStorage.ts</c>.
///
/// <para>
/// An interface rather than a concrete class because the deployment target is
/// Azure Blob while the parity reference is local disk, and the route contract is
/// identical either way — put bytes under a key, read them back, remove them. A
/// blob implementation drops in here with no change above this line.
/// </para>
/// </summary>
public interface IDocumentScanStorage
{
    Task PutAsync(string key, byte[] bytes, CancellationToken cancellationToken = default);

    Task<byte[]> GetAsync(string key, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// The storage key layout, kept beside the store so both sides of a delete agree
/// on it.
/// </summary>
public static class DocumentScanStorageKeys
{
    private static readonly IReadOnlyDictionary<string, string> ExtensionByMime =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["application/pdf"] = "pdf",
            ["image/jpeg"] = "jpg",
            ["image/png"] = "png",
            ["image/heic"] = "heic",
            ["image/webp"] = "webp",
        };

    /// <summary>Node's <c>buildStorageKey</c>: <c>{userId}/{scanId}.{ext}</c>.</summary>
    public static string Build(string userId, string scanId, string mimeType)
    {
        var extension = ExtensionByMime.TryGetValue(mimeType, out var known) ? known : "bin";
        return $"{userId}/{scanId}.{extension}";
    }
}

/// <summary>
/// Local-disk store, mirroring Node's <c>LocalDiskStorage</c> byte for byte —
/// including the <c>..</c> guard, which is the only thing standing between a
/// crafted key and a path traversal.
/// </summary>
public sealed class LocalDiskDocumentScanStorage : IDocumentScanStorage
{
    private readonly string _root;

    public LocalDiskDocumentScanStorage(string root)
    {
        _root = root;
    }

    public async Task PutAsync(string key, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws when the file is gone. Deliberate: Node's <c>readFile</c> rejects and
    /// the request becomes a 500 <c>internal_error</c>, because a row pointing at
    /// missing bytes is a server fault, not a 404 the client can act on.
    /// </summary>
    public Task<byte[]> GetAsync(string key, CancellationToken cancellationToken = default) =>
        File.ReadAllBytesAsync(Resolve(key), cancellationToken);

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        File.Delete(Resolve(key));
        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        if (key.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("storage:invalid-key");
        }

        return Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
    }
}
