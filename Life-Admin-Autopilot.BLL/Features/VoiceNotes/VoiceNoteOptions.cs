using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>
/// The two knobs <c>server/src/env.ts</c> exposes for voice notes, with the same
/// defaults.
///
/// <para>
/// Each reads a structured key first and falls back to the Node environment
/// variable name — the same pattern <c>DocumentScanOptions</c> uses — so a single
/// <c>.env</c> can drive both servers during a differential.
/// </para>
/// </summary>
public sealed class VoiceNoteOptions
{
    /// <summary>5 MiB. The FRIENDLY cap the handler enforces by hand.</summary>
    public int MaxBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Local-disk root. Null means <c>&lt;cwd&gt;/uploads/voice-notes</c>.</summary>
    public string? StorageDirectory { get; init; }

    /// <summary>
    /// The number that appears in the <c>payload_too_large</c> message. Node
    /// computes it as <c>Math.round(maxBytes / 1048576)</c>, so the message says
    /// "5MB" even though the limit is 5 MiB.
    /// </summary>
    public int MaxMegabytes => (int)Math.Round(MaxBytes / (1024.0 * 1024.0), MidpointRounding.AwayFromZero);

    public static VoiceNoteOptions FromConfiguration(IConfiguration configuration) => new()
    {
        MaxBytes = ReadInt(configuration, "VoiceNotes:MaxBytes", "VOICE_NOTE_MAX_BYTES", 5 * 1024 * 1024),
        StorageDirectory = configuration["VoiceNotes:StorageDirectory"]
                           ?? configuration["VOICE_NOTE_STORAGE_DIR"],
    };

    /// <summary>Resolved local-disk root, matching Node's <c>join(cwd, 'uploads', 'voice-notes')</c>.</summary>
    public string ResolveStorageRoot() =>
        string.IsNullOrWhiteSpace(StorageDirectory)
            ? Path.Combine(Directory.GetCurrentDirectory(), "uploads", "voice-notes")
            : StorageDirectory;

    private static int ReadInt(IConfiguration configuration, string key, string envKey, int fallback)
    {
        var raw = configuration[key] ?? configuration[envKey];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }
}
