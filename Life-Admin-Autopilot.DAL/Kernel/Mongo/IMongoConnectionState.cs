using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Kernel.Mongo;

/// <summary>
/// The .NET stand-in for <c>mongoose.connection.readyState</c>, which
/// <c>routes/health.ts</c> renders through a fixed label table. Only the labels
/// below can ever reach the wire.
/// </summary>
public static class MongoConnectionLabels
{
    public const string Disconnected = "disconnected";
    public const string Connected = "connected";
    public const string Connecting = "connecting";
    public const string Disconnecting = "disconnecting";
    public const string Uninitialized = "uninitialized";
    public const string Unknown = "unknown";
}

public interface IMongoConnectionState
{
    /// <summary>One of <see cref="MongoConnectionLabels"/>.</summary>
    ValueTask<string> GetLabelAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Pings the server and caches the answer briefly. Mongoose exposes a live
/// socket flag; the driver pools connections lazily, so a ping is the closest
/// honest equivalent. The cache keeps <c>/health</c> as cheap as Node's O(1)
/// readyState read under polling.
/// </summary>
public sealed class MongoPingConnectionState : IMongoConnectionState
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);

    private readonly IMongoDatabase _database;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _cached = MongoConnectionLabels.Uninitialized;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public MongoPingConnectionState(IMongoDatabase database)
    {
        _database = database;
    }

    public async ValueTask<string> GetLabelAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _cachedAt < CacheFor)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (now - _cachedAt < CacheFor)
            {
                return _cached;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PingTimeout);
            try
            {
                await _database
                    .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: timeout.Token)
                    .ConfigureAwait(false);
                _cached = MongoConnectionLabels.Connected;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _cached = MongoConnectionLabels.Connecting;
            }
            catch (Exception)
            {
                _cached = MongoConnectionLabels.Disconnected;
            }

            _cachedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
