using Life_Admin_Autopilot_Backend.Kernel.Modules;

namespace Life_Admin_Autopilot_Backend.Features.DocumentScans;

/// <summary>
/// Discovered by <c>EndpointModuleScanner</c>. No <c>Program.cs</c> edit.
/// </summary>
public sealed class DocumentScansModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddDocumentScansFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapDocumentScanEndpoints();
}
