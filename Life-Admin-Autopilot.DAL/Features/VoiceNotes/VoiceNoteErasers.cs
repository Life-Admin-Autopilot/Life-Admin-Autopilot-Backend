using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.UserData;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.VoiceNotes;

/// <summary>
/// The recorded audio itself. Registered at <see cref="UserErasureOrder.Storage"/>
/// — it MUST run before the rows holding the storage keys are deleted, because
/// once the row is gone the key is unrecoverable and the file leaks forever.
///
/// <para>
/// Node's <c>deleteUserAndDependents()</c> deletes the <c>VoiceNote</c> rows but
/// has NO audio cleanup at all, so "delete my account" leaves every recording on
/// disk. Removing the bytes is a superset in the direction that matters — data
/// that is gone rather than data that is wrong — and the same call the coordinator
/// already made for the Google refresh token.
/// </para>
/// </summary>
public sealed class VoiceNoteStorageEraser : IUserDataEraser
{
    private readonly IVoiceNoteRepository _notes;
    private readonly IVoiceNoteStorage _storage;
    private readonly ILogger<VoiceNoteStorageEraser> _logger;

    public VoiceNoteStorageEraser(
        IVoiceNoteRepository notes,
        IVoiceNoteStorage storage,
        ILogger<VoiceNoteStorageEraser> logger)
    {
        _notes = notes;
        _storage = storage;
        _logger = logger;
    }

    public string Name => "voice-note-storage";

    public int Order => UserErasureOrder.Storage;

    public async Task EraseAsync(UserErasureContext context, CancellationToken cancellationToken = default)
    {
        var keys = await _notes.ListStorageKeysAsync(context.UserId, cancellationToken).ConfigureAwait(false);

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
                _logger.LogWarning(ex, "voiceNote:erase-storage-failed storageKey={StorageKey}", key);
            }
        }
    }
}

/// <summary>
/// The note records. <c>VoiceNote.deleteMany({userId})</c> — the entry Node's
/// hand-maintained cascade list already carries, and which slice K correctly
/// declined to register on this slice's behalf.
/// </summary>
public sealed class VoiceNoteEraser : MongoCollectionEraser
{
    public VoiceNoteEraser(IMongoDatabase database)
        : base("voicenotes", MongoCollections.VoiceNotes) => UseDatabase(database);
}
