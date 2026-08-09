using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.Quota;
using Life_Admin_Autopilot.DAL.Kernel.UserData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot.DAL.Kernel;

/// <summary>
/// DAL half of the shared kernel. Called once from
/// <c>AddKernel()</c> in the PL — a slice never calls it.
/// </summary>
public static class KernelDataExtensions
{
    public static IServiceCollection AddKernelData(this IServiceCollection services)
    {
        // Must happen before any collection is resolved, so do it eagerly rather
        // than inside a factory.
        MongoKernelConventions.Register();

        services.TryAddSingleton<IMongoConnectionState, MongoPingConnectionState>();
        services.TryAddScoped<IUsageQuotaStore, MongoUsageQuotaStore>();
        services.TryAddScoped<MongoIndexInitializer>();
        services.AddMongoIndexProvider<KernelIndexProvider>();

        // Always last in the cascade. Slices add their own dependents-order erasers.
        services.AddUserDataEraser<UserProfileEraser>();

        return services;
    }

    /// <summary>
    /// Register one slice's Mongo indexes. Call from your <c>AddXxxFeature()</c>.
    ///
    /// <para>Not optional for a collection with a uniqueness invariant — see
    /// <see cref="IMongoIndexProvider"/>.</para>
    /// </summary>
    public static IServiceCollection AddMongoIndexProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IMongoIndexProvider
    {
        services.AddScoped<IMongoIndexProvider, TProvider>();
        return services;
    }

    /// <summary>
    /// Register one slice's contribution to account deletion.
    ///
    /// <para>Call from your <c>AddXxxFeature()</c> extension. Registrations are
    /// additive, so no two slices ever touch the same file.</para>
    /// </summary>
    public static IServiceCollection AddUserDataEraser<TEraser>(this IServiceCollection services)
        where TEraser : class, IUserDataEraser
    {
        services.AddScoped<IUserDataEraser, TEraser>();
        return services;
    }
}
