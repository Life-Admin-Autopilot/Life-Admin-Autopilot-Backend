using System.Threading.Channels;

namespace Life_Admin_Autopilot.DAL.Kernel.Activity;

/// <summary>The closed vocabulary of things worth watching happen.</summary>
public static class AdminActivityKind
{
    public const string Signup = "signup";
    public const string AiTurn = "ai_turn";
    public const string AiError = "ai_error";
    public const string AdminAction = "admin_action";
    public const string Notification = "notification";
    public const string Broadcast = "broadcast";
    public const string FlagToggled = "flag_toggled";
}

/// <summary>How loudly the console should render it.</summary>
public static class AdminActivitySeverity
{
    public const string Info = "info";
    public const string Notice = "notice";
    public const string Warning = "warning";
}

/// <summary>One thing that happened, as the live feed shows it.</summary>
/// <param name="Sequence">
/// Monotonic within a process lifetime. The feed uses it as a React key and to
/// drop a duplicate that arrives in both the backfill and the live stream.
/// </param>
public sealed record AdminActivityEvent(
    long Sequence,
    DateTime At,
    string Kind,
    string Summary,
    string Severity = AdminActivitySeverity.Info,
    string? Detail = null,
    string? UserId = null,
    string? Email = null);

/// <summary>
/// The live activity feed's transport.
///
/// <para>
/// <b>Deliberately in-process and lossy.</b> This is a window onto what is
/// happening right now, not a record — the record is the audit log and the usage
/// events, both of which are durable and queryable. Building it on a real broker
/// would add an operational dependency to a feature whose whole value is that it
/// costs nothing when nobody is watching.
/// </para>
///
/// <para>
/// <b>The consequence, stated plainly:</b> on a multi-instance deployment a
/// viewer sees only the events their own instance handled. The feed would need a
/// Redis fan-out to be complete, and until then the console should not present it
/// as one.
/// </para>
/// </summary>
public interface IAdminActivityBus
{
    /// <summary>
    /// Announce something. <b>Never throws and never blocks</b> — publishers are on
    /// the hot path of real requests, and a live feed is not worth slowing a
    /// customer's turn down by a microsecond, let alone failing it.
    /// </summary>
    void Publish(
        string kind,
        string summary,
        string severity = AdminActivitySeverity.Info,
        string? detail = null,
        string? userId = null,
        string? email = null);

    /// <summary>
    /// The most recent events, oldest first, so a viewer who just connected sees
    /// context rather than an empty panel until something happens.
    /// </summary>
    IReadOnlyList<AdminActivityEvent> Recent(int limit);

    /// <summary>
    /// A subscription. Disposing the returned reader's registration is handled by
    /// cancelling <paramref name="cancellationToken"/>.
    /// </summary>
    ChannelReader<AdminActivityEvent> Subscribe(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAdminActivityBus"/>
public sealed class AdminActivityBus : IAdminActivityBus
{
    /// <summary>How many events a newly-connected console can catch up on.</summary>
    public const int BacklogSize = 50;

    /// <summary>
    /// Per-subscriber queue depth. Small on purpose: a console that has fallen
    /// this far behind is not going to catch up, and the newest events are the
    /// ones worth keeping.
    /// </summary>
    public const int SubscriberCapacity = 100;

    private readonly object _gate = new();
    private readonly LinkedList<AdminActivityEvent> _backlog = new();
    private readonly List<Channel<AdminActivityEvent>> _subscribers = new();
    private readonly TimeProvider _time;

    private long _sequence;

    public AdminActivityBus(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public void Publish(
        string kind,
        string summary,
        string severity = AdminActivitySeverity.Info,
        string? detail = null,
        string? userId = null,
        string? email = null)
    {
        try
        {
            AdminActivityEvent activity;

            lock (_gate)
            {
                activity = new AdminActivityEvent(
                    ++_sequence,
                    _time.GetUtcNow().UtcDateTime,
                    kind,
                    summary,
                    severity,
                    detail,
                    userId,
                    email);

                _backlog.AddLast(activity);
                while (_backlog.Count > BacklogSize)
                {
                    _backlog.RemoveFirst();
                }

                // Writing inside the lock keeps subscriber order identical to
                // sequence order. The channels are bounded with DropOldest, so a
                // write always completes immediately and never blocks the caller.
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryWrite(activity);
                }
            }
        }
        catch
        {
            // A live feed is never worth failing a real request over. See the
            // interface summary.
        }
    }

    public IReadOnlyList<AdminActivityEvent> Recent(int limit)
    {
        lock (_gate)
        {
            var take = Math.Clamp(limit, 1, BacklogSize);
            return _backlog.Skip(Math.Max(0, _backlog.Count - take)).ToList();
        }
    }

    public ChannelReader<AdminActivityEvent> Subscribe(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<AdminActivityEvent>(
            new BoundedChannelOptions(SubscriberCapacity)
            {
                // A console that cannot keep up loses the OLDEST events, never the
                // newest. The alternative (DropWrite) would freeze the feed at the
                // moment it fell behind, which looks exactly like the product
                // going quiet.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        lock (_gate)
        {
            _subscribers.Add(channel);
        }

        cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }

            channel.Writer.TryComplete();
        });

        return channel.Reader;
    }

    /// <summary>Live subscriber count. For tests and a health line.</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }
}
