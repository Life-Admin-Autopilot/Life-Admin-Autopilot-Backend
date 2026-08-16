using Life_Admin_Autopilot.BLL.Features.Knowledge;
using Life_Admin_Autopilot.BLL.Features.Planning;
using Life_Admin_Autopilot.BLL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Features.Account;
using Life_Admin_Autopilot.DAL.Features.Clarifications;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Features.Notifications;
using Life_Admin_Autopilot.DAL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Kernel;
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
        services.TryAddScoped<IVoiceNoteTaskPersistence, VoiceNoteTaskPersistence>();
        services.TryAddScoped<IVoiceClarificationStaging, VoiceClarificationStaging>();
        services.TryAddScoped<IVoiceExtractionCommit, VoiceExtractionCommit>();

        // How the outcome reaches the user. The recording surface closes on upload,
        // so from that moment this is the only channel the note has.
        services.TryAddScoped<VoiceNoteOutcomeNotifier>();

        // Its collaborators, owned by three other slices. TryAdd throughout, so
        // whichever module the assembly scanner reaches first wins and the duplicate
        // registrations are no-ops — assembly-scan order is not something a slice
        // should have to reason about.
        services.TryAddScoped<NotificationRepository>();
        services.TryAddScoped<IDocumentScanNotifications, DocumentScanNotifications>();

        // The open-question cap the clarify lane is now subject to.
        services.TryAddScoped<ClarificationRepository>();

        // Extraction needs the caller's locale. TryAdd, so this and the account and
        // document-scan slices' identical registrations cannot clobber each other.
        services.TryAddScoped<IAccountProfileRepository, AccountProfileRepository>();

        // Local disk, mirroring lib/voiceNoteStorage.ts. Swapping in a blob store is
        // a one-line change here and nothing above it moves.
        services.TryAddSingleton<IVoiceNoteStorage>(
            _ => new LocalDiskVoiceNoteStorage(options.ResolveStorageRoot()));

        // The no-key parity target: both throw 503 on their first line, so with no
        // provider configured every note burns its attempt ladder and settles at
        // `failed` — which is exactly what Node does without GEMINI_API_KEY.
        services.TryAddScoped<IVoiceTranscriber, NullVoiceTranscriber>();
        services.TryAddScoped<IVoiceExtractor, NullVoiceExtractor>();

        // …and the replacements, when there IS something to call. `Replace`, never
        // `TryAdd` — the null ones are already registered above, so a TryAdd here is
        // a silent no-op and every note keeps failing.
        //
        // Gated on the SAME configuration each adapter actually needs, one at a time.
        // A deployment with an ASR token and no planning key transcribes and then
        // fails honestly at extraction, which is more useful than refusing to
        // transcribe because a different key is missing. Doing this here rather than
        // from the AI slice also keeps it order-independent: module registration
        // order is assembly-scan order, and a Replace that runs before the TryAdd it
        // is meant to override does nothing at all.
        if (HasSpeechProvider(configuration))
        {
            services.Replace(ServiceDescriptor.Scoped<IVoiceTranscriber, NemotronVoiceTranscriber>());
        }

        if (PlanningOptions.FromConfiguration(configuration).IsConfigured)
        {
            services.Replace(ServiceDescriptor.Scoped<IVoiceExtractor, PlanningVoiceExtractor>());

            // The auto-file policy needs to know whether a draft clashes with
            // something the user already has. TryAdd, so the Knowledge and Planning
            // slices' identical registrations cannot clobber each other.
            services.TryAddScoped<ConflictService>();
        }

        // Storage order first: the audio must go while the rows still hold the keys.
        services.AddUserDataEraser<VoiceNoteStorageEraser>();
        services.AddUserDataEraser<VoiceNoteEraser>();

        services.AddMongoIndexProvider<VoiceNoteIndexes>();
        services.AddKernelWorker<VoiceNoteWorker>();

        return services;
    }

    /// <summary>
    /// Is there an ASR provider to call?
    ///
    /// <para>
    /// Reads <c>HF_TOKEN</c> the way <c>AddSpeechServices</c> does — the same key,
    /// deliberately, so "the speech feature is configured" cannot mean one thing to
    /// the controller and another to the worker.
    /// </para>
    /// </summary>
    private static bool HasSpeechProvider(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["HF_TOKEN"]);
}
