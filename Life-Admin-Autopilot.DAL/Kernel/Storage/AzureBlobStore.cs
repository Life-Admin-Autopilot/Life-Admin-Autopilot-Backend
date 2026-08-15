using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.DAL.Kernel.Storage;

/// <summary>
/// Whether this deployment stores uploads in Azure Blob, and where.
///
/// <para>
/// Absent a connection string every store falls back to local disk, which is the
/// parity reference and what a teammate running the project gets. Nothing here
/// is required to boot.
/// </para>
/// </summary>
public sealed class AzureBlobOptions
{
    public const string DefaultDocumentsContainer = "documents";
    public const string DefaultVoiceNotesContainer = "voice-notes";

    public string? ConnectionString { get; init; }

    public string DocumentsContainer { get; init; } = DefaultDocumentsContainer;

    public string VoiceNotesContainer { get; init; } = DefaultVoiceNotesContainer;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);

    public static AzureBlobOptions FromConfiguration(IConfiguration configuration) => new()
    {
        // The env-var spelling first, since that is what the secret is already
        // stored under; the colon form is the idiomatic .NET alternative.
        ConnectionString =
            configuration["AZURE_STORAGE_CONNECTION_STRING"]
            ?? configuration["Azure:Storage:ConnectionString"],

        DocumentsContainer =
            configuration["AZURE_STORAGE_DOCUMENTS_CONTAINER"]
            ?? configuration["Azure:Storage:DocumentsContainer"]
            ?? DefaultDocumentsContainer,

        VoiceNotesContainer =
            configuration["AZURE_STORAGE_VOICE_NOTES_CONTAINER"]
            ?? configuration["Azure:Storage:VoiceNotesContainer"]
            ?? DefaultVoiceNotesContainer,
    };
}

/// <summary>
/// Put bytes under a key, read them back, remove them — against one Azure Blob
/// container.
///
/// <para>
/// Both upload stores have byte-identical contracts, so they share this rather
/// than each carrying its own copy of the SDK calls. The behaviour deliberately
/// matches <c>LocalDiskStorage</c> at every edge, because the routes above were
/// written against local disk and the parity suite asserts their responses:
/// a missing blob on read THROWS (a row pointing at absent bytes is a server
/// fault, not a 404 the client can act on), while a missing blob on delete is
/// silent, exactly as <c>File.Delete</c> is.
/// </para>
/// </summary>
public sealed class AzureBlobStore
{
    private readonly BlobContainerClient _container;

    // Creating a container is idempotent but costs a round trip, so it runs once
    // per process rather than on every upload. Lazy over a Task is the standard
    // async-once: concurrent callers await the same attempt, and a failure is
    // not cached as success.
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    public AzureBlobStore(string connectionString, string containerName)
    {
        _container = new BlobContainerClient(connectionString, containerName);
    }

    public async Task PutAsync(string key, byte[] bytes, CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        var blob = _container.GetBlobClient(Validate(key));
        using var stream = new MemoryStream(bytes, writable: false);

        // Overwrite: a retry of the same scan must land on the same key rather
        // than failing on "blob already exists", which is what local disk does.
        await blob.UploadAsync(stream, overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws when the blob is gone, mirroring <c>File.ReadAllBytesAsync</c>. The
    /// SDK's own <see cref="RequestFailedException"/> carries the 404, so it is
    /// left to propagate rather than translated into something the routes above
    /// would have to learn about.
    /// </summary>
    public async Task<byte[]> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var blob = _container.GetBlobClient(Validate(key));
        var response = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return response.Value.Content.ToArray();
    }

    /// <summary>Silent when the blob is already gone, as <c>File.Delete</c> is.</summary>
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var blob = _container.GetBlobClient(Validate(key));
        await blob.DeleteIfExistsAsync(
            DeleteSnapshotsOption.IncludeSnapshots,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_ensured)
        {
            return;
        }

        await _ensureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ensured)
            {
                return;
            }

            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _ensured = true;
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    /// <summary>
    /// The same <c>..</c> guard the disk store carries. Blob names have no parent
    /// directory to escape into, so this is not the traversal defence it is on
    /// disk — it is here so a key rejected by one store is rejected by both, and
    /// switching backends cannot quietly widen what a crafted key may address.
    /// </summary>
    private static string Validate(string key)
    {
        if (key.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("storage:invalid-key");
        }

        return key;
    }
}
