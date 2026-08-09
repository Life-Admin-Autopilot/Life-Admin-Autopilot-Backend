namespace Life_Admin_Autopilot_Backend.Kernel.Hosting;

/// <summary>
/// Base for the five background workers the slices will add — voice
/// transcription, reminders, document scans, ICS feeds and Google sync.
///
/// <para>
/// Node's workers are all the same shape: <c>setInterval</c> around a
/// <c>runOnce()</c> that claims a batch, plus a guard so a slow tick never
/// overlaps itself. This reproduces that exactly, because the overlap guard is
/// load-bearing — two concurrent ticks would double-fire reminders.
/// </para>
///
/// <para><b>Rules for a worker</b></para>
/// <list type="bullet">
///   <item><see cref="RunOnceAsync"/> must never throw. It is wrapped anyway, but a
///         throwing tick means a missed batch.</item>
///   <item>Claim work ATOMICALLY (a conditional update that stamps a lock or a
///         <c>firedAt</c>) before doing it. The double-send guard belongs in the
///         claim, not in the scheduler.</item>
///   <item>Resolve scoped services from <see cref="Services"/> inside the tick —
///         a <c>BackgroundService</c> is a singleton and cannot capture a scope.</item>
///   <item>Register with <c>services.AddKernelWorker&lt;MyWorker&gt;()</c> from your
///         slice's <c>AddXxxFeature()</c>.</item>
/// </list>
/// </summary>
public abstract class KernelPollingWorker : BackgroundService
{
    protected KernelPollingWorker(IServiceProvider services, ILogger logger)
    {
        Services = services;
        Logger = logger;
    }

    protected IServiceProvider Services { get; }

    protected ILogger Logger { get; }

    /// <summary>Poll interval. Node's workers use 5s–60s depending on how coarse the work is.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>Diagnostic label used in the tick-failure log line.</summary>
    protected abstract string WorkerName { get; }

    /// <summary>
    /// Optional startup delay, so a cold boot is not immediately hit by five
    /// workers at once.
    /// </summary>
    protected virtual TimeSpan StartupDelay => TimeSpan.Zero;

    protected abstract Task RunOnceAsync(CancellationToken cancellationToken);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (StartupDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(StartupDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // The overlap guard: awaiting the tick before waiting for the next
                // period means a slow tick delays the next one instead of racing it.
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "worker:tick-failed worker={Worker}", WorkerName);
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

public static class KernelWorkerExtensions
{
    /// <summary>
    /// Register a background worker. Call from your slice's
    /// <c>AddXxxFeature()</c> — never from <c>Program.cs</c>.
    ///
    /// <para>Workers are skipped entirely when <c>Kernel:Workers:Enabled</c> is
    /// false, which the test fixture sets so a <c>WebApplicationFactory</c> does not
    /// start five pollers per test class.</para>
    /// </summary>
    public static IServiceCollection AddKernelWorker<TWorker>(this IServiceCollection services)
        where TWorker : BackgroundService
    {
        services.AddSingleton<IHostedService>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            if (!config.GetValue("Kernel:Workers:Enabled", true))
            {
                return new DisabledWorker();
            }

            return ActivatorUtilities.CreateInstance<TWorker>(sp);
        });

        return services;
    }

    private sealed class DisabledWorker : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
