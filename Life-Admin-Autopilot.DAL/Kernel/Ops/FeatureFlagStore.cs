using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Kernel.Ops;

public static class OpsCollections
{
    public const string FeatureFlags = "adminfeatureflags";
}

/// <summary>
/// The kill switches.
///
/// <para>
/// Closed vocabulary, and every one of them is a <b>disable</b> switch rather
/// than an enable. That direction matters: a flag row that fails to load, a
/// collection that does not exist yet, and a brand-new deployment all mean "not
/// disabled", so the failure mode of this whole subsystem is the product working
/// normally rather than the product being off.
/// </para>
/// </summary>
public static class FeatureFlags
{
    /// <summary>Turns off <c>POST /ai/ask</c> and the confirm continuation.</summary>
    public const string AiChat = "ai_chat";

    /// <summary>Turns off document-scan extraction. Uploads still store.</summary>
    public const string DocumentScan = "document_scan";

    /// <summary>Turns off speech-to-text.</summary>
    public const string Transcription = "transcription";

    public static readonly IReadOnlyList<string> All = new[] { AiChat, DocumentScan, Transcription };

    public static bool IsKnown(string key) => All.Contains(key, StringComparer.Ordinal);
}

public sealed class FeatureFlagDocument
{
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId Id { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>True means the capability is OFF. See <see cref="FeatureFlags"/>.</summary>
    public bool Disabled { get; set; }

    public string? Reason { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public interface IFeatureFlagStore
{
    Task<IReadOnlyList<FeatureFlagDocument>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Is this capability switched off?
    ///
    /// <para>
    /// <b>Never throws, and answers false on any failure.</b> This sits on the hot
    /// path of the AI routes; a Mongo blip must not take the product down, and
    /// "we could not read the kill switch" is not a reason to behave as though it
    /// were pulled.
    /// </para>
    /// </summary>
    Task<bool> IsDisabledAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(
        string key,
        bool disabled,
        string? reason,
        string updatedBy,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IFeatureFlagStore"/>
public sealed class MongoFeatureFlagStore : IFeatureFlagStore
{
    /// <summary>
    /// How long a read is trusted before re-checking.
    ///
    /// <para>
    /// Without a cache this is a Mongo round trip on every AI turn. Ten seconds
    /// is short enough that pulling a switch takes effect while the person who
    /// pulled it is still looking at the screen, and long enough that the read
    /// disappears into the noise of a turn that takes four seconds anyway.
    /// </para>
    /// </summary>
    public static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(10);

    private readonly IMongoDatabase _database;
    private readonly TimeProvider _time;

    // Static so the cache is process-wide rather than per-scoped-instance — a
    // scoped cache would be rebuilt on every request and cache nothing.
    private static readonly Dictionary<string, (bool Disabled, DateTimeOffset At)> Cache = new();
    private static readonly object Gate = new();

    public MongoFeatureFlagStore(IMongoDatabase database, TimeProvider? time = null)
    {
        _database = database;
        _time = time ?? TimeProvider.System;
    }

    private IMongoCollection<FeatureFlagDocument> Collection =>
        _database.GetCollection<FeatureFlagDocument>(OpsCollections.FeatureFlags);

    public async Task<IReadOnlyList<FeatureFlagDocument>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var stored = await Collection
            .Find(Builders<FeatureFlagDocument>.Filter.Empty)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Every known flag comes back, whether or not a row exists — the console
        // has to render a switch for a capability nobody has ever toggled.
        return FeatureFlags.All
            .Select(key =>
                stored.FirstOrDefault(s => s.Key == key)
                ?? new FeatureFlagDocument { Key = key, Disabled = false })
            .ToList();
    }

    public async Task<bool> IsDisabledAsync(string key, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached) && now - cached.At < CacheWindow)
            {
                return cached.Disabled;
            }
        }

        bool disabled;

        try
        {
            var row = await Collection
                .Find(f => f.Key == key)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            disabled = row?.Disabled ?? false;
        }
        catch
        {
            // See the interface summary: unreadable is not disabled.
            return false;
        }

        lock (Gate)
        {
            Cache[key] = (disabled, now);
        }

        return disabled;
    }

    public async Task SetAsync(
        string key,
        bool disabled,
        string? reason,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        await Collection.UpdateOneAsync(
            f => f.Key == key,
            Builders<FeatureFlagDocument>.Update
                .Set(f => f.Key, key)
                .Set(f => f.Disabled, disabled)
                .Set(f => f.Reason, reason)
                .Set(f => f.UpdatedBy, updatedBy)
                .Set(f => f.UpdatedAt, now),
            new UpdateOptions { IsUpsert = true },
            cancellationToken).ConfigureAwait(false);

        // Evict rather than write-through: the console must never show a stale
        // switch to the person who just flipped it.
        lock (Gate)
        {
            Cache.Remove(key);
        }
    }
}

/// <summary>Unique key so two racing upserts cannot leave two rows for one flag.</summary>
public sealed class FeatureFlagIndexes : IMongoIndexProvider
{
    public string Name => "feature-flags";

    public Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default) =>
        database.GetCollection<BsonDocument>(OpsCollections.FeatureFlags).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument { ["key"] = 1 },
                new CreateIndexOptions { Unique = true }),
            cancellationToken: cancellationToken);
}
