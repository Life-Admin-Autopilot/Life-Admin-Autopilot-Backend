using Life_Admin_Autopilot.DAL.Kernel.Mongo;

namespace Life_Admin_Autopilot_Backend.Kernel.Hosting;

/// <summary>
/// Creates every registered provider's Mongo indexes shortly after boot.
///
/// <para>
/// Deliberately NOT awaited in <see cref="StartAsync"/>: index creation talks to
/// Mongo, and blocking startup on it would hang the server — and every test that
/// spins up a host — whenever the database is unreachable. Failures are logged and
/// the server serves regardless.
/// </para>
///
/// <para>Disable with <c>Kernel:Mongo:EnsureIndexes = false</c>.</para>
/// </summary>
public sealed class MongoIndexHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MongoIndexHostedService> _logger;
    private readonly CancellationTokenSource _stopping = new();

    public MongoIndexHostedService(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<MongoIndexHostedService> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("Kernel:Mongo:EnsureIndexes", true))
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    using var scope = _services.CreateScope();
                    await scope.ServiceProvider
                        .GetRequiredService<MongoIndexInitializer>()
                        .EnsureAllAsync(_stopping.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "mongo:index-init-failed");
                }
            },
            CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }
}
