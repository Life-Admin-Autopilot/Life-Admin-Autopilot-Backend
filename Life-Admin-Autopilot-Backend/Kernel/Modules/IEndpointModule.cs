using System.Reflection;

namespace Life_Admin_Autopilot_Backend.Kernel.Modules;

/// <summary>
/// <b>The conflict-free way to add a feature slice.</b> Implement this anywhere in
/// a loaded assembly and the kernel finds it by scanning — nobody edits
/// <c>Program.cs</c>, so seven agents can land seven slices without touching a
/// shared file.
///
/// <para><b>Conventions</b></para>
/// <list type="bullet">
///   <item>One module per slice, in that slice's folder, named <c>XxxModule</c>.</item>
///   <item>It must have a public parameterless constructor — it is instantiated
///         during startup, before the container is built.</item>
///   <item><see cref="AddServices"/> should do nothing but call your slice's
///         <c>AddXxxFeature()</c> extension, so the DI surface stays greppable.</item>
///   <item><see cref="MapEndpoints"/> is for minimal APIs. Controllers are
///         discovered separately by MVC and need no mapping here — a
///         controller-only slice implements <see cref="AddServices"/> and leaves
///         <see cref="MapEndpoints"/> as the default no-op.</item>
///   <item><see cref="Order"/> only matters if one slice must register before
///         another. Leave it at 0.</item>
/// </list>
///
/// <example>
/// <code>
/// public sealed class TasksModule : IEndpointModule
/// {
///     public void AddServices(IServiceCollection services, IConfiguration configuration) =>
///         services.AddTasksFeature(configuration);
///
///     public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
///         endpoints.MapTasksEndpoints();
/// }
/// </code>
/// </example>
/// </summary>
public interface IEndpointModule
{
    int Order => 0;

    /// <summary>Runs at <c>builder.Services</c> time. Call your <c>AddXxxFeature()</c> here.</summary>
    void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    /// <summary>Runs after the app is built. Map minimal-API endpoints here.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

/// <summary>
/// Discovers and runs every <see cref="IEndpointModule"/>. Called exactly twice
/// from <c>Program.cs</c> and never by a slice.
/// </summary>
public static class EndpointModuleScanner
{
    private static IReadOnlyList<IEndpointModule>? _cached;

    /// <summary>
    /// Scans the entry assembly plus every already-loaded assembly whose name starts
    /// with <c>Life-Admin-Autopilot</c>, so a module can live in PL, BLL or DAL.
    /// </summary>
    public static IReadOnlyList<IEndpointModule> Discover()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var assemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && (a.GetName().Name?.StartsWith("Life-Admin-Autopilot", StringComparison.Ordinal) ?? false))
            .Append(Assembly.GetExecutingAssembly())
            .Distinct()
            .ToList();

        _cached = assemblies
            .SelectMany(SafeTypes)
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .Where(t => typeof(IEndpointModule).IsAssignableFrom(t))
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IEndpointModule)Activator.CreateInstance(t)!)
            .OrderBy(m => m.Order)
            .ThenBy(m => m.GetType().FullName, StringComparer.Ordinal)
            .ToList();

        return _cached;
    }

    public static IServiceCollection AddEndpointModules(this IServiceCollection services, IConfiguration configuration)
    {
        foreach (var module in Discover())
        {
            module.AddServices(services, configuration);
        }

        return services;
    }

    public static IEndpointRouteBuilder MapEndpointModules(this IEndpointRouteBuilder endpoints)
    {
        foreach (var module in Discover())
        {
            module.MapEndpoints(endpoints);
        }

        return endpoints;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
