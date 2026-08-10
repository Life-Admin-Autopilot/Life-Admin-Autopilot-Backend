namespace Life_Admin_Autopilot.DAL.Features.VoiceNotes;

/// <summary>
/// Where the recorded audio lives. Port of
/// <c>server/src/lib/voiceNoteStorage.ts</c>.
///
/// <para>
/// An interface rather than a concrete class because the deployment target is
/// Azure Blob while the parity reference is local disk, and the contract is
/// identical either way — put bytes under a key, read them back, remove them.
/// </para>
/// </summary>
public interface IVoiceNoteStorage
{
    Task PutAsync(string key, byte[] bytes, CancellationToken cancellationToken = default);

    Task<byte[]> GetAsync(string key, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>The storage key layout, kept beside the store so both sides of a delete agree on it.</summary>
public static class VoiceNoteStorageKeys
{
    /// <summary>
    /// Node's <c>buildStorageKey</c>: <c>{userId}/{noteId}.m4a</c>.
    ///
    /// <para>
    /// The extension is ALWAYS <c>.m4a</c>, regardless of the four content types
    /// the route accepts — Node hard-codes it, so an <c>audio/aac</c> upload is
    /// stored under an <c>.m4a</c> key too. Deriving it from the mime type here
    /// (as the document-scan key builder legitimately does) would make the two
    /// servers disagree on the path for the same note.
    /// </para>
    /// </summary>
    public static string Build(string userId, string noteId) => $"{userId}/{noteId}.m4a";
}

/// <summary>
/// Local-disk store, mirroring Node's <c>LocalDiskStorage</c> — including the
/// <c>..</c> guard, which is the only thing standing between a crafted key and a
/// path traversal.
/// </summary>
public sealed class LocalDiskVoiceNoteStorage : IVoiceNoteStorage
{
    private readonly string _root;

    public LocalDiskVoiceNoteStorage(string root)
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
    /// Throws when the file is gone. Deliberate: Node's <c>readFile</c> rejects, the
    /// worker's catch treats it as a job failure, and the note settles at
    /// <c>failed</c> — a row pointing at missing bytes is a server fault.
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
