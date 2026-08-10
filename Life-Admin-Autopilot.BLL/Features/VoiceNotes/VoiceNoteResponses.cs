using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Dtos;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>Every 2xx body the voice-note routes emit. One class per envelope.</summary>
public sealed class VoiceSingleResponse
{
    [JsonPropertyName("voiceNote")]
    public VoiceNoteDto VoiceNote { get; init; } = new();
}

public sealed class VoiceListResponse
{
    [JsonPropertyName("voiceNotes")]
    public IReadOnlyList<VoiceNoteDto> VoiceNotes { get; init; } = Array.Empty<VoiceNoteDto>();
}

/// <summary>
/// Shared by <c>extract-tasks</c> and <c>review</c> — Node builds the same
/// <c>{ tasks, voiceNote }</c> literal in both, and the contract gives them two
/// names only because they are two operations.
///
/// <para>
/// <c>tasks</c> holds only the Tasks THIS call created, never the note's whole
/// history.
/// </para>
/// </summary>
public sealed class VoiceTasksResponse
{
    [JsonPropertyName("tasks")]
    public IReadOnlyList<TaskDto> Tasks { get; init; } = Array.Empty<TaskDto>();

    [JsonPropertyName("voiceNote")]
    public VoiceNoteDto VoiceNote { get; init; } = new();
}
