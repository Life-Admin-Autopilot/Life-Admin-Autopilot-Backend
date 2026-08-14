using Life_Admin_Autopilot.BLL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Features.Account;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Kernel;
using Life_Admin_Autopilot_Backend.Kernel.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.DocumentScans;

/// <summary>
/// The document-scan slice's entire DI surface.
///
/// <para>
/// The two collections it owns — <c>scanneddocuments</c> and
/// <c>documentscanusagecounters</c> — are already named in
/// <c>MongoCollections</c>, so the slice adds no constant of its own and there is
/// nothing to append to that merge-conflict-prone class.
/// </para>
/// </summary>
public static class DocumentScansFeature
{
    public static IServiceCollection AddDocumentScansFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Read once at startup, like Node's env(). Singleton because the values are
        // immutable for the process lifetime and the storage root derives from them.
        var options = DocumentScanOptions.FromConfiguration(configuration);
        services.TryAddSingleton(options);

        services.TryAddScoped<IScannedDocumentRepository, ScannedDocumentRepository>();
        services.TryAddScoped<IDocumentScanNotifications, DocumentScanNotifications>();
        services.TryAddScoped<IDocumentScanQuotaService, DocumentScanQuotaService>();
        services.TryAddScoped<IDocumentScanReviewService, DocumentScanReviewService>();

        // GET /quota needs the caller's REAL subscription tier. TryAdd, so this and
        // the account slice's identical registration cannot clobber each other —
        // the same pattern AccountFeature already documents for the auth slice.
        services.TryAddScoped<IAccountProfileRepository, AccountProfileRepository>();

        // Local disk, mirroring lib/documentScanStorage.ts. Swapping in a blob store
        // is a one-line change here and nothing above it moves.
        services.TryAddSingleton<IDocumentScanStorage>(
            _ => new LocalDiskDocumentScanStorage(options.ResolveStorageRoot()));

        // The no-key parity target. The AI slice REPLACES this — services.Replace,
        // never TryAdd, or the null one wins and every scan keeps failing.
        services.TryAddScoped<IDocumentExtractor, NullDocumentExtractor>();

        // ...and here is that replacement. Gated on the key, so an unconfigured
        // deployment keeps the reference server's behaviour exactly — the no-key
        // parity run asserts that every scan fails with Node's sentence, and a
        // reader that worked without a key would break it.
        var extraction = DocumentExtractionOptions.FromConfiguration(configuration);
        services.TryAddSingleton(extraction);

        if (extraction.IsConfigured)
        {
            services.AddHttpClient<GeminiDocumentExtractor>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(extraction.TimeoutSeconds);
            });

            // Replace, not TryAdd: the null registration above already owns the
            // interface, so anything less leaves it in place and nothing changes.
            services.Replace(ServiceDescriptor.Scoped<IDocumentExtractor>(
                provider => provider.GetRequiredService<GeminiDocumentExtractor>()));
        }

        // Storage order first: the blobs must go while the rows still hold the keys.
        services.AddUserDataEraser<DocumentScanStorageEraser>();
        services.AddUserDataEraser<ScannedDocumentEraser>();
        services.AddUserDataEraser<DocumentScanUsageEraser>();

        services.AddMongoIndexProvider<DocumentScanIndexes>();
        services.AddKernelWorker<DocumentScanWorker>();

        return services;
    }
}
