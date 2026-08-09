using Life_Admin_Autopilot_Backend.Kernel.Modules;

namespace Life_Admin_Autopilot_Backend.Features.Account;

/// <summary>
/// Discovered by <c>EndpointModuleScanner</c>. No <c>Program.cs</c> edit.
/// </summary>
public sealed class AccountModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddAccountFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapAccountEndpoints();
}
