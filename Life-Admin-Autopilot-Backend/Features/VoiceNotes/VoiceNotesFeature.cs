using Life_Admin_Autopilot.BLL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Features.Account;
using Life_Admin_Autopilot.DAL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Kernel;
using Life_Admin_Autopilot.DAL.Kernel.Storage;
using Life_Admin_Autopilot_Backend.Kernel.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.VoiceNotes;

/// <summary>
/// The voice-note slice's entire DI surface.
///
/// <para>
/// The one collection it owns — <c>voicenotes</c> — is already named in
/// <c>MongoCollections</c>, so the slice adds no constant of its own and there is
/// nothing to append to that merge-conflict-prone class.
/// </para>
/// </summary>
public static class VoiceNotesFeature
{
    public static IServiceCollection AddVoiceNotesFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Read once at startup, like Node's env(). Singleton because the values are
        // immutable for the process lifetime and the storage root derives from them.
        var options = VoiceNoteOptions.FromConfiguration(configuration);
        services.TryAddSingleton(options);

        services.TryAddScoped<IVoiceNoteRepository, VoiceNoteRepository>();
        services.TryAddScoped<IVoiceNoteNotifications, VoiceNoteNotifications>();
        services.TryAddScoped<IVoiceNoteTaskPersistence, VoiceNoteTaskPersistence>();
        services.TryAddScoped<IVoiceClarificationStaging, VoiceClarificationStaging>();
        services.TryAddScoped<IVoiceExtractionCommit, VoiceExtractionCommit>();

        // Extraction needs the caller's locale. TryAdd, so this and the account and
        // document-scan slices' identical registrations cannot clobber each other.
        services.TryAddScoped<IAccountProfileRepository, AccountProfileRepository>();

        // Azure Blob when a connection string is configured, local disk otherwise —
        // the same decision the document-scan slice makes, and for the same reason:
        // local disk is the parity reference and the only thing a clone can use.
        var blobs = AzureBlobOptions.FromConfiguration(configuration);
        services.TryAddSingleton(blobs);
        services.TryAddSingleton<IVoiceNoteStorage>(_ => blobs.IsConfigured
            ? new AzureBlobVoiceNoteStorage(blobs.ConnectionString!, blobs.VoiceNotesContainer)
            : new LocalDiskVoiceNoteStorage(options.ResolveStorageRoot()));

        // The no-key parity target. The AI slice REPLACES both — services.Replace,
        // never TryAdd, or the null ones win and every note keeps failing.
        services.TryAddScoped<IVoiceTranscriber, NullVoiceTranscriber>();
        services.TryAddScoped<IVoiceExtractor, NullVoiceExtractor>();

        // Storage order first: the audio must go while the rows still hold the keys.
        services.AddUserDataEraser<VoiceNoteStorageEraser>();
        services.AddUserDataEraser<VoiceNoteEraser>();

        services.AddMongoIndexProvider<VoiceNoteIndexes>();
        services.AddKernelWorker<VoiceNoteWorker>();

        return services;
    }
}
