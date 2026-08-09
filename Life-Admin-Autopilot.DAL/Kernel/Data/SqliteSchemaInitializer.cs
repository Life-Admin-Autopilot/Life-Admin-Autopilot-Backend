using Life_Admin_Autopilot.DAL.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.DAL.Kernel.Data;

/// <summary>
/// Creates the Identity schema when — and only when — the provider is SQLite.
///
/// <para>
/// <b>Never runs against SQL Server.</b> <c>EnsureCreated()</c> bypasses migrations
/// and leaves a database that migrations can never subsequently touch, which would
/// quietly break the production deploy path. SQL Server keeps the checked-in
/// migration set as its single source of truth.
/// </para>
///
/// <para>
/// Runs synchronously during startup rather than fire-and-forget (unlike
/// <c>MongoIndexHostedService</c>): SQLite is a local file, so it cannot hang on a
/// network, and any endpoint touching Identity needs the tables to exist before the
/// first request. Failures are logged and non-fatal.
/// </para>
/// </summary>
public sealed class SqliteSchemaInitializer : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SqliteSchemaInitializer> _logger;

    public SqliteSchemaInitializer(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<SqliteSchemaInitializer> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!DatabaseProvider.ShouldEnsureCreated(_configuration))
        {
            return;
        }

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("db:sqlite-schema-ready");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "db:sqlite-schema-init-failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
