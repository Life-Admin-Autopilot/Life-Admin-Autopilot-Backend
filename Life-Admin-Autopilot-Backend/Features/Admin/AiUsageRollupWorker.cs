using Life_Admin_Autopilot.BLL.Features.Admin;
using Life_Admin_Autopilot.DAL.Kernel.Quota;
using Life_Admin_Autopilot.DAL.Kernel.Telemetry;

namespace Life_Admin_Autopilot_Backend.Features.Admin;

/// <summary>
/// Folds raw usage events into daily rollups.
///
/// <para>
/// <b>Rolls up today AND yesterday, every pass.</b> Today's row is what makes the
/// Pulse screen live rather than a day behind; yesterday's is re-folded because a
/// turn that started at 23:59 lands its event after midnight, and a job that only
/// ever looked forward would leave that call permanently uncounted. Re-running is
/// free — <see cref="IAiUsageStore.RollupDayAsync"/> replaces a day rather than
/// incrementing it.
/// </para>
///
/// <para>
/// <b>Deliberately not a cron.</b> A five-minute loop inside the process needs no
/// scheduler, no leader election for a single-instance deployment, and no separate
/// thing to notice has stopped. If this ever runs multi-instance, the unique index
/// on <c>{day, userId, feature}</c> is what keeps two racing passes from
/// double-counting — the loser of the delete-then-insert fails rather than
/// duplicating.
/// </para>
/// </summary>
public sealed class AiUsageRollupWorker : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Let the app finish starting before the first pass. The aggregation is cheap
    /// but it is not what anyone is waiting for at boot.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<AiUsageRollupWorker> _logger;

    public AiUsageRollupWorker(
        IServiceScopeFactory scopes,
        ILogger<AiUsageRollupWorker> logger,
        TimeProvider? time = null)
    {
        _scopes = scopes;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, _time, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Public so a test — or a future admin "recompute" button — can drive one pass.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IAiUsageStore>();

            var now = _time.GetUtcNow().UtcDateTime;
            var today = UsageQuotaBuckets.UtcDate(now);
            var yesterday = UsageQuotaBuckets.UtcDate(now.AddDays(-1));

            var todayRows = await store.RollupDayAsync(today, cancellationToken).ConfigureAwait(false);
            var yesterdayRows = await store.RollupDayAsync(yesterday, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "ai:usage-rollup today={Today} rows={TodayRows} yesterday={Yesterday} rows={YesterdayRows}",
                today,
                todayRows,
                yesterday,
                yesterdayRows);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a failure.
        }
        catch (Exception ex)
        {
            // Never fatal. A background worker that throws out of ExecuteAsync stops
            // permanently and silently, and the first sign would be a dashboard that
            // quietly stopped updating.
            _logger.LogError(ex, "ai:usage-rollup-failed");
        }
    }
}

/// <summary>
/// Creates the console roles at boot, and grants the bootstrap admin named in
/// configuration.
///
/// <para>
/// <b><c>ADMIN_BOOTSTRAP_EMAIL</c> grants, it never creates.</b> The account must
/// already exist — signed up through the app like anyone else. A bootstrap that
/// minted credentials would be a permanent backdoor with a password in an
/// environment variable.
/// </para>
/// </summary>
public sealed class AdminRoleSeeder : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminRoleSeeder> _logger;

    public AdminRoleSeeder(
        IServiceScopeFactory scopes,
        IConfiguration configuration,
        ILogger<AdminRoleSeeder> logger)
    {
        _scopes = scopes;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var roles = scope.ServiceProvider.GetRequiredService<IAdminRoleStore>();

            await roles.EnsureRolesExistAsync(cancellationToken).ConfigureAwait(false);

            var email = _configuration["ADMIN_BOOTSTRAP_EMAIL"] ?? _configuration["Admin:BootstrapEmail"];
            if (string.IsNullOrWhiteSpace(email))
            {
                return;
            }

            var granted = await roles
                .GrantAsync(email.Trim(), AdminRoles.Admin, cancellationToken)
                .ConfigureAwait(false);

            if (granted)
            {
                _logger.LogInformation("admin:bootstrap-granted email={Email}", email);
            }
            else
            {
                _logger.LogWarning(
                    "admin:bootstrap-skipped email={Email} — no account with that address. "
                    + "Sign up in the app first, then restart; the console never creates credentials.",
                    email);
            }
        }
        catch (Exception ex)
        {
            // Boot must not depend on this. A server that refuses to start because
            // role seeding failed takes the customer API down over an admin concern.
            _logger.LogError(ex, "admin:role-seed-failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
