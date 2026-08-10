using Life_Admin_Autopilot_Backend.Kernel.Modules;

namespace Life_Admin_Autopilot_Backend.Features.Profile;

/// <summary>
/// Discovered by <c>EndpointModuleScanner</c>. No <c>Program.cs</c> edit.
/// </summary>
public sealed class ProfileModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddProfileFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapProfileEndpoints();
}
