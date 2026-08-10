using Life_Admin_Autopilot_Backend.Kernel.Modules;

namespace Life_Admin_Autopilot_Backend.Features.VoiceNotes;

/// <summary>
/// Discovered by <c>EndpointModuleScanner</c>. No <c>Program.cs</c> edit.
/// </summary>
public sealed class VoiceNotesModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddVoiceNotesFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapVoiceNoteEndpoints();
}
