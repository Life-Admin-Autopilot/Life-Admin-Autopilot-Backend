using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.UserData;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.DocumentScans;

/// <summary>
/// The scanned bytes themselves. Registered at
/// <see cref="UserErasureOrder.Storage"/> — it MUST run before the rows holding
/// the storage keys are deleted, because once the row is gone the key is
/// unrecoverable and the file leaks forever. Node's
/// <c>routes/me.ts</c> gathers the keys first for exactly this reason.
/// </summary>
public sealed class DocumentScanStorageEraser : IUserDataEraser
{
    private readonly IScannedDocumentRepository _scans;
    private readonly IDocumentScanStorage _storage;
    private readonly ILogger<DocumentScanStorageEraser> _logger;

    public DocumentScanStorageEraser(
        IScannedDocumentRepository scans,
        IDocumentScanStorage storage,
        ILogger<DocumentScanStorageEraser> logger)
    {
        _scans = scans;
        _storage = storage;
        _logger = logger;
    }

    public string Name => "document-scan-storage";

    public int Order => UserErasureOrder.Storage;

    public async Task EraseAsync(UserErasureContext context, CancellationToken cancellationToken = default)
    {
        var keys = await _scans.ListStorageKeysAsync(context.UserId, cancellationToken).ConfigureAwait(false);

        foreach (var key in keys)
        {
            try
            {
                await _storage.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Best effort, and idempotent: a file that is already gone must not
                // abort a user's account deletion.
                _logger.LogWarning(ex, "documentScan:erase-storage-failed storageKey={StorageKey}", key);
            }
        }
    }
}

/// <summary>The scan records. <c>ScannedDocument.deleteMany({userId})</c>.</summary>
public sealed class ScannedDocumentEraser : MongoCollectionEraser
{
    public ScannedDocumentEraser(IMongoDatabase database)
        : base("scanned-documents", MongoCollections.ScannedDocuments) => UseDatabase(database);
}

/// <summary>
/// The monthly scan allowance. Node erases this too, so a re-registered account
/// does not inherit a spent month.
/// </summary>
public sealed class DocumentScanUsageEraser : MongoCollectionEraser
{
    public DocumentScanUsageEraser(IMongoDatabase database)
        : base("document-scan-usage", MongoCollections.DocumentScanUsageCounters) => UseDatabase(database);
}
